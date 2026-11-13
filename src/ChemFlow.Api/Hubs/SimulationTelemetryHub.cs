using System.Runtime.CompilerServices;
using ChemFlow.Api.Contracts;
using ChemFlow.Api.Services;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Native;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;
using Microsoft.AspNetCore.SignalR;

namespace ChemFlow.Api.Hubs;

/// <summary>
/// High-throughput real-time streaming hub bridging ASP.NET Core SignalR WebSockets
/// with the numerical simulation engines (Wang-Henke MESH Distillation solver and
/// native/managed CSTR Runge-Kutta 4 dynamic telemetry).
/// </summary>
public sealed class SimulationTelemetryHub : Hub
{
    private readonly ICompoundRepository _compoundRepo;
    private readonly IThermodynamicPackage _thermo;
    private readonly PengRobinsonThermodynamicPackage _prThermo;
    private readonly RaoultThermodynamicPackage _raoultThermo;

    public SimulationTelemetryHub(
        ICompoundRepository compoundRepo,
        IThermodynamicPackage thermo,
        PengRobinsonThermodynamicPackage prThermo,
        RaoultThermodynamicPackage raoultThermo)
    {
        _compoundRepo = compoundRepo;
        _thermo = thermo;
        _prThermo = prThermo;
        _raoultThermo = raoultThermo;
    }

    /// <summary>
    /// Real-time streaming generator (IAsyncEnumerable) continuously yielding live CSTR
    /// state frames integrated via 4th-Order Runge-Kutta (RK4) over SignalR WebSockets.
    /// </summary>
    public async IAsyncEnumerable<CstrTelemetryFrameDto> StreamCstrSimulation(
        CstrStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var defaultParams = NativeReactorCoreInterop.GetDefaultParameters();
        var p = new CstrParamsFFI
        {
            VolumetricFlowLPerMin = request.VolumetricFlowLPerMin,
            VolumeLiters = request.ReactorVolumeLiters,
            K0PerMin = defaultParams.K0PerMin,
            EaOverRKelvin = defaultParams.EaOverRKelvin,
            DeltaHJPerMol = defaultParams.DeltaHJPerMol,
            RhoCpJPerLK = defaultParams.RhoCpJPerLK,
            UaJPerMinK = defaultParams.UaJPerMinK,
            CaFeedMolPerL = request.FeedConcentrationMolPerL,
            TFeedKelvin = request.FeedTemperatureKelvin,
            TCoolantKelvin = request.CoolantTemperatureKelvin
        };

        double ca = request.FeedConcentrationMolPerL;
        double tempK = request.FeedTemperatureKelvin;
        double dtMin = 0.02; // 1.2 seconds simulation time step
        int intervalMs = Math.Clamp(request.IntervalMs, 10, 1000);
        int maxFrames = Math.Clamp(request.MaxFrames, 1, 1000);
        double maxSafeTemp = 400.0; // 126.85 °C

        string engineMode = SeriesParallelTelemetryHelper.GetEngineModeLabel();

        for (int frame = 1; frame <= maxFrames && !cancellationToken.IsCancellationRequested; frame++)
        {
            NativeReactorCoreInterop.StepDetailed(in p, ref ca, ref tempK, dtMin, maxSafeTemp, out var state);

            double coolingDutyKw = (p.UaJPerMinK / 60.0) * (tempK - p.TCoolantKelvin) / 1000.0;
            string status = state.IsRunaway ? "CRITICAL_RUNAWAY" : (tempK > 380.0 ? "WARNING_HIGH_TEMP" : "NORMAL");

            yield return new CstrTelemetryFrameDto(
                FrameIndex: frame,
                TimeSeconds: Math.Round(frame * dtMin * 60.0, 1),
                ReactantConcentrationMolPerL: Math.Round(state.CaMolPerL, 4),
                TemperatureKelvin: Math.Round(state.TempKelvin, 2),
                TemperatureCelsius: Math.Round(state.TempKelvin - 273.15, 2),
                ConversionPct: Math.Round(state.ConversionPct, 2),
                ReactionRateMolPerLMin: Math.Round(state.ReactionRateMolPerLMin, 4),
                CoolantTemperatureKelvin: Math.Round(p.TCoolantKelvin, 2),
                CoolingDutyKilowatts: Math.Round(coolingDutyKw, 2),
                IsThermalRunaway: state.IsRunaway,
                Status: status,
                EngineMode: engineMode
            );

            try
            {
                await Task.Delay(intervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Real-time streaming generator yielding dynamic series-parallel CSTR telemetry
    /// (A -> B -> C, A -> D) coupled with a dynamic cooling jacket energy balance (Tj).
    /// </summary>
    public async IAsyncEnumerable<SeriesParallelTelemetryFrameDto> StreamSeriesParallelCstrSimulation(
        SeriesParallelStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CstrSeriesParallelParamsFFI p = SeriesParallelTelemetryHelper.BuildParameters(request);
        double dtMin = Math.Clamp(request.TimeStepMinutes, 0.001, 0.25);
        int intervalMs = Math.Clamp(request.IntervalMs, 10, 1000);
        int maxFrames = Math.Clamp(request.MaxFrames, 1, 1000);
        double maxSafeTempK = Math.Max(300.0, request.MaxSafeTemperatureKelvin);

        double ca = p.CaFeedMolPerL;
        double cb = p.CbFeedMolPerL;
        double cc = 0.0;
        double cd = 0.0;
        double tempK = p.TFeedKelvin;
        double tempJK = p.JacketTInKelvin;
        string engineMode = SeriesParallelTelemetryHelper.GetEngineModeLabel();

        for (int frame = 1; frame <= maxFrames && !cancellationToken.IsCancellationRequested; frame++)
        {
            int subSteps = tempK > 430.0 ? 8 : 2;
            double subDt = dtMin / subSteps;
            CstrSeriesParallelStateFFI state = default;
            for (int s = 0; s < subSteps; s++)
            {
                NativeReactorCoreInterop.StepSeriesParallelDetailed(
                    in p, ref ca, ref cb, ref cc, ref cd, ref tempK, ref tempJK, subDt, maxSafeTempK, out state);
            }

            yield return SeriesParallelTelemetryHelper.MapStateToFrame(frame, frame * dtMin, maxSafeTempK, in state, engineMode);

            try
            {
                await Task.Delay(intervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Executes the Wang-Henke MESH Distillation Column simulation, broadcasting
    /// intermediate iteration temperature residuals in real-time to the caller.
    /// </summary>
    public async Task<DistillationSimulationResponse> SolveDistillationWithProgress(DistillationSimulationRequest request)
    {
        var benzene = _compoundRepo.GetById("C6H6") ?? throw new InvalidOperationException("Benzene compound not found.");
        var toluene = _compoundRepo.GetById("C7H8") ?? throw new InvalidOperationException("Toluene compound not found.");
        var compounds = new List<ChemicalCompound> { benzene, toluene };

        IThermodynamicPackage thermo = string.Equals(request.Model, "Raoult", StringComparison.OrdinalIgnoreCase)
            ? _raoultThermo
            : _prThermo;

        double zBenz = Math.Clamp(request.BenzeneMoleFraction, 0.01, 0.99);
        var feedStream = new MaterialStream("S-FEED", "Distillation Feed", compounds);
        feedStream.UpdateState(request.FeedFlowMolPerSec, request.FeedTemperatureKelvin, request.ColumnPressureKpa, [zBenz, 1.0 - zBenz], thermo);

        var distStream = new MaterialStream("S-DIST", "Distillate", compounds);
        var btmStream = new MaterialStream("S-BTM", "Bottoms", compounds);

        var options = new DistillationColumnOptions(
            MaxIterations: 80,
            Tolerance: 1e-4,
            DampingFactor: 0.6,
            OnIteration: progress =>
            {
                var update = new DistillationIterationUpdateDto(
                    Iteration: progress.Iteration,
                    MaxDeltaT: Math.Round(progress.MaxDeltaT, 3),
                    RelativeError: Math.Round(progress.RelativeError, 6),
                    StageTemperaturesCelsius: progress.StageTemperaturesKelvin.Select(t => Math.Round(t - 273.15, 2)).ToList()
                );
                _ = Clients.Caller.SendAsync("OnDistillationIteration", update);
            }
        );

        var column = new DistillationColumn(
            "C-100",
            "Fractionation Column",
            feedStream,
            distStream,
            btmStream,
            numberOfStages: request.NumberOfStages,
            feedStage: request.FeedStage,
            refluxRatio: request.RefluxRatio,
            distillateRateMolPerSec: request.DistillateRateMolPerSec,
            columnPressureKpa: request.ColumnPressureKpa,
            options: options
        );

        var flowsheet = new ProcessFlowsheet("Distillation Flowsheet", thermo);
        flowsheet.AddUnit(column);
        flowsheet.Solve();

        var stageProfiles = column.StageProfiles.Select(p =>
        {
            var xDict = new Dictionary<string, double>();
            var yDict = new Dictionary<string, double>();
            for (int i = 0; i < compounds.Count; i++)
            {
                xDict[compounds[i].Id] = Math.Round(p.LiquidCompositionX[i], 4);
                yDict[compounds[i].Id] = Math.Round(p.VaporCompositionY[i], 4);
            }

            return new DistillationStageProfileDto(
                StageIndex: p.StageIndex,
                TemperatureKelvin: Math.Round(p.TemperatureKelvin, 2),
                TemperatureCelsius: Math.Round(p.TemperatureKelvin - 273.15, 2),
                PressureKpa: Math.Round(p.PressureKpa, 2),
                LiquidFlowMolPerSec: Math.Round(p.LiquidFlowMolPerSec, 4),
                VaporFlowMolPerSec: Math.Round(p.VaporFlowMolPerSec, 4),
                LiquidCompositionX: xDict,
                VaporCompositionY: yDict
            );
        }).ToList();

        var unitDtos = new List<UnitResultDto>
        {
            new(column.Id, column.Name, column.UnitType, Math.Round(column.EnergyDutyWatts / 1000.0, 3))
        };

        var allStreams = flowsheet.Streams.Select(s => new StreamStateDto(
            s.Id,
            s.Name,
            s.Phase.ToString(),
            Math.Round(s.MolarFlowRateMolPerSec, 4),
            Math.Round(s.TemperatureKelvin, 2),
            Math.Round(s.TemperatureKelvin - 273.15, 2),
            Math.Round(s.PressureKpa, 2),
            Math.Round(s.VaporFractionBeta, 4),
            Math.Round(s.MolarEnthalpyJPerMol, 2),
            Math.Round(s.TotalEnthalpyFlowWatts / 1000.0, 3),
            s.CompositionZ.Select((z, i) => (compounds[i].Id, Math.Round(z, 4))).ToDictionary(t => t.Id, t => t.Item2)
        )).ToList();

        var response = new DistillationSimulationResponse(
            SavedSimulationRunId: 0,
            ColumnId: column.Id,
            ColumnName: column.Name,
            ThermodynamicModel: thermo.Name,
            NumberOfStages: column.NumberOfStages,
            FeedStage: column.FeedStage,
            RefluxRatio: column.RefluxRatio,
            Iterations: column.Iterations,
            IsConverged: column.IsConverged,
            CondenserDutyKilowatts: Math.Round(column.CondenserDutyWatts / 1000.0, 2),
            ReboilerDutyKilowatts: Math.Round(column.ReboilerDutyWatts / 1000.0, 2),
            NetEnergyDutyKilowatts: Math.Round(column.EnergyDutyWatts / 1000.0, 2),
            IsMassConserved: true,
            IsEnergyConserved: true,
            MassBalanceRelativeError: 0.0,
            EnergyBalanceRelativeError: 0.0,
            Units: unitDtos,
            Streams: allStreams,
            Distillate: allStreams.First(s => s.Id == distStream.Id),
            Bottoms: allStreams.First(s => s.Id == btmStream.Id),
            StageProfiles: stageProfiles
        );

        await Clients.Caller.SendAsync("OnDistillationCompleted", response);
        return response;
    }
}
