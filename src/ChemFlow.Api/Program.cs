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
builder.Services.AddSingleton<IThermodynamicPackage, RaoultThermodynamicPackage>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

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

app.MapPost("/api/thermo/flash", (
    FlashCalculationRequest request,
    ICompoundRepository compoundRepo,
    IThermodynamicPackage thermo) =>
{
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
    IThermodynamicPackage thermo) =>
{
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
        Streams: streamDtos
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

app.Run();
