using ChemFlow.Api.Contracts;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;
using ChemFlow.Infrastructure.Persistence;
using ChemFlow.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

string connectionString = builder.Configuration.GetConnectionString("ChemFlowDb")
    ?? "Data Source=chemflow.db";

builder.Services.AddSingleton(_ =>
{
    var initializer = new SqliteDatabaseInitializer(connectionString);
    initializer.InitializeAndSeed();
    return initializer;
});

builder.Services.AddScoped<ICompoundRepository, SqliteCompoundRepository>();
builder.Services.AddScoped<ISimulationHistoryRepository, SqliteSimulationHistoryRepository>();
builder.Services.AddSingleton<RaoultThermodynamicPackage>();
builder.Services.AddSingleton<PengRobinsonThermodynamicPackage>();
builder.Services.AddSingleton<IThermodynamicPackage>(sp => sp.GetRequiredService<PengRobinsonThermodynamicPackage>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/api/compounds", (ICompoundRepository repo) =>
{
    var compounds = repo.GetAll()
        .Select(c =>
        {
            double tbKelvin = c.GetNormalBoilingPointKelvin();
            return new CompoundSummaryDto(
                c.Id,
                c.Name,
                c.Formula,
                c.MolarMass,
                c.CriticalTemperatureKelvin,
                c.CriticalPressureKpa,
                Math.Round(tbKelvin, 2),
                Math.Round(tbKelvin - 273.15, 2)
            );
        })
        .ToList();

    return Results.Ok(compounds);
})
.WithName("GetCompounds")
.WithOpenApi();

app.MapGet("/api/compounds/{id}", (string id, ICompoundRepository repo) =>
{
    ChemicalCompound? c = repo.GetById(id);
    if (c is null)
        return Results.NotFound(new { error = $"Compound '{id}' not found." });

    double tbKelvin = c.GetNormalBoilingPointKelvin();
    return Results.Ok(new CompoundSummaryDto(
        c.Id,
        c.Name,
        c.Formula,
        c.MolarMass,
        c.CriticalTemperatureKelvin,
        c.CriticalPressureKpa,
        Math.Round(tbKelvin, 2),
        Math.Round(tbKelvin - 273.15, 2)
    ));
})
.WithName("GetCompoundById")
.WithOpenApi();

app.MapGet("/api/thermo/models", () =>
{
    return Results.Ok(new ThermodynamicModelDto[]
    {
        new("peng-robinson", "Peng-Robinson EOS (1976)", "Industry-standard cubic equation of state for high-pressure non-ideal fluids and hydrocarbon systems."),
        new("raoult", "Ideal Raoult's Law", "Ideal gas and ideal solution model using pure-component Antoine vapor pressures.")
    });
})
.WithName("GetThermodynamicModels")
.WithOpenApi();

app.MapPost("/api/thermo/flash", (
    FlashCalculationRequest request,
    ICompoundRepository compoundRepo,
    RaoultThermodynamicPackage raoult,
    PengRobinsonThermodynamicPackage pengRobinson) =>
{
    IThermodynamicPackage thermo = request.Model?.ToLowerInvariant() is "raoult" or "ideal"
        ? raoult
        : pengRobinson;

    IReadOnlyList<ChemicalCompound> compounds = compoundRepo.GetByIds(request.CompoundIds);
    FlashResult result = thermo.CalculateIsothermalFlash(
        compounds,
        request.CompositionZ,
        request.TemperatureKelvin,
        request.PressureKpa);

    return Results.Ok(new FlashCalculationResponse(
        ThermodynamicModel: thermo.Name,
        Phase: result.Phase.ToString(),
        TemperatureKelvin: result.TemperatureKelvin,
        TemperatureCelsius: Math.Round(result.TemperatureKelvin - 273.15, 2),
        PressureKpa: result.PressureKpa,
        VaporFractionBeta: Math.Round(result.VaporFractionBeta, 6),
        CompoundIds: compounds.Select(c => c.Id).ToList(),
        OverallCompositionZ: result.OverallCompositionZ,
        LiquidCompositionX: result.LiquidCompositionX,
        VaporCompositionY: result.VaporCompositionY,
        KValues: result.KValues,
        MolarEnthalpyJPerMol: Math.Round(result.MolarEnthalpyJPerMol, 2),
        Iterations: result.Iterations
    ));
})
.WithName("CalculateIsothermalFlash")
.WithOpenApi();

app.MapPost("/api/thermo/txy", (
    BinaryTxyDiagramRequest request,
    ICompoundRepository compoundRepo,
    RaoultThermodynamicPackage raoult,
    PengRobinsonThermodynamicPackage pengRobinson) =>
{
    IThermodynamicPackage thermo = request.Model?.ToLowerInvariant() is "raoult" or "ideal"
        ? raoult
        : pengRobinson;

    IReadOnlyList<ChemicalCompound> binary = compoundRepo.GetByIds([request.LightCompoundId, request.HeavyCompoundId]);
    int steps = Math.Clamp(request.Steps, 2, 101);
    var points = new List<TxyDiagramPointDto>(steps);

    for (int i = 0; i < steps; i++)
    {
        double x1 = (double)i / (steps - 1);
        double[] x = [x1, 1.0 - x1];

        double tBubble = thermo.CalculateBubblePointTemperatureKelvin(binary, x, request.PressureKpa);
        double tDew = thermo.CalculateDewPointTemperatureKelvin(binary, x, request.PressureKpa);
        double[] kAtBubble = thermo.CalculateKValues(binary, x, tBubble, request.PressureKpa);
        double y1 = Math.Clamp(x1 * kAtBubble[0], 0.0, 1.0);

        points.Add(new TxyDiagramPointDto(
            MoleFractionLight: Math.Round(x1, 4),
            BubbleTemperatureKelvin: Math.Round(tBubble, 2),
            DewTemperatureKelvin: Math.Round(tDew, 2),
            EquilibriumVaporFractionLightY: Math.Round(y1, 4)
        ));
    }

    return Results.Ok(points);
})
.WithName("GenerateBinaryTxyDiagram")
.WithOpenApi();

app.MapPost("/api/simulations/benchmark", (
    BenchmarkSimulationRequest request,
    ICompoundRepository compoundRepo,
    ISimulationHistoryRepository historyRepo,
    IThermodynamicPackage thermo) =>
{
    IReadOnlyList<ChemicalCompound> compounds = compoundRepo.GetByIds(["C6H6", "C7H8"]);

    var feedBenzene = new MaterialStream(
        "S-101", "Rich Benzene Feed", compounds,
        request.BenzeneFeedFlowMolPerSec, request.BenzeneFeedTemperatureKelvin, 120.0, [0.90, 0.10]);

    var feedToluene = new MaterialStream(
        "S-102", "Rich Toluene Feed", compounds,
        request.TolueneFeedFlowMolPerSec, request.TolueneFeedTemperatureKelvin, 120.0, [0.10, 0.90]);

    var mixedStream = new MaterialStream("S-103", "Mixed Feed", compounds);
    var heatedStream = new MaterialStream("S-104", "Preheated Flash Feed", compounds);
    var vaporProduct = new MaterialStream("S-105", "Overhead Benzene-Rich Vapor", compounds);
    var liquidProduct = new MaterialStream("S-106", "Bottoms Toluene-Rich Liquid", compounds);

    var mixer = new Mixer("M-100", "Feed Mixer", [feedBenzene, feedToluene], mixedStream);
    var preheater = new HeaterCooler("E-100", "Feed Preheater", mixedStream, heatedStream, request.PreheaterTargetTemperatureKelvin, pressureDropKpa: 5.0);
    var flashDrum = new FlashDrum("V-100", "VLE Flash Separator", heatedStream, vaporProduct, liquidProduct, operatingPressureKpa: request.FlashDrumPressureKpa);

    var flowsheet = new ProcessFlowsheet("Benzene-Toluene Flash Separation Plant", thermo);
    flowsheet.AddUnit(mixer);
    flowsheet.AddUnit(preheater);
    flowsheet.AddUnit(flashDrum);

    IReadOnlyList<IUnitOperation> executedOrder = flowsheet.Solve();
    FlowsheetBalanceReport balanceReport = flowsheet.GenerateBalanceReport();
    long runId = historyRepo.SaveSimulationRun(flowsheet, executedOrder);

    var unitDtos = executedOrder
        .Select(u => new UnitResultDto(u.Id, u.Name, u.UnitType, Math.Round(u.EnergyDutyWatts / 1000.0, 3)))
        .ToList();

    var streamDtos = flowsheet.Streams
        .Select(s =>
        {
            var fractions = new Dictionary<string, double>();
            for (int i = 0; i < s.Compounds.Count; i++)
                fractions[s.Compounds[i].Id] = Math.Round(s.CompositionZ[i], 4);

            return new StreamStateDto(
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
                fractions
            );
        })
        .ToList();

    return Results.Ok(new PlantSimulationResponse(
        SavedSimulationRunId: runId,
        FlowsheetName: flowsheet.Name,
        ThermodynamicModel: thermo.Name,
        TopologicalExecutionOrder: executedOrder.Select(u => u.Id).ToList(),
        Units: unitDtos,
        Streams: streamDtos,
        IsMassConserved: balanceReport.IsMassConserved,
        IsEnergyConserved: balanceReport.IsEnergyConserved,
        MassBalanceRelativeError: Math.Round(balanceReport.GlobalBalance.RelativeMassError, 8),
        EnergyBalanceRelativeError: Math.Round(balanceReport.GlobalBalance.RelativeEnthalpyError, 8),
        TopologyUrl: $"/api/simulations/{runId}/topology",
        BalanceReportUrl: $"/api/simulations/{runId}/report"
    ));
})
.WithName("RunBenchmarkSimulation")
.WithOpenApi();

app.MapGet("/api/simulations", (ISimulationHistoryRepository historyRepo) =>
    Results.Ok(historyRepo.GetRecentRuns(20)))
.WithName("GetRecentSimulations")
.WithOpenApi();

app.MapGet("/api/simulations/{id:long}", (long id, ISimulationHistoryRepository historyRepo) =>
{
    SimulationRunRecord? run = historyRepo.GetById(id);
    return run is not null ? Results.Ok(run) : Results.NotFound(new { error = $"Simulation run {id} not found." });
})
.WithName("GetSimulationById")
.WithOpenApi();

app.MapGet("/api/simulations/{id:long}/topology", (
    long id,
    string? format,
    HttpContext httpContext,
    ISimulationHistoryRepository historyRepo) =>
{
    SimulationRunRecord? run = historyRepo.GetById(id);
    if (run is null)
        return Results.NotFound(new { error = $"Simulation run {id} not found." });

    bool wantsDot = string.Equals(format, "dot", StringComparison.OrdinalIgnoreCase)
        || httpContext.Request.Headers.Accept.Any(a => a?.Contains("text/vnd.graphviz") == true || a?.Contains("text/plain") == true);

    if (wantsDot)
    {
        if (string.IsNullOrWhiteSpace(run.TopologyDot))
            return Results.NotFound(new { error = $"Graphviz DOT topology not available for simulation run {id}." });

        return Results.Text(run.TopologyDot, "text/vnd.graphviz; charset=utf-8");
    }

    FlowsheetTopology? topology = historyRepo.GetTopology(id);
    if (topology is null)
        return Results.NotFound(new { error = $"Topology for simulation run {id} not found." });

    return Results.Ok(topology);
})
.WithName("GetSimulationTopology")
.WithOpenApi();

app.MapGet("/api/simulations/{id:long}/report", (long id, ISimulationHistoryRepository historyRepo) =>
{
    FlowsheetBalanceReport? report = historyRepo.GetBalanceReport(id);
    if (report is null)
    {
        SimulationRunRecord? run = historyRepo.GetById(id);
        if (run is null)
            return Results.NotFound(new { error = $"Simulation run {id} not found." });

        return Results.NotFound(new { error = $"Balance report for simulation run {id} not available." });
    }

    return Results.Ok(report);
})
.WithName("GetSimulationBalanceReport")
.WithOpenApi();

app.Run();

public partial class Program { }
