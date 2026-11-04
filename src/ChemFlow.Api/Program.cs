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

// 1. Configure SQLite Database & Dependency Injection (IoC Container)
string connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=chemflow.db";

builder.Services.AddSingleton(new SqliteDatabaseInitializer(connectionString));
builder.Services.AddScoped<ICompoundRepository, SqliteCompoundRepository>();
builder.Services.AddScoped<ISimulationHistoryRepository, SqliteSimulationHistoryRepository>();
builder.Services.AddSingleton<IThermodynamicPackage, RaoultThermodynamicPackage>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "ChemFlow.NET — Process Simulation & Thermodynamics Engine API",
        Version = "v1.0",
        Description = "Enterprise C# / .NET 8 REST API for multicomponent Vapor-Liquid Equilibrium (Rachford-Rice), " +
                      "Directed Acyclic Graph (DAG) flowsheet solving, and relational SQLite simulation tracking."
    });
});

var app = builder.Build();

// 2. Initialize Relational SQL Schema and Seed Default Compounds on Startup
using (var scope = app.Services.CreateScope())
{
    var dbInit = scope.ServiceProvider.GetRequiredService<SqliteDatabaseInitializer>();
    dbInit.InitializeAndSeed();
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// ============================================================================
// COMPOUNDS ENDPOINTS (/api/compounds)
// ============================================================================
var compoundsGroup = app.MapGroup("/api/compounds").WithTags("Chemical Compounds (SQL)");

compoundsGroup.MapGet("/", (ICompoundRepository repo) =>
{
    var list = repo.GetAll().Select(c =>
    {
        double tbK = c.GetNormalBoilingPointKelvin();
        return new CompoundSummaryDto(
            c.Id,
            c.Name,
            c.Formula,
            c.MolarMass,
            c.CriticalTemperatureKelvin,
            c.CriticalPressureKpa,
            Math.Round(tbK, 2),
            Math.Round(tbK - 273.15, 2)
        );
    });
    return Results.Ok(list);
})
.WithName("GetAllCompounds")
.WithOpenApi();

compoundsGroup.MapGet("/{id}", (string id, ICompoundRepository repo) =>
{
    ChemicalCompound? c = repo.GetById(id);
    if (c is null)
        return Results.NotFound(new { Error = $"Compound '{id}' was not found." });

    double tbK = c.GetNormalBoilingPointKelvin();
    return Results.Ok(new CompoundSummaryDto(
        c.Id,
        c.Name,
        c.Formula,
        c.MolarMass,
        c.CriticalTemperatureKelvin,
        c.CriticalPressureKpa,
        Math.Round(tbK, 2),
        Math.Round(tbK - 273.15, 2)
    ));
})
.WithName("GetCompoundById")
.WithOpenApi();

// ============================================================================
// THERMODYNAMICS ENDPOINTS (/api/thermo)
// ============================================================================
var thermoGroup = app.MapGroup("/api/thermo").WithTags("Thermodynamics & VLE (Rachford-Rice)");

thermoGroup.MapPost("/flash", (
    FlashCalculationRequest request,
    ICompoundRepository compoundRepo,
    IThermodynamicPackage thermo) =>
{
    try
    {
        IReadOnlyList<ChemicalCompound> compounds = compoundRepo.GetByIds(request.CompoundIds);
        FlashResult flash = thermo.CalculateIsothermalFlash(
            compounds,
            request.CompositionZ,
            request.TemperatureKelvin,
            request.PressureKpa);

        return Results.Ok(new FlashCalculationResponse(
            ThermodynamicModel: thermo.Name,
            Phase: flash.Phase.ToString(),
            TemperatureKelvin: flash.TemperatureKelvin,
            TemperatureCelsius: Math.Round(flash.TemperatureKelvin - 273.15, 2),
            PressureKpa: flash.PressureKpa,
            VaporFractionBeta: Math.Round(flash.VaporFractionBeta, 6),
            CompoundIds: compounds.Select(c => c.Id).ToList(),
            OverallCompositionZ: flash.OverallCompositionZ,
            LiquidCompositionX: flash.LiquidCompositionX.Select(x => Math.Round(x, 6)).ToList(),
            VaporCompositionY: flash.VaporCompositionY.Select(y => Math.Round(y, 6)).ToList(),
            KValues: flash.KValues.Select(k => Math.Round(k, 6)).ToList(),
            MolarEnthalpyJPerMol: Math.Round(flash.MolarEnthalpyJPerMol, 2),
            Iterations: flash.Iterations
        ));
    }
    catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
    {
        return Results.BadRequest(new { Error = ex.Message });
    }
})
.WithName("CalculateIsothermalFlash")
.WithOpenApi();

thermoGroup.MapPost("/txy-diagram", (
    BinaryTxyDiagramRequest request,
    ICompoundRepository compoundRepo,
    IThermodynamicPackage thermo) =>
{
    try
    {
        IReadOnlyList<ChemicalCompound> pair = compoundRepo.GetByIds([request.LightCompoundId, request.HeavyCompoundId]);
        int steps = Math.Clamp(request.Steps, 2, 101);
        var points = new List<TxyDiagramPointDto>(steps);

        for (int i = 0; i < steps; i++)
        {
            double x1 = (double)i / (steps - 1);
            double[] x = [x1, 1.0 - x1];

            double tBubble = thermo.CalculateBubblePointTemperatureKelvin(pair, x, request.PressureKpa);
            double tDew = thermo.CalculateDewPointTemperatureKelvin(pair, x, request.PressureKpa);
            double[] kAtBubble = thermo.CalculateKValues(pair, x, tBubble, request.PressureKpa);
            double y1 = Math.Clamp(x1 * kAtBubble[0], 0.0, 1.0);

            points.Add(new TxyDiagramPointDto(
                MoleFractionLight: Math.Round(x1, 4),
                BubbleTemperatureKelvin: Math.Round(tBubble, 3),
                DewTemperatureKelvin: Math.Round(tDew, 3),
                EquilibriumVaporFractionLightY: Math.Round(y1, 4)
            ));
        }

        return Results.Ok(points);
    }
    catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
    {
        return Results.BadRequest(new { Error = ex.Message });
    }
})
.WithName("GenerateBinaryTxyDiagram")
.WithOpenApi();

// ============================================================================
// PROCESS FLOWSHEET SIMULATION ENDPOINTS (/api/simulations)
// ============================================================================
var simGroup = app.MapGroup("/api/simulations").WithTags("Process Flowsheet DAG & SQL History");

simGroup.MapPost("/benchmark", (
    BenchmarkSimulationRequest request,
    ICompoundRepository compoundRepo,
    ISimulationHistoryRepository historyRepo,
    IThermodynamicPackage thermo) =>
{
    try
    {
        IReadOnlyList<ChemicalCompound> compounds = compoundRepo.GetByIds(["C6H6", "C7H8"]);

        var feed1 = new MaterialStream("S-101", "Benzene-Rich Feed", compounds,
            request.BenzeneFeedFlowMolPerSec, request.BenzeneFeedTemperatureKelvin, 120.0, [0.90, 0.10]);
        var feed2 = new MaterialStream("S-102", "Toluene-Rich Feed", compounds,
            request.TolueneFeedFlowMolPerSec, request.TolueneFeedTemperatureKelvin, 120.0, [0.10, 0.90]);
        var mixed = new MaterialStream("S-103", "Mixed Stream", compounds);
        var heated = new MaterialStream("S-104", "Preheated Feed", compounds);
        var vaporProduct = new MaterialStream("S-105", "Overhead Vapor Product", compounds);
        var liquidProduct = new MaterialStream("S-106", "Bottoms Liquid Product", compounds);

        double pressureDrop = Math.Max(0.0, 120.0 - request.FlashDrumPressureKpa);

        var mixer = new Mixer("MIX-101", "Adiabatic Feed Mixer", [feed1, feed2], mixed);
        var heater = new HeaterCooler("E-101", "Feed Preheater", mixed, heated,
            request.PreheaterTargetTemperatureKelvin, pressureDrop);
        var flashDrum = new FlashDrum("V-101", "VLE Flash Separator", heated, vaporProduct, liquidProduct);

        var flowsheet = new ProcessFlowsheet("Benzene-Toluene Flash Separation Plant", thermo);
        flowsheet.AddUnit(flashDrum);
        flowsheet.AddUnit(heater);
        flowsheet.AddUnit(mixer);

        IReadOnlyList<IUnitOperation> executedOrder = flowsheet.Solve();
        long savedRunId = historyRepo.SaveSimulationRun(flowsheet, executedOrder);

        var unitsDto = executedOrder.Select(u => new UnitResultDto(
            u.Id,
            u.Name,
            u.UnitType,
            Math.Round(u.EnergyDutyWatts / 1000.0, 3)
        )).ToList();

        var streamsDto = flowsheet.Streams.Select(s =>
        {
            var fractions = new Dictionary<string, double>();
            for (int i = 0; i < compounds.Count; i++)
                fractions[compounds[i].Id] = Math.Round(s.CompositionZ[i], 5);

            return new StreamStateDto(
                s.Id,
                s.Name,
                s.Phase.ToString(),
                Math.Round(s.MolarFlowRateMolPerSec, 4),
                Math.Round(s.TemperatureKelvin, 2),
                Math.Round(s.TemperatureKelvin - 273.15, 2),
                Math.Round(s.PressureKpa, 3),
                Math.Round(s.VaporFractionBeta, 5),
                Math.Round(s.MolarEnthalpyJPerMol, 2),
                Math.Round(s.TotalEnthalpyFlowWatts / 1000.0, 3),
                fractions
            );
        }).ToList();

        return Results.Ok(new PlantSimulationResponse(
            SavedSimulationRunId: savedRunId,
            FlowsheetName: flowsheet.Name,
            ThermodynamicModel: thermo.Name,
            TopologicalExecutionOrder: executedOrder.Select(u => u.Id).ToList(),
            Units: unitsDto,
            Streams: streamsDto
        ));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { Error = ex.Message });
    }
})
.WithName("RunBenchmarkPlantSimulation")
.WithOpenApi();

simGroup.MapGet("/history", (ISimulationHistoryRepository historyRepo) =>
{
    return Results.Ok(historyRepo.GetRecentRuns());
})
.WithName("GetSimulationHistory")
.WithOpenApi();

simGroup.MapGet("/history/{id:long}", (long id, ISimulationHistoryRepository historyRepo) =>
{
    SimulationRunRecord? record = historyRepo.GetById(id);
    return record is not null ? Results.Ok(record) : Results.NotFound();
})
.WithName("GetSimulationRunById")
.WithOpenApi();

app.Run();

public partial class Program { }
