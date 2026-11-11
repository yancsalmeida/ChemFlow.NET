using System.Net;
using System.Net.Http.Json;
using ChemFlow.Api.Contracts;
using ChemFlow.Domain.Flowsheet;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChemFlow.Tests;

public class SimulationApiEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SimulationApiEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task BenchmarkSimulation_ThenGetTopologyAndReport_ReturnsValidPayloadsAndBalances()
    {
        // 1. Run Benchmark Simulation
        var benchmarkRequest = new BenchmarkSimulationRequest(
            BenzeneFeedFlowMolPerSec: 50.0,
            BenzeneFeedTemperatureKelvin: 300.0,
            TolueneFeedFlowMolPerSec: 50.0,
            TolueneFeedTemperatureKelvin: 310.0,
            PreheaterTargetTemperatureKelvin: 368.15,
            FlashDrumPressureKpa: 101.325
        );

        HttpResponseMessage simResponse = await _client.PostAsJsonAsync("/api/simulations/benchmark", benchmarkRequest);
        Assert.Equal(HttpStatusCode.OK, simResponse.StatusCode);

        PlantSimulationResponse? simResult = await simResponse.Content.ReadFromJsonAsync<PlantSimulationResponse>();
        Assert.NotNull(simResult);
        Assert.True(simResult.SavedSimulationRunId > 0);
        Assert.True(simResult.IsMassConserved);
        Assert.True(simResult.IsEnergyConserved);
        Assert.NotNull(simResult.TopologyUrl);
        Assert.NotNull(simResult.BalanceReportUrl);

        long runId = simResult.SavedSimulationRunId;

        // 2. GET /api/simulations/{id}/topology (JSON)
        HttpResponseMessage topoResponse = await _client.GetAsync($"/api/simulations/{runId}/topology");
        Assert.Equal(HttpStatusCode.OK, topoResponse.StatusCode);

        FlowsheetTopology? topology = await topoResponse.Content.ReadFromJsonAsync<FlowsheetTopology>();
        Assert.NotNull(topology);
        Assert.Equal("Benzene-Toluene Flash Separation Plant", topology.FlowsheetName);
        Assert.True(topology.IsAcyclic);
        Assert.Equal(3, topology.Units.Count);
        Assert.Equal(6, topology.Streams.Count);
        Assert.Contains("E-100", topology.AdjacencyList["M-100"]);

        // 3. GET /api/simulations/{id}/topology?format=dot (Graphviz DOT)
        HttpResponseMessage dotResponse = await _client.GetAsync($"/api/simulations/{runId}/topology?format=dot");
        Assert.Equal(HttpStatusCode.OK, dotResponse.StatusCode);
        Assert.Contains("text/vnd.graphviz", dotResponse.Content.Headers.ContentType?.MediaType);

        string dotText = await dotResponse.Content.ReadAsStringAsync();
        Assert.StartsWith("digraph \"Benzene-Toluene Flash Separation Plant\"", dotText);
        Assert.Contains("rankdir=LR;", dotText);
        Assert.Contains("\"M_100\"", dotText);
        Assert.Contains("\"E_100\"", dotText);
        Assert.Contains("\"V_100\"", dotText);

        // 4. GET /api/simulations/{id}/report (JSON Mass & Energy Balance Report)
        HttpResponseMessage reportResponse = await _client.GetAsync($"/api/simulations/{runId}/report");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);

        FlowsheetBalanceReport? report = await reportResponse.Content.ReadFromJsonAsync<FlowsheetBalanceReport>();
        Assert.NotNull(report);
        Assert.True(report.IsMassConserved);
        Assert.True(report.IsEnergyConserved);
        Assert.True(report.GlobalBalance.RelativeMassError < 1e-5);
        Assert.True(report.GlobalBalance.RelativeEnthalpyError < 1e-4);
        Assert.Equal(2, report.ComponentBalances.Count);
        Assert.Equal(3, report.UnitBalances.Count);
    }

    [Fact]
    public async Task GetTopologyAndReport_WithNonExistentId_ReturnsNotFound()
    {
        // Act & Assert
        HttpResponseMessage topoResp = await _client.GetAsync("/api/simulations/99999999/topology");
        Assert.Equal(HttpStatusCode.NotFound, topoResp.StatusCode);

        HttpResponseMessage reportResp = await _client.GetAsync("/api/simulations/99999999/report");
        Assert.Equal(HttpStatusCode.NotFound, reportResp.StatusCode);
    }

    [Fact]
    public async Task RecycleSimulation_Endpoint_SolvesAndSavesRecycleTopology()
    {
        // 1. POST /api/simulations/recycle
        var request = new RecycleSimulationRequest(
            FreshFeedFlowMolPerSec: 60.0,
            FreshFeedTemperatureKelvin: 325.0,
            BenzeneMoleFraction: 0.55,
            FlashDrumTemperatureKelvin: 366.0,
            FlashDrumPressureKpa: 101.325,
            RecycleFraction: 0.60,
            Method: "Wegstein"
        );

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/simulations/recycle", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        PlantSimulationResponse? result = await response.Content.ReadFromJsonAsync<PlantSimulationResponse>();
        Assert.NotNull(result);
        Assert.True(result.SavedSimulationRunId > 0);
        Assert.True(result.HasRecycle);
        Assert.True(result.RecycleIterations > 0);
        Assert.True(result.IsMassConserved);
        Assert.True(result.IsEnergyConserved);
        Assert.Contains("S-204", result.TearStreams ?? []);

        // 2. GET /api/simulations/{id}/topology?format=dot
        HttpResponseMessage dotResp = await _client.GetAsync($"/api/simulations/{result.SavedSimulationRunId}/topology?format=dot");
        Assert.Equal(HttpStatusCode.OK, dotResp.StatusCode);
        string dotText = await dotResp.Content.ReadAsStringAsync();
        Assert.Contains("color=\"#a855f7\"", dotText);
        Assert.Contains("[Recycle/Tear]", dotText);
    }

    [Fact]
    public async Task DistillationSimulation_Endpoint_SolvesAndReturnsProfilesAndDuties()
    {
        // 1. POST /api/simulations/distillation
        var request = new DistillationSimulationRequest(
            FeedFlowMolPerSec: 50.0,
            FeedTemperatureKelvin: 340.0,
            BenzeneMoleFraction: 0.50,
            NumberOfStages: 10,
            FeedStage: 5,
            RefluxRatio: 2.0,
            DistillateRateMolPerSec: 25.0,
            ColumnPressureKpa: 101.325,
            Model: "Peng-Robinson"
        );

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/simulations/distillation", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        DistillationSimulationResponse? result = await response.Content.ReadFromJsonAsync<DistillationSimulationResponse>();
        Assert.NotNull(result);
        Assert.True(result.SavedSimulationRunId > 0);
        Assert.True(result.IsConverged);
        Assert.True(result.CondenserDutyKilowatts < 0.0);
        Assert.True(result.ReboilerDutyKilowatts > 0.0);
        Assert.True(result.IsMassConserved);
        Assert.True(result.IsEnergyConserved);
        Assert.Equal(10, result.StageProfiles.Count);
        Assert.True(result.Distillate.MoleFractions["C6H6"] > 0.85);
        Assert.True(result.Bottoms.MoleFractions["C7H8"] > 0.85);

        // 2. Verify saved flowsheet topology via GET /api/simulations/{id}/topology
        HttpResponseMessage topoResp = await _client.GetAsync($"/api/simulations/{result.SavedSimulationRunId}/topology");
        Assert.Equal(HttpStatusCode.OK, topoResp.StatusCode);
        FlowsheetTopology? topology = await topoResp.Content.ReadFromJsonAsync<FlowsheetTopology>();
        Assert.NotNull(topology);
        Assert.Equal("Fractionation Column Simulation", topology.FlowsheetName);
        Assert.Contains(topology.Units, u => u.UnitType == "DistillationColumn");
    }

    [Fact]
    public async Task FlashCalculation_Endpoint_WithIndustrialInteractionPreset_ExecutesSuccessfullyAndShiftsBeta()
    {
        var reqIdeal = new FlashCalculationRequest(
            CompoundIds: ["n-C6H14", "n-C7H16"],
            CompositionZ: [0.5, 0.5],
            TemperatureKelvin: 356.0,
            PressureKpa: 101.325,
            Model: "Peng-Robinson",
            InteractionPreset: null
        );

        var reqNonIdeal = new FlashCalculationRequest(
            CompoundIds: ["n-C6H14", "n-C7H16"],
            CompositionZ: [0.5, 0.5],
            TemperatureKelvin: 356.0,
            PressureKpa: 101.325,
            Model: "Peng-Robinson",
            InteractionPreset: "industrial"
        );

        HttpResponseMessage respIdeal = await _client.PostAsJsonAsync("/api/thermo/flash", reqIdeal);
        HttpResponseMessage respNonIdeal = await _client.PostAsJsonAsync("/api/thermo/flash", reqNonIdeal);

        Assert.Equal(HttpStatusCode.OK, respIdeal.StatusCode);
        Assert.Equal(HttpStatusCode.OK, respNonIdeal.StatusCode);

        FlashCalculationResponse? resIdeal = await respIdeal.Content.ReadFromJsonAsync<FlashCalculationResponse>();
        FlashCalculationResponse? resNonIdeal = await respNonIdeal.Content.ReadFromJsonAsync<FlashCalculationResponse>();

        Assert.NotNull(resIdeal);
        Assert.NotNull(resNonIdeal);
        Assert.Equal("VaporLiquidEquilibrium", resIdeal.Phase);
        Assert.Equal("VaporLiquidEquilibrium", resNonIdeal.Phase);
        Assert.True(resNonIdeal.VaporFractionBeta > resIdeal.VaporFractionBeta);
        Assert.True(resNonIdeal.Iterations > 0);
    }
}
