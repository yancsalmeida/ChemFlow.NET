using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ChemFlow.Api.Contracts;
using ChemFlow.Api.Services;
using ChemFlow.Domain.Control;
using ChemFlow.Domain.Native;
using ChemFlow.Infrastructure.Industrial;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChemFlow.Tests;

public class SupervisoryModbusControlTests
{
    [Fact]
    public async Task ModbusTcpClient_ExecutesRealTcpMbapRoundTrip_ForCoilsAndHoldingRegisters()
    {
        await using var simServer = new LoopbackSeriesParallelModbusServer();
        simServer.Start();

        await using var client = new ModbusTcpClient();
        await client.ConnectAsync("127.0.0.1", simServer.Port);
        Assert.True(client.IsConnected);

        await client.WriteSingleCoilAsync(6, false);
        bool[] coils = await client.ReadCoilsAsync(6, 2);
        Assert.False(coils[0]);
        Assert.False(coils[1]);

        await client.WriteSingleRegisterAsync(39, 645);
        await client.WriteSingleRegisterAsync(40, 769);
        ushort[] regs = await client.ReadHoldingRegistersAsync(39, 2);
        Assert.Equal((ushort)645, regs[0]);
        Assert.Equal((ushort)769, regs[1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReadHoldingRegistersAsync(120, 10));
    }

    [Fact]
    public async Task SupervisoryModbusControllerService_ClosesControlLoopOverRealModbusTcp_AndRejectsDisturbance()
    {
        await using var simServer = new LoopbackSeriesParallelModbusServer();
        simServer.Start();

        await using var supervisory = new SupervisoryModbusControllerService(
            new ModbusTcpClientFactory(),
            NullLogger<SupervisoryModbusControllerService>.Instance);

        SupervisoryModbusStatusResponse startStatus = await supervisory.StartControlLoopAsync(
            new SupervisoryModbusStartRequest(
                Host: "127.0.0.1",
                Port: simServer.Port,
                ScanIntervalMs: 120,
                TimeScale: 5.0,
                WeightCbEconomic: 60.0,
                MaxSafeTempC: 126.85));

        Assert.True(startStatus.IsRunning);
        Assert.False(simServer.Coils[6]); // Remote supervisory controller took ownership

        await Task.Delay(700);
        await supervisory.ApplyDisturbanceAsync(new SupervisoryDisturbanceRequest(FeedTempC: 76.85));
        await Task.Delay(700);

        SupervisoryModbusStatusResponse runningStatus = supervisory.GetStatus();
        Assert.True(runningStatus.IsRunning);
        Assert.True(runningStatus.StepsExecuted >= 5, $"Expected >= 5 SIL steps, got {runningStatus.StepsExecuted}");
        Assert.NotNull(runningStatus.LatestTick);
        Assert.InRange(runningStatus.LatestTick.PlantFeedTempC, 76.7, 77.0);
        Assert.False(runningStatus.LatestTick.PlantRunawayAlarm);
        Assert.True(runningStatus.LatestTick.PlantTempC < 126.85);
        Assert.True(simServer.RegisterWrites > 1);

        SupervisoryModbusStatusResponse stopStatus = await supervisory.StopControlLoopAsync();
        Assert.False(stopStatus.IsRunning);
    }

    [Fact]
    public async Task SupervisoryModbusControllerService_StreamsTicksToHistorianPublisher()
    {
        await using var simServer = new LoopbackSeriesParallelModbusServer();
        simServer.Start();

        var batchId = Guid.NewGuid();
        var capturedBodies = new List<string>();
        var handler = new StubHistorianHandler(batchId, capturedBodies);
        using var httpClient = new HttpClient(handler);
        var publisher = new HttpHistorianTelemetryPublisher(
            httpClient,
            NullLogger<HttpHistorianTelemetryPublisher>.Instance);

        await using var supervisory = new SupervisoryModbusControllerService(
            new ModbusTcpClientFactory(),
            NullLogger<SupervisoryModbusControllerService>.Instance,
            hubContext: null,
            historianPublisher: publisher);

        await supervisory.StartControlLoopAsync(
            new SupervisoryModbusStartRequest(
                Host: "127.0.0.1",
                Port: simServer.Port,
                ScanIntervalMs: 120,
                TimeScale: 5.0,
                PublishToHistorian: true,
                HistorianEngineUrl: "http://mock-historian:5100"));

        await Task.Delay(600);
        SupervisoryModbusStatusResponse status = await supervisory.StopControlLoopAsync();

        Assert.True(status.HistorianEnabled);
        Assert.Equal("BATCH-SIL-TEST", status.HistorianBatchCode);
        Assert.True(status.StepsExecuted >= 3, $"Expected >= 3 SIL steps, got {status.StepsExecuted}");
        Assert.True(status.HistorianMeasurementsPublished >= status.StepsExecuted * 7L);
        Assert.True(status.HistorianAlertsTriggered >= status.StepsExecuted);
        Assert.Null(status.HistorianLastError);
        Assert.NotEmpty(capturedBodies);
        Assert.Contains("\"SP-TT-201\"", capturedBodies[0]);
        Assert.Contains("\"SP-AT-201-CB\"", capturedBodies[0]);
        Assert.Contains("\"SP-TCV-201-NMPC\"", capturedBodies[0]);
    }

    private sealed class StubHistorianHandler(Guid batchId, List<string> capturedIngestBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/batches", StringComparison.Ordinal))
            {
                return JsonResp(HttpStatusCode.OK, "[]");
            }
            if (request.Method == HttpMethod.Post && path == "/api/batches")
            {
                return JsonResp(HttpStatusCode.Created, $"{{\"id\":\"{batchId}\",\"code\":\"BATCH-SIL-TEST\",\"status\":\"Scheduled\"}}");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/start", StringComparison.Ordinal))
            {
                return JsonResp(HttpStatusCode.OK, $"{{\"id\":\"{batchId}\",\"code\":\"BATCH-SIL-TEST\",\"status\":\"InProgress\"}}");
            }
            if (request.Method == HttpMethod.Post && path == "/api/telemetry/ingest")
            {
                string raw = await request.Content!.ReadAsStringAsync(cancellationToken);
                lock (capturedIngestBodies) { capturedIngestBodies.Add(raw); }
                return JsonResp(HttpStatusCode.OK, "{\"measurementsIngested\":7,\"alertsTriggered\":1}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResp(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class LoopbackSeriesParallelModbusServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptTask;

        public ushort[] HoldingRegisters { get; } = new ushort[128];
        public bool[] Coils { get; } = new bool[64];
        public int RegisterWrites { get; private set; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private double _ca = 0.48, _cb = 0.28, _cc = 0.21, _cd = 0.03;
        private double _tempK = 62.0 + 273.15, _jacketK = 36.0 + 273.15;
        private double _valvePct = 55.0;
        private double _feedTempC = 51.85;

        public LoopbackSeriesParallelModbusServer()
        {
            Coils[6] = true; // Initially embedded NMPC enabled until remote supervisor disables it
            SyncRegisters();
        }

        public void Start()
        {
            _listener.Start();
            _acceptTask = Task.Run(AcceptClientsAsync);
        }

        private void AdvancePlant()
        {
            var baseParams = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
            baseParams.TFeedKelvin = _feedTempC + SeriesParallelActuatorModel.KelvinOffset;
            baseParams.JacketTInKelvin = 21.85 + SeriesParallelActuatorModel.KelvinOffset;
            var act = SeriesParallelActuatorModel.ApplyValveCommand(in baseParams, _valvePct);
            SeriesParallelActuatorModel.StepRk4Substepped(
                in act, ref _ca, ref _cb, ref _cc, ref _cd, ref _tempK, ref _jacketK, 0.04);
            SyncRegisters();
        }

        private void SyncRegisters()
        {
            double tempC = _tempK - 273.15;
            double tjC = _jacketK - 273.15;
            double reacted = Math.Max(1e-6, 1.0 - _ca);
            double selB = Math.Clamp((_cb / reacted) * 100.0, 0.0, 100.0);

            HoldingRegisters[30] = (ushort)Math.Round(_ca * 1000);
            HoldingRegisters[31] = (ushort)Math.Round(_cb * 1000);
            HoldingRegisters[32] = (ushort)Math.Round(_cc * 1000);
            HoldingRegisters[33] = (ushort)Math.Round(_cd * 1000);
            HoldingRegisters[34] = (ushort)Math.Round(tempC * 10);
            HoldingRegisters[35] = (ushort)Math.Round(tjC * 10);
            HoldingRegisters[36] = (ushort)Math.Round(selB * 10);
            HoldingRegisters[39] = (ushort)Math.Round(_valvePct * 10);
            HoldingRegisters[40] = (ushort)Math.Round(_feedTempC * 10);
            Coils[7] = tempC > 126.85;
        }

        private async Task AcceptClientsAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => ServeClientAsync(client));
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task ServeClientAsync(TcpClient client)
        {
            await using var stream = client.GetStream();
            var header = new byte[7];

            while (!_cts.IsCancellationRequested && client.Connected)
            {
                int read = await ReadExactOrZeroAsync(stream, header, _cts.Token);
                if (read == 0) break;

                ushort txId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
                ushort pid = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                ushort len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                byte uid = header[6];

                int pduLen = len - 1;
                byte[] pdu = new byte[pduLen];
                if (await ReadExactOrZeroAsync(stream, pdu, _cts.Token) == 0) break;

                byte fc = pdu[0];
                byte[] respPdu;

                if (fc == 0x03)
                {
                    AdvancePlant();
                    ushort start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
                    ushort count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                    if (start + count <= HoldingRegisters.Length)
                    {
                        respPdu = new byte[2 + count * 2];
                        respPdu[0] = 0x03;
                        respPdu[1] = (byte)(count * 2);
                        for (int i = 0; i < count; i++)
                            BinaryPrimitives.WriteUInt16BigEndian(respPdu.AsSpan(2 + i * 2, 2), HoldingRegisters[start + i]);
                    }
                    else
                    {
                        respPdu = [0x83, 0x02];
                    }
                }
                else if (fc == 0x01)
                {
                    ushort start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
                    ushort count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                    int byteCount = (count + 7) / 8;
                    respPdu = new byte[2 + byteCount];
                    respPdu[0] = 0x01;
                    respPdu[1] = (byte)byteCount;
                    for (int i = 0; i < count; i++)
                    {
                        if (Coils[start + i]) respPdu[2 + i / 8] |= (byte)(1 << (i % 8));
                    }
                }
                else if (fc == 0x06)
                {
                    ushort addr = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
                    ushort val = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                    HoldingRegisters[addr] = val;
                    RegisterWrites++;
                    if (addr == 39) _valvePct = val / 10.0;
                    if (addr == 40) _feedTempC = val / 10.0;
                    respPdu = pdu[..5];
                }
                else if (fc == 0x05)
                {
                    ushort addr = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
                    ushort val = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                    Coils[addr] = val == 0xFF00;
                    respPdu = pdu[..5];
                }
                else
                {
                    respPdu = [(byte)(fc | 0x80), 0x01];
                }

                byte[] frame = new byte[7 + respPdu.Length];
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), txId);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), pid);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), (ushort)(1 + respPdu.Length));
                frame[6] = uid;
                Buffer.BlockCopy(respPdu, 0, frame, 7, respPdu.Length);
                await stream.WriteAsync(frame, _cts.Token);
            }
            client.Dispose();
        }

        private static async Task<int> ReadExactOrZeroAsync(NetworkStream s, byte[] buf, CancellationToken ct)
        {
            int off = 0;
            while (off < buf.Length)
            {
                int r = await s.ReadAsync(buf.AsMemory(off), ct);
                if (r == 0) return 0;
                off += r;
            }
            return off;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            if (_acceptTask is not null)
            {
                try { await _acceptTask; } catch { }
            }
            _cts.Dispose();
        }
    }
}
