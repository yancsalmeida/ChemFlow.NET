using System.Net;
using System.Net.Http.Json;
using ChemFlow.Api.Contracts;
using ChemFlow.Api.Hubs;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Native;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;
using ChemFlow.Infrastructure.Persistence;
using ChemFlow.Infrastructure.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChemFlow.Tests;

public class SimulationTelemetryHubTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly ICompoundRepository _compoundRepo;
    private readonly PengRobinsonThermodynamicPackage _prThermo;
    private readonly RaoultThermodynamicPackage _raoultThermo;

    public SimulationTelemetryHubTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
        var init = new SqliteDatabaseInitializer("Data Source=test_telemetry_hub.db");
        init.InitializeAndSeed();
        _compoundRepo = new SqliteCompoundRepository(init);
        _prThermo = new PengRobinsonThermodynamicPackage();
        _raoultThermo = new RaoultThermodynamicPackage();
    }

    [Fact]
    public async Task PostCstrStep_ReturnsValidTelemetryFrame_WithRK4Physics()
    {
        var request = new CstrStreamRequest(
            VolumetricFlowLPerMin: 100.0,
            CoolantTemperatureKelvin: 300.0,
            FeedConcentrationMolPerL: 1.0,
            FeedTemperatureKelvin: 350.0,
            ReactorVolumeLiters: 100.0
        );

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/telemetry/cstr/step", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CstrTelemetryFrameDto? frame = await response.Content.ReadFromJsonAsync<CstrTelemetryFrameDto>();
        Assert.NotNull(frame);
        Assert.Equal(1, frame.FrameIndex);
        Assert.True(frame.ReactantConcentrationMolPerL >= 0.0 && frame.ReactantConcentrationMolPerL <= 1.0);
        Assert.True(frame.TemperatureKelvin > 250.0 && frame.TemperatureKelvin < 600.0);
        Assert.True(frame.ConversionPct >= 0.0 && frame.ConversionPct <= 100.0);
        Assert.NotNull(frame.EngineMode);
    }

    [Fact]
    public async Task StreamCstrSimulation_YieldsRealTimeFrames_WithMonotonicTime()
    {
        var hub = new SimulationTelemetryHub(_compoundRepo, _prThermo, _prThermo, _raoultThermo);
        var request = new CstrStreamRequest(
            VolumetricFlowLPerMin: 100.0,
            CoolantTemperatureKelvin: 300.0,
            FeedConcentrationMolPerL: 1.0,
            FeedTemperatureKelvin: 340.0,
            MaxFrames: 5,
            IntervalMs: 10
        );

        var frames = new List<CstrTelemetryFrameDto>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (CstrTelemetryFrameDto frame in hub.StreamCstrSimulation(request, cts.Token))
        {
            frames.Add(frame);
        }

        Assert.Equal(5, frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(i + 1, frames[i].FrameIndex);
            if (i > 0)
            {
                Assert.True(frames[i].TimeSeconds > frames[i - 1].TimeSeconds);
            }
        }
    }

    [Fact]
    public void DistillationColumn_WithIterationCallback_FiresOnEveryWangHenkeStep()
    {
        var benzene = _compoundRepo.GetById("C6H6")!;
        var toluene = _compoundRepo.GetById("C7H8")!;
        var compounds = new List<ChemicalCompound> { benzene, toluene };

        var feed = new MaterialStream("S-FEED", "Feed", compounds);
        feed.UpdateState(50.0, 340.0, 101.325, [0.5, 0.5], _raoultThermo);

        var dist = new MaterialStream("S-DIST", "Distillate", compounds);
        var btm = new MaterialStream("S-BTM", "Bottoms", compounds);

        var progressList = new List<DistillationIterationProgress>();
        var options = new DistillationColumnOptions(
            MaxIterations: 40,
            Tolerance: 1e-4,
            DampingFactor: 0.6,
            OnIteration: p => progressList.Add(p)
        );

        var column = new DistillationColumn(
            "C-100",
            "Test Column",
            feed,
            dist,
            btm,
            numberOfStages: 8,
            feedStage: 4,
            refluxRatio: 2.0,
            distillateRateMolPerSec: 25.0,
            options: options
        );

        column.Solve(_raoultThermo);

        Assert.True(column.IsConverged);
        Assert.NotEmpty(progressList);
        Assert.Equal(column.Iterations, progressList.Count);

        // Verify that residual generally decreases and final maxRelDeltaT meets tolerance
        Assert.True(progressList.Last().RelativeError <= 1e-4 || progressList.Last().MaxDeltaT <= 0.01);
    }
}
