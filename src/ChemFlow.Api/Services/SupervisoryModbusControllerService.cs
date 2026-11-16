using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ChemFlow.Api.Contracts;
using ChemFlow.Api.Hubs;
using ChemFlow.Domain.Control;
using ChemFlow.Domain.Industrial;
using ChemFlow.Domain.Native;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChemFlow.Api.Services;

/// <summary>
/// Real-time Software-in-the-Loop (SIL) distributed supervisory controller.
/// Connects over industrial Modbus TCP to an external plant PLC/simulator (ProcessTwin.Py),
/// estimates unmeasured species [Ca, Cb, Cc, Cd] via C# <see cref="SeriesParallelEkf6"/> from
/// thermocouple registers only, optimizes cooling valve FCV-201 with <see cref="SeriesParallelEconomicNmpc"/>,
/// and writes valve setpoints back to the PLC over Modbus FC 0x06.
/// </summary>
public sealed class SupervisoryModbusControllerService : IHostedService, IAsyncDisposable
{
    private const int MaxHistoryCount = 180;
    private const ushort RegStartAddress = 30;
    private const ushort RegCount = 12; // HR 30..41
    private const ushort RegCoolingValve = 39;
    private const ushort RegFeedTemp = 40;
    private const ushort RegTargetCb = 41;
    private const ushort CoilSpNmpcEnable = 6;
    private const ushort CoilSpRunawayAlarm = 7;

    private readonly IModbusTcpClientFactory _clientFactory;
    private readonly IHubContext<SimulationTelemetryHub>? _hubContext;
    private readonly ILogger<SupervisoryModbusControllerService> _logger;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Queue<SupervisoryModbusTickDto> _history = new();
    private int _disposed;

    private IModbusTcpClient? _modbusClient;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    private string _host;
    private int _port;
    private int _scanIntervalMs = 500;
    private double _timeScale = 5.0;
    private double _weightCbEconomic = 60.0;
    private double _maxSafeTempC = 126.85;
    private double? _targetCbMolPerL;
    private double _maxSlewPerStepPct = 25.0;

    private bool _isRunning;
    private int _stepsExecuted;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _lastTickUtc;
    private string? _lastError;
    private SupervisoryModbusTickDto? _latestTick;

    private SeriesParallelEkf6? _ekf;
    private SeriesParallelEconomicNmpc? _nmpc;
    private double _lastValvePct = 55.0;

    public SupervisoryModbusControllerService(
        IModbusTcpClientFactory clientFactory,
        ILogger<SupervisoryModbusControllerService> logger,
        IHubContext<SimulationTelemetryHub>? hubContext = null)
    {
        _clientFactory = clientFactory;
        _logger = logger;
        _hubContext = hubContext;
        _host = Environment.GetEnvironmentVariable("PROCESSTWIN_MODBUS_HOST") ?? "127.0.0.1";
        _port = int.TryParse(Environment.GetEnvironmentVariable("PROCESSTWIN_MODBUS_PORT"), out int envPort)
            ? envPort
            : 5020;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopControlLoopAsync().ConfigureAwait(false);
    }

    public async Task<SupervisoryModbusStatusResponse> StartControlLoopAsync(
        SupervisoryModbusStartRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopControlLoopLockedAsync().ConfigureAwait(false);

            var req = request ?? new SupervisoryModbusStartRequest();
            string host = string.IsNullOrWhiteSpace(req.Host)
                ? (Environment.GetEnvironmentVariable("PROCESSTWIN_MODBUS_HOST") ?? "127.0.0.1")
                : req.Host.Trim();
            int port = req.Port is int p and > 0 and <= 65535
                ? p
                : (int.TryParse(Environment.GetEnvironmentVariable("PROCESSTWIN_MODBUS_PORT"), out int envPort) ? envPort : 5020);

            int scanMs = Math.Clamp(req.ScanIntervalMs, 100, 5000);
            double timeScale = Math.Clamp(req.TimeScale, 0.5, 30.0);
            double weightCb = Math.Clamp(req.WeightCbEconomic, 0.0, 500.0);
            double maxSafeC = Math.Clamp(req.MaxSafeTempC, 80.0, 200.0);
            double? targetCb = req.TargetCbMolPerL is double t and > 0.0 ? Math.Clamp(t, 0.01, 2.0) : null;
            double maxSlew = Math.Clamp(req.MaxSlewPerStepPct, 2.0, 100.0);

            var client = _clientFactory.CreateClient();
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

            // Take remote supervisory ownership by disabling embedded Python NMPC (Coil 6 = false)
            await client.WriteSingleCoilAsync(CoilSpNmpcEnable, false, cancellationToken).ConfigureAwait(false);

            if (req.FeedTempOverrideC is double feedOverrideC)
            {
                ushort feedReg = (ushort)Math.Clamp(Math.Round(feedOverrideC * 10.0), 100, 1800);
                await client.WriteSingleRegisterAsync(RegFeedTemp, feedReg, cancellationToken).ConfigureAwait(false);
            }

            if (targetCb is double tgt)
            {
                ushort tgtReg = (ushort)Math.Clamp(Math.Round(tgt * 1000.0), 1, 2000);
                await client.WriteSingleRegisterAsync(RegTargetCb, tgtReg, cancellationToken).ConfigureAwait(false);
            }

            ushort[] initRegs = await client.ReadHoldingRegistersAsync(RegStartAddress, RegCount, cancellationToken)
                .ConfigureAwait(false);

            double caInit = initRegs[0] / 1000.0;
            double cbInit = initRegs[1] / 1000.0;
            double ccInit = initRegs[2] / 1000.0;
            double cdInit = initRegs[3] / 1000.0;
            double tempCInit = initRegs[4] / 10.0;
            double jacketCInit = initRegs[5] / 10.0;
            double valveInit = initRegs[9] / 10.0;

            // Deliberately seed initial EKF concentrations with a realistic offset to observe live convergence
            var ekf = new SeriesParallelEkf6(
                ca0: Math.Clamp(caInit + 0.12, 0.01, 2.0),
                cb0: Math.Clamp(Math.Max(0.01, cbInit - 0.08), 0.01, 2.0),
                cc0: Math.Max(0.0, ccInit),
                cd0: Math.Max(0.0, cdInit),
                tempK0: tempCInit + SeriesParallelActuatorModel.KelvinOffset,
                jacketTempK0: jacketCInit + SeriesParallelActuatorModel.KelvinOffset);

            var nmpcCfg = new SpNmpcConfig(
                WeightCbEconomic: weightCb,
                MaxSafeTempK: maxSafeC + SeriesParallelActuatorModel.KelvinOffset,
                TargetCbMolPerL: targetCb,
                MaxSlewPerStepPct: maxSlew);

            var nmpc = new SeriesParallelEconomicNmpc(nmpcCfg, valveInit);

            lock (_stateLock)
            {
                _modbusClient = client;
                _host = host;
                _port = port;
                _scanIntervalMs = scanMs;
                _timeScale = timeScale;
                _weightCbEconomic = weightCb;
                _maxSafeTempC = maxSafeC;
                _targetCbMolPerL = targetCb;
                _maxSlewPerStepPct = maxSlew;
                _ekf = ekf;
                _nmpc = nmpc;
                _lastValvePct = valveInit;
                _stepsExecuted = 0;
                _startedAtUtc = DateTimeOffset.UtcNow;
                _lastTickUtc = null;
                _lastError = null;
                _latestTick = null;
                _history.Clear();
                _isRunning = true;
            }

            // Execute first supervisory step immediately so callers observe live actuation right away
            await ExecuteSupervisoryCycleAsync(cancellationToken).ConfigureAwait(false);

            _loopCts = new CancellationTokenSource();
            var loopCt = _loopCts.Token;
            _loopTask = Task.Run(() => RunControlLoopAsync(loopCt), CancellationToken.None);

            return GetStatus();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<SupervisoryModbusStatusResponse> ApplyDisturbanceAsync(
        SupervisoryDisturbanceRequest request,
        CancellationToken cancellationToken = default)
    {
        IModbusTcpClient? client;
        lock (_stateLock)
        {
            client = _modbusClient;
            if (request.WeightCbEconomic is double w)
                _weightCbEconomic = Math.Clamp(w, 0.0, 500.0);
            if (request.MaxSafeTempC is double tMax)
                _maxSafeTempC = Math.Clamp(tMax, 80.0, 200.0);
            if (request.ClearTargetCb)
                _targetCbMolPerL = null;
            else if (request.TargetCbMolPerL is double tgt and > 0.0)
                _targetCbMolPerL = Math.Clamp(tgt, 0.01, 2.0);

            if (_nmpc is not null)
            {
                var updatedCfg = new SpNmpcConfig(
                    WeightCbEconomic: _weightCbEconomic,
                    MaxSafeTempK: _maxSafeTempC + SeriesParallelActuatorModel.KelvinOffset,
                    TargetCbMolPerL: _targetCbMolPerL,
                    MaxSlewPerStepPct: _maxSlewPerStepPct);
                _nmpc = new SeriesParallelEconomicNmpc(updatedCfg, _lastValvePct);
            }
        }

        if (client is { IsConnected: true })
        {
            if (request.FeedTempC is double feedC)
            {
                ushort regFeed = (ushort)Math.Clamp(Math.Round(feedC * 10.0), 100, 1800);
                await client.WriteSingleRegisterAsync(RegFeedTemp, regFeed, cancellationToken).ConfigureAwait(false);
            }

            if (request.ClearTargetCb)
            {
                await client.WriteSingleRegisterAsync(RegTargetCb, 0, cancellationToken).ConfigureAwait(false);
            }
            else if (request.TargetCbMolPerL is double tgt and > 0.0)
            {
                ushort regTgt = (ushort)Math.Clamp(Math.Round(tgt * 1000.0), 1, 2000);
                await client.WriteSingleRegisterAsync(RegTargetCb, regTgt, cancellationToken).ConfigureAwait(false);
            }
        }

        return GetStatus();
    }

    public async Task<SupervisoryModbusStatusResponse> StopControlLoopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopControlLoopLockedAsync().ConfigureAwait(false);
            return GetStatus();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public SupervisoryModbusStatusResponse GetStatus()
    {
        lock (_stateLock)
        {
            return new SupervisoryModbusStatusResponse(
                IsRunning: _isRunning,
                Host: _host,
                Port: _port,
                ScanIntervalMs: _scanIntervalMs,
                TimeScale: _timeScale,
                StepsExecuted: _stepsExecuted,
                StartedAtUtc: _startedAtUtc,
                LastTickUtc: _lastTickUtc,
                LastError: _lastError,
                WeightCbEconomic: _weightCbEconomic,
                MaxSafeTempC: _maxSafeTempC,
                TargetCbMolPerL: _targetCbMolPerL,
                EngineMode: SeriesParallelTelemetryHelper.GetEngineModeLabel(),
                LatestTick: _latestTick,
                History: _history.ToArray()
            );
        }
    }

    private async Task RunControlLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_scanIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ExecuteSupervisoryCycleAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Modbus SIL supervisory cycle error on {Host}:{Port}", _host, _port);
                    lock (_stateLock)
                    {
                        _lastError = ex.Message;
                        _isRunning = false;
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteSupervisoryCycleAsync(CancellationToken ct)
    {
        IModbusTcpClient? client;
        SeriesParallelEkf6? ekf;
        SeriesParallelEconomicNmpc? nmpc;
        double lastValve;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? prevTick;
        DateTimeOffset? startedAt;
        int scanMs;
        double timeScale;
        double? configuredTargetCb;

        lock (_stateLock)
        {
            client = _modbusClient;
            ekf = _ekf;
            nmpc = _nmpc;
            lastValve = _lastValvePct;
            prevTick = _lastTickUtc;
            startedAt = _startedAtUtc;
            scanMs = _scanIntervalMs;
            timeScale = _timeScale;
            configuredTargetCb = _targetCbMolPerL;
        }

        if (client is null || ekf is null || nmpc is null)
            return;

        var modbusSw = Stopwatch.StartNew();
        ushort[] regs = await client.ReadHoldingRegistersAsync(RegStartAddress, RegCount, ct).ConfigureAwait(false);
        bool[] coils = await client.ReadCoilsAsync(CoilSpNmpcEnable, 2, ct).ConfigureAwait(false);

        double plantCa = regs[0] / 1000.0;
        double plantCb = regs[1] / 1000.0;
        double plantTempC = regs[4] / 10.0;
        double plantJacketTempC = regs[5] / 10.0;
        double plantSelectivityPct = regs[6] / 10.0;
        double plantFeedTempC = regs[10] / 10.0;
        double? plcTargetCb = regs[11] > 0 ? regs[11] / 1000.0 : configuredTargetCb;
        bool plantRunawayAlarm = coils.Length > 1 && coils[1];

        // Keep remote ownership asserted if external tool flipped Coil 6 back on
        if (coils.Length > 0 && coils[0])
        {
            await client.WriteSingleCoilAsync(CoilSpNmpcEnable, false, ct).ConfigureAwait(false);
        }

        double dtSec = prevTick.HasValue
            ? Math.Max(0.05, (now - prevTick.Value).TotalSeconds)
            : scanMs / 1000.0;
        double dtMin = Math.Clamp((dtSec * timeScale) / 60.0, 0.002, 0.25);

        var baseParams = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        baseParams.TFeedKelvin = plantFeedTempC + SeriesParallelActuatorModel.KelvinOffset;
        baseParams.JacketTInKelvin = 21.85 + SeriesParallelActuatorModel.KelvinOffset;

        // 1. EKF6 Predict + Thermocouple Measurement Update (T, Tj only)
        ekf.Predict(in baseParams, lastValve, dtMin);
        var ekfUpdate = ekf.Update(
            measuredTempK: plantTempC + SeriesParallelActuatorModel.KelvinOffset,
            measuredJacketTempK: plantJacketTempC + SeriesParallelActuatorModel.KelvinOffset);

        // 2. Economic NMPC Optimization exclusively from EKF6 state estimate
        var nmpcSw = Stopwatch.StartNew();
        if (plcTargetCb != configuredTargetCb)
        {
            var cfg = new SpNmpcConfig(
                WeightCbEconomic: _weightCbEconomic,
                MaxSafeTempK: _maxSafeTempC + SeriesParallelActuatorModel.KelvinOffset,
                TargetCbMolPerL: plcTargetCb,
                MaxSlewPerStepPct: _maxSlewPerStepPct);
            nmpc = new SeriesParallelEconomicNmpc(cfg, lastValve);
        }

        var action = nmpc.ComputeAction(
            in baseParams,
            ekfUpdate.CaMolPerL,
            ekfUpdate.CbMolPerL,
            ekfUpdate.CcMolPerL,
            ekfUpdate.CdMolPerL,
            ekfUpdate.TempKelvin,
            ekfUpdate.JacketTempKelvin,
            lastValve);
        nmpcSw.Stop();

        double cmdValvePct = Math.Round(Math.Clamp(action.ValveCommandPct, 0.0, 100.0), 2);
        ushort regValve = (ushort)Math.Clamp(Math.Round(cmdValvePct * 10.0), 0, 1000);

        // 3. Write valve command back to plant PLC over Modbus FC 0x06
        await client.WriteSingleRegisterAsync(RegCoolingValve, regValve, ct).ConfigureAwait(false);
        modbusSw.Stop();

        double elapsedSec = startedAt.HasValue ? Math.Round((now - startedAt.Value).TotalSeconds, 2) : 0.0;
        double simTimeMin = prevTick.HasValue && _latestTick is not null
            ? Math.Round(_latestTick.SimTimeMin + dtMin, 4)
            : Math.Round(dtMin, 4);

        SupervisoryModbusTickDto tick;
        lock (_stateLock)
        {
            _stepsExecuted++;
            _lastTickUtc = now;
            _lastValvePct = cmdValvePct;
            _nmpc = nmpc;
            _targetCbMolPerL = plcTargetCb;

            tick = new SupervisoryModbusTickDto(
                TimestampUtc: now,
                Step: _stepsExecuted,
                ElapsedSeconds: elapsedSec,
                SimTimeMin: simTimeMin,
                PlantTempC: Math.Round(plantTempC, 2),
                PlantJacketTempC: Math.Round(plantJacketTempC, 2),
                PlantCaMolPerL: Math.Round(plantCa, 4),
                PlantCbMolPerL: Math.Round(plantCb, 4),
                PlantSelectivityBPct: Math.Round(plantSelectivityPct, 2),
                PlantFeedTempC: Math.Round(plantFeedTempC, 2),
                PlantRunawayAlarm: plantRunawayAlarm,
                EkfCaMolPerL: Math.Round(ekfUpdate.CaMolPerL, 4),
                EkfCbMolPerL: Math.Round(ekfUpdate.CbMolPerL, 4),
                EkfTempC: Math.Round(ekfUpdate.TempKelvin - SeriesParallelActuatorModel.KelvinOffset, 2),
                EkfJacketTempC: Math.Round(ekfUpdate.JacketTempKelvin - SeriesParallelActuatorModel.KelvinOffset, 2),
                EkfNis: Math.Round(ekfUpdate.Nis, 3),
                SensorFaultDetected: ekfUpdate.SensorFaultDetected,
                CommandedValvePct: cmdValvePct,
                NmpcObjectiveCost: Math.Round(action.ObjectiveCost, 3),
                ModbusRttMs: Math.Round(modbusSw.Elapsed.TotalMilliseconds, 2),
                NmpcSolveMs: Math.Round(nmpcSw.Elapsed.TotalMilliseconds, 2),
                EngineMode: SeriesParallelTelemetryHelper.GetEngineModeLabel()
            );

            _latestTick = tick;
            _history.Enqueue(tick);
            while (_history.Count > MaxHistoryCount)
                _history.Dequeue();
        }

        if (_hubContext is not null)
        {
            await _hubContext.Clients.All.SendAsync("SupervisoryModbusTick", tick, ct).ConfigureAwait(false);
        }
    }

    private async Task StopControlLoopLockedAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            _loopCts.Dispose();
            _loopCts = null;
            _loopTask = null;
        }

        if (_modbusClient is not null)
        {
            await _modbusClient.DisposeAsync().ConfigureAwait(false);
            _modbusClient = null;
        }

        lock (_stateLock)
        {
            _isRunning = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopControlLoopLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }
}
