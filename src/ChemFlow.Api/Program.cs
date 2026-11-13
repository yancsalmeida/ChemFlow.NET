using ChemFlow.Api.Contracts;
using ChemFlow.Api.Hubs;
using ChemFlow.Api.Services;
using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Mathematics;
using ChemFlow.Domain.Native;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Transport;
using ChemFlow.Domain.Units;
using ChemFlow.Infrastructure.Persistence;
using ChemFlow.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

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
    IThermodynamicPackage thermo;
    if (request.Model?.ToLowerInvariant() is "raoult" or "ideal")
    {
        thermo = raoult;
    }
    else if (request.InteractionPreset?.ToLowerInvariant() is "industrial" or "default")
    {
        thermo = new PengRobinsonThermodynamicPackage(BinaryInteractionMatrix.CreateDefaultIndustrialMatrix());
    }
    else
    {
        thermo = pengRobinson;
    }

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

app.MapPost("/api/simulations/recycle", (
    RecycleSimulationRequest request,
    ICompoundRepository compoundRepo,
    ISimulationHistoryRepository historyRepo,
    IThermodynamicPackage thermo) =>
{
    ChemicalCompound benzene = compoundRepo.GetById("C6H6")
        ?? throw new InvalidOperationException("Compound C6H6 not found.");
    ChemicalCompound toluene = compoundRepo.GetById("C7H8")
        ?? throw new InvalidOperationException("Compound C7H8 not found.");

    IReadOnlyList<ChemicalCompound> compounds = [benzene, toluene];

    double zB = Math.Clamp(request.BenzeneMoleFraction, 0.01, 0.99);
    double[] freshZ = [zB, 1.0 - zB];

    var freshFeed = new MaterialStream(
        "S-201", "Fresh Feed", compounds,
        request.FreshFeedFlowMolPerSec, request.FreshFeedTemperatureKelvin, 120.0, freshZ);

    var recycleStream = new MaterialStream(
        "S-204", "Recycle Stream", compounds,
        molarFlowRateMolPerSec: request.FreshFeedFlowMolPerSec * 0.4,
        temperatureKelvin: request.FlashDrumTemperatureKelvin,
        pressureKpa: request.FlashDrumPressureKpa,
        compositionZ: [0.20, 0.80]);

    var mixedFeed = new MaterialStream("S-202", "Mixed Feed", compounds);
    var vaporDistillate = new MaterialStream("S-203", "Overhead Vapor Product", compounds);
    var liquidBottoms = new MaterialStream("S-BTM", "Separator Bottoms", compounds);
    var liquidPurge = new MaterialStream("S-205", "Liquid Purge Product", compounds);

    var mixer = new Mixer("M-201", "Feed & Recycle Mixer", [freshFeed, recycleStream], mixedFeed);
    var flashDrum = new FlashDrum("V-201", "VLE Separator Drum", mixedFeed, vaporDistillate, liquidBottoms,
        operatingTemperatureKelvin: request.FlashDrumTemperatureKelvin,
        operatingPressureKpa: request.FlashDrumPressureKpa);

    double recFrac = Math.Clamp(request.RecycleFraction, 0.05, 0.95);
    var splitter = new Splitter("SP-201", "Recycle-Purge Splitter", liquidBottoms,
        [recycleStream, liquidPurge], [recFrac, 1.0 - recFrac]);

    var flowsheet = new ProcessFlowsheet("Benzene-Toluene Closed-Loop Recycle Plant", thermo);
    flowsheet.AddUnit(mixer);
    flowsheet.AddUnit(flashDrum);
    flowsheet.AddUnit(splitter);
    flowsheet.AddTearStream(recycleStream);

    var method = string.Equals(request.Method, "DirectSubstitution", StringComparison.OrdinalIgnoreCase)
        ? RecycleConvergenceMethod.DirectSubstitution
        : RecycleConvergenceMethod.Wegstein;

    var solverOptions = new RecycleSolverOptions(
        Method: method,
        MaxIterations: 60,
        Tolerance: request.Tolerance
    );

    FlowsheetSolveResult solveResult = flowsheet.SolveWithRecycle(solverOptions);
    FlowsheetBalanceReport balanceReport = flowsheet.GenerateBalanceReport();
    long runId = historyRepo.SaveSimulationRun(flowsheet, solveResult.ExecutionOrder);

    var unitDtos = solveResult.ExecutionOrder
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
        TopologicalExecutionOrder: solveResult.ExecutionOrder.Select(u => u.Id).ToList(),
        Units: unitDtos,
        Streams: streamDtos,
        IsMassConserved: balanceReport.IsMassConserved,
        IsEnergyConserved: balanceReport.IsEnergyConserved,
        MassBalanceRelativeError: Math.Round(balanceReport.GlobalBalance.RelativeMassError, 8),
        EnergyBalanceRelativeError: Math.Round(balanceReport.GlobalBalance.RelativeEnthalpyError, 8),
        TopologyUrl: $"/api/simulations/{runId}/topology",
        BalanceReportUrl: $"/api/simulations/{runId}/report",
        HasRecycle: solveResult.HasRecycle,
        RecycleIterations: solveResult.Iterations,
        TearStreams: solveResult.TearStreamIds
    ));
})
.WithName("RunRecycleSimulation")
.WithOpenApi();

app.MapPost("/api/simulations/distillation", (
    DistillationSimulationRequest request,
    ICompoundRepository compoundRepo,
    ISimulationHistoryRepository historyRepo,
    PengRobinsonThermodynamicPackage prPackage,
    RaoultThermodynamicPackage raoultPackage) =>
{
    IThermodynamicPackage thermo = string.Equals(request.Model, "Raoult", StringComparison.OrdinalIgnoreCase)
        ? raoultPackage
        : prPackage;

    ChemicalCompound benzene = compoundRepo.GetById("C6H6")
        ?? throw new InvalidOperationException("Compound C6H6 not found.");
    ChemicalCompound toluene = compoundRepo.GetById("C7H8")
        ?? throw new InvalidOperationException("Compound C7H8 not found.");

    IReadOnlyList<ChemicalCompound> compounds = [benzene, toluene];

    double zB = Math.Clamp(request.BenzeneMoleFraction, 0.01, 0.99);
    double zT = 1.0 - zB;

    var feedStream = new MaterialStream(
        "S-FEED",
        "Distillation Feed",
        compounds,
        request.FeedFlowMolPerSec,
        request.FeedTemperatureKelvin,
        request.ColumnPressureKpa,
        [zB, zT]
    );

    var distillate = new MaterialStream("S-DIST", "Distillate Product", compounds);
    var bottoms = new MaterialStream("S-BTM", "Bottoms Residue", compounds);

    var column = new DistillationColumn(
        id: "C-100",
        name: "Fractionation Column",
        feedStream: feedStream,
        distillateOutlet: distillate,
        bottomsOutlet: bottoms,
        numberOfStages: request.NumberOfStages,
        feedStage: request.FeedStage,
        refluxRatio: request.RefluxRatio,
        distillateRateMolPerSec: request.DistillateRateMolPerSec,
        columnPressureKpa: request.ColumnPressureKpa
    );

    var flowsheet = new ProcessFlowsheet("Fractionation Column Simulation", thermo);
    flowsheet.AddUnit(column);

    IReadOnlyList<IUnitOperation> executedOrder = flowsheet.Solve();
    FlowsheetBalanceReport balanceReport = flowsheet.GenerateBalanceReport();
    long runId = historyRepo.SaveSimulationRun(flowsheet, executedOrder);

    static StreamStateDto MapStream(MaterialStream s)
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
    }

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

    var unitDtos = executedOrder
        .Select(u => new UnitResultDto(u.Id, u.Name, u.UnitType, Math.Round(u.EnergyDutyWatts / 1000.0, 3)))
        .ToList();

    var allStreams = flowsheet.Streams.Select(MapStream).ToList();

    return Results.Ok(new DistillationSimulationResponse(
        SavedSimulationRunId: runId,
        ColumnId: column.Id,
        ColumnName: column.Name,
        ThermodynamicModel: thermo.Name,
        NumberOfStages: column.NumberOfStages,
        FeedStage: column.FeedStage,
        RefluxRatio: column.RefluxRatio,
        Iterations: column.Iterations,
        IsConverged: column.IsConverged,
        CondenserDutyKilowatts: Math.Round(column.CondenserDutyWatts / 1000.0, 3),
        ReboilerDutyKilowatts: Math.Round(column.ReboilerDutyWatts / 1000.0, 3),
        NetEnergyDutyKilowatts: Math.Round(column.EnergyDutyWatts / 1000.0, 3),
        IsMassConserved: balanceReport.IsMassConserved,
        IsEnergyConserved: balanceReport.IsEnergyConserved,
        MassBalanceRelativeError: Math.Round(balanceReport.GlobalBalance.RelativeMassError, 8),
        EnergyBalanceRelativeError: Math.Round(balanceReport.GlobalBalance.RelativeEnthalpyError, 8),
        Units: unitDtos,
        Streams: allStreams,
        Distillate: MapStream(distillate),
        Bottoms: MapStream(bottoms),
        StageProfiles: stageProfiles,
        TopologyUrl: $"/api/simulations/{runId}/topology",
        BalanceReportUrl: $"/api/simulations/{runId}/report"
    ));
})
.WithName("RunDistillationSimulation")
.WithOpenApi();

app.MapPost("/api/simulations/shortcut-distillation", (
    ShortcutDistillationRequest request,
    ICompoundRepository compoundRepo,
    PengRobinsonThermodynamicPackage prPackage,
    RaoultThermodynamicPackage raoultPackage) =>
{
    IThermodynamicPackage thermo = string.Equals(request.Model, "Peng-Robinson", StringComparison.OrdinalIgnoreCase)
        ? prPackage
        : raoultPackage;

    List<ChemicalCompound> compounds = [];
    if (request.CompoundIds is { Count: >= 2 } ids)
    {
        foreach (string id in ids)
        {
            ChemicalCompound? c = compoundRepo.GetById(id);
            if (c != null) compounds.Add(c);
        }
    }

    if (compounds.Count < 2)
    {
        compounds = [
            compoundRepo.GetById("C6H6") ?? DefaultCompounds.Benzene,
            compoundRepo.GetById("C7H8") ?? DefaultCompounds.Toluene
        ];
    }

    int nComp = compounds.Count;
    double[] zFeed = new double[nComp];
    if (request.FeedMoleFractions is { Count: > 0 } fractions && fractions.Count == nComp)
    {
        double sum = fractions.Sum();
        for (int i = 0; i < nComp; i++)
            zFeed[i] = sum > 0.0 ? fractions[i] / sum : 1.0 / nComp;
    }
    else
    {
        Array.Fill(zFeed, 1.0 / nComp);
    }

    int lkIdx = Math.Clamp(request.LightKeyIndex, 0, nComp - 1);
    int hkIdx = Math.Clamp(request.HeavyKeyIndex, 0, nComp - 1);
    if (lkIdx == hkIdx)
    {
        if (hkIdx < nComp - 1) hkIdx++;
        else lkIdx = 0;
    }

    var feedStream = new MaterialStream(
        "S-FEED",
        "Shortcut Feed",
        compounds,
        request.FeedFlowMolPerSec,
        request.FeedTemperatureKelvin,
        request.ColumnPressureKpa,
        zFeed
    );
    feedStream.SolveEquilibrium(thermo);

    var distillate = new MaterialStream("S-DIST", "Shortcut Distillate", compounds);
    var bottoms = new MaterialStream("S-BTM", "Shortcut Bottoms", compounds);

    var correlation = string.Equals(request.Correlation, "Eduljee", StringComparison.OrdinalIgnoreCase)
        ? GillilandCorrelationType.Eduljee
        : GillilandCorrelationType.Molokanov;

    var shortcut = new ShortcutDistillationColumn(
        id: "C-FUG",
        name: "Shortcut Distillation Column (FUG)",
        feedStream: feedStream,
        distillateOutlet: distillate,
        bottomsOutlet: bottoms,
        lightKeyIndex: lkIdx,
        heavyKeyIndex: hkIdx,
        lightKeyRecoveryDistillate: request.LightKeyRecoveryDistillate,
        heavyKeyRecoveryBottoms: request.HeavyKeyRecoveryBottoms,
        refluxRatioMultiplier: request.RefluxRatioMultiplier,
        columnPressureKpa: request.ColumnPressureKpa,
        correlation: correlation
    );

    shortcut.Solve(thermo);
    FugDesignResult res = shortcut.DesignResult!;

    var distDict = new Dictionary<string, double>();
    var btmDict = new Dictionary<string, double>();
    var alphaDict = new Dictionary<string, double>();

    for (int i = 0; i < nComp; i++)
    {
        string cId = compounds[i].Id;
        distDict[cId] = Math.Round(res.DistillateCompositionX[i], 4);
        btmDict[cId] = Math.Round(res.BottomsCompositionX[i], 4);
        alphaDict[cId] = Math.Round(res.RelativeVolatilities[i], 4);
    }

    return Results.Ok(new ShortcutDistillationResponse(
        ColumnId: shortcut.Id,
        ColumnName: shortcut.Name,
        ThermodynamicModel: thermo.Name,
        MinimumStagesNmin: Math.Round(res.MinimumStagesNmin, 2),
        MinimumRefluxRatioRmin: Math.Round(res.MinimumRefluxRatioRmin, 3),
        OperatingRefluxRatio: Math.Round(res.OperatingRefluxRatio, 3),
        TheoreticalStagesN: Math.Round(res.TheoreticalStagesN, 2),
        RecommendedNumberOfStages: res.RecommendedNumberOfStages,
        OptimalFeedStage: res.OptimalFeedStage,
        RectifyingStagesNr: Math.Round(res.RectifyingStagesNr, 2),
        StrippingStagesNs: Math.Round(res.StrippingStagesNs, 2),
        UnderwoodTheta: Math.Round(res.UnderwoodTheta, 4),
        DistillateFlowMolPerSec: Math.Round(res.DistillateRateMolPerSec, 4),
        BottomsFlowMolPerSec: Math.Round(res.BottomsRateMolPerSec, 4),
        TopTemperatureCelsius: Math.Round(res.TopTemperatureKelvin - 273.15, 2),
        BottomTemperatureCelsius: Math.Round(res.BottomTemperatureKelvin - 273.15, 2),
        CondenserDutyKilowatts: Math.Round(res.CondenserDutyWatts / 1000.0, 3),
        ReboilerDutyKilowatts: Math.Round(res.ReboilerDutyWatts / 1000.0, 3),
        EstimatedDiameterMeters: Math.Round(res.EstimatedDiameterMeters, 2),
        DistillateComposition: distDict,
        BottomsComposition: btmDict,
        RelativeVolatilities: alphaDict
    ));
})
.WithName("RunShortcutDistillation")
.WithOpenApi();

app.MapPost("/api/simulations/series-parallel-plant", (
    SeriesParallelPlantSimulationRequest request,
    ICompoundRepository compoundRepo,
    ISimulationHistoryRepository historyRepo,
    PengRobinsonThermodynamicPackage prPackage,
    RaoultThermodynamicPackage raoultPackage) =>
{
    IThermodynamicPackage thermo = string.Equals(request.Model, "PengRobinson", StringComparison.OrdinalIgnoreCase)
        ? prPackage
        : raoultPackage;

    ChemicalCompound pentane = compoundRepo.GetById("n-C5H12")
        ?? throw new InvalidOperationException("Compound n-C5H12 not found.");
    ChemicalCompound benzene = compoundRepo.GetById("C6H6")
        ?? throw new InvalidOperationException("Compound C6H6 not found.");
    ChemicalCompound toluene = compoundRepo.GetById("C7H8")
        ?? throw new InvalidOperationException("Compound C7H8 not found.");
    ChemicalCompound pXylene = compoundRepo.GetById("p-C8H10")
        ?? throw new InvalidOperationException("Compound p-C8H10 not found.");

    IReadOnlyList<ChemicalCompound> compounds = [pentane, benzene, toluene, pXylene];

    double freshFlow = Math.Clamp(request.FreshFeedFlowMolPerSec, 1.0, 200.0);
    double freshTempK = Math.Clamp(request.FreshFeedTemperatureKelvin, 280.0, 450.0);
    double pressureKpa = Math.Clamp(request.ColumnPressureKpa, 50.0, 1000.0);
    double recycleFrac = Math.Clamp(request.RecycleFraction, 0.05, 0.95);

    var freshFeed = new MaterialStream(
        "S-FEED",
        "Fresh Pentane Feed",
        compounds,
        molarFlowRateMolPerSec: freshFlow,
        temperatureKelvin: freshTempK,
        pressureKpa: pressureKpa,
        compositionZ: [0.97, 0.03, 0.0, 0.0]);

    var recycleTear = new MaterialStream(
        "S-RECYCLE",
        "Reactant A Recycle",
        compounds,
        molarFlowRateMolPerSec: Math.Round(freshFlow * 0.25, 4),
        temperatureKelvin: 330.0,
        pressureKpa: pressureKpa,
        compositionZ: [0.95, 0.05, 0.0, 0.0]);

    var reactorFeed = new MaterialStream("S-RXN-IN", "Mixed Reactor Feed", compounds);
    var reactorOut = new MaterialStream("S-RXN-OUT", "Reactor Effluent", compounds);
    var distRecoverA = new MaterialStream("S-DIST-A", "Recovered Reactant Overhead", compounds);
    var crudeProducts = new MaterialStream("S-CRUDE-PROD", "Crude Product Bottoms", compounds);
    var productB = new MaterialStream("S-PROD-B", "Purified Benzene Product", compounds);
    var heaviesOut = new MaterialStream("S-HEAVIES", "Heavy Byproducts C+D", compounds);
    var purgeOut = new MaterialStream("S-PURGE", "Light Purge", compounds);

    var mixer = new Mixer("MIX-101", "Feed-Recycle Mixer", [freshFeed, recycleTear], reactorFeed);

    bool useHighExotherm = request.UseHighExothermKinetics;
    var reactor = new SeriesParallelContinuousStirredTankReactor(
        id: "R-101",
        name: "Series-Parallel CSTR",
        inletStream: reactorFeed,
        outletStream: reactorOut,
        reactantACompoundId: "n-C5H12",
        productBCompoundId: "C6H6",
        byproductCCompoundId: "C7H8",
        byproductDCompoundId: "p-C8H10",
        reactorVolumeLiters: Math.Clamp(request.ReactorVolumeLiters, 5.0, 5000.0),
        rhoCpJPerLK: 2200.0,
        k0_1PerMin: useHighExotherm ? null : 2.0e6,
        ea1OverRKelvin: useHighExotherm ? null : 5000.0,
        deltaH1JPerMol: useHighExotherm ? null : 8000.0,
        k0_2PerMin: useHighExotherm ? null : 3.5e5,
        ea2OverRKelvin: useHighExotherm ? null : 5200.0,
        deltaH2JPerMol: useHighExotherm ? null : 6000.0,
        k0_3PerMin: useHighExotherm ? null : 1.5e5,
        ea3OverRKelvin: useHighExotherm ? null : 5400.0,
        deltaH3JPerMol: useHighExotherm ? null : 7000.0,
        coolingUaWattsPerKelvin: Math.Clamp(request.CoolingUaWattsPerKelvin, 0.0, 100000.0),
        jacketInletTemperatureKelvin: Math.Clamp(request.JacketInletTemperatureKelvin, 250.0, 450.0),
        maxSafeTemperatureKelvin: Math.Clamp(request.MaxSafeTemperatureKelvin, 350.0, 650.0));

    var colRecoverA = new ShortcutDistillationColumn(
        id: "C-101",
        name: "Reactant A Recovery Column",
        feedStream: reactorOut,
        distillateOutlet: distRecoverA,
        bottomsOutlet: crudeProducts,
        lightKeyIndex: 0,
        heavyKeyIndex: 1,
        lightKeyRecoveryDistillate: 0.96,
        heavyKeyRecoveryBottoms: 0.96,
        refluxRatioMultiplier: 1.25,
        columnPressureKpa: pressureKpa);

    var colPurifyB = new ShortcutDistillationColumn(
        id: "C-102",
        name: "Benzene Product Column",
        feedStream: crudeProducts,
        distillateOutlet: productB,
        bottomsOutlet: heaviesOut,
        lightKeyIndex: 1,
        heavyKeyIndex: 2,
        lightKeyRecoveryDistillate: 0.96,
        heavyKeyRecoveryBottoms: 0.96,
        refluxRatioMultiplier: 1.25,
        columnPressureKpa: pressureKpa);

    var splitter = new Splitter(
        id: "SPL-101",
        name: "Recycle/Purge Splitter",
        inletStream: distRecoverA,
        outletStreams: [recycleTear, purgeOut],
        splitFractions: [recycleFrac, 1.0 - recycleFrac]);

    var flowsheet = new ProcessFlowsheet("Series-Parallel CSTR Recycle Plant", thermo);
    flowsheet.AddUnit(mixer);
    flowsheet.AddUnit(reactor);
    flowsheet.AddUnit(colRecoverA);
    flowsheet.AddUnit(colPurifyB);
    flowsheet.AddUnit(splitter);

    RecycleConvergenceMethod method = request.Method.Trim().ToUpperInvariant() switch
    {
        "DIRECT" or "DIRECTSUBSTITUTION" => RecycleConvergenceMethod.DirectSubstitution,
        "BROYDEN" => RecycleConvergenceMethod.Broyden,
        _ => RecycleConvergenceMethod.Wegstein
    };

    FlowsheetSolveResult solveResult = flowsheet.SolveWithRecycle(
        new RecycleSolverOptions(
            Method: method,
            MaxIterations: 60,
            Tolerance: Math.Clamp(request.Tolerance, 1e-8, 1e-2)));

    FlowsheetBalanceReport balanceReport = flowsheet.GenerateBalanceReport();
    long runId = historyRepo.SaveSimulationRun(flowsheet, solveResult.ExecutionOrder);

    double freshA = freshFeed.GetComponentMolarFlowMolPerSec(0);
    double exitA = purgeOut.GetComponentMolarFlowMolPerSec(0)
                 + productB.GetComponentMolarFlowMolPerSec(0)
                 + heaviesOut.GetComponentMolarFlowMolPerSec(0);
    double overallConvA = freshA > 1e-12 ? Math.Clamp((freshA - exitA) / freshA * 100.0, 0.0, 100.0) : 0.0;

    var unitDtos = solveResult.ExecutionOrder
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
                fractions);
        })
        .ToList();

    return Results.Ok(new SeriesParallelPlantSimulationResponse(
        SavedSimulationRunId: runId,
        FlowsheetName: flowsheet.Name,
        ThermodynamicModel: thermo.Name,
        TopologicalExecutionOrder: solveResult.ExecutionOrder.Select(u => u.Id).ToList(),
        Units: unitDtos,
        Streams: streamDtos,
        IsMassConserved: balanceReport.IsMassConserved,
        IsEnergyConserved: balanceReport.IsEnergyConserved,
        MassBalanceRelativeError: Math.Round(balanceReport.GlobalBalance.RelativeMassError, 8),
        EnergyBalanceRelativeError: Math.Round(balanceReport.GlobalBalance.RelativeEnthalpyError, 8),
        TopologyUrl: $"/api/simulations/{runId}/topology",
        BalanceReportUrl: $"/api/simulations/{runId}/report",
        HasRecycle: solveResult.HasRecycle,
        RecycleIterations: solveResult.Iterations,
        TearStreams: solveResult.TearStreamIds,
        SinglePassConversionAPercent: Math.Round(reactor.ConversionAPercent, 2),
        OverallPlantConversionAPercent: Math.Round(overallConvA, 2),
        YieldBPercent: Math.Round(reactor.YieldBPercent, 2),
        SelectivityBPercent: Math.Round(reactor.SelectivityBPercent, 2),
        ReactorTemperatureKelvin: Math.Round(reactor.OutletTemperatureKelvin, 2),
        ReactorTemperatureCelsius: Math.Round(reactor.OutletTemperatureKelvin - 273.15, 2),
        JacketOutletTemperatureKelvin: Math.Round(reactor.JacketOutletTemperatureKelvin, 2),
        JacketOutletTemperatureCelsius: Math.Round(reactor.JacketOutletTemperatureKelvin - 273.15, 2),
        OutletCaMolPerL: Math.Round(reactor.OutletCaMolPerL, 4),
        OutletCbMolPerL: Math.Round(reactor.OutletCbMolPerL, 4),
        OutletCcMolPerL: Math.Round(reactor.OutletCcMolPerL, 4),
        OutletCdMolPerL: Math.Round(reactor.OutletCdMolPerL, 4),
        HeatGeneratedWatts: Math.Round(reactor.HeatGeneratedWatts, 1),
        HeatRemovedWatts: Math.Round(reactor.HeatRemovedWatts, 1),
        IsThermalRunaway: reactor.IsThermalRunaway,
        IsUsingNativeCore: reactor.IsUsingNativeCore,
        ProductBPurityMoleFraction: Math.Round(productB.CompositionZ[1], 4),
        PurifiedBenzeneFlowMolPerSec: Math.Round(productB.GetComponentMolarFlowMolPerSec(1), 4)
    ));
})
.WithName("RunSeriesParallelPlant")
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

// ==============================================================================
// SignalR Real-Time Streaming Hub & CSTR Telemetry Endpoints
// ==============================================================================
app.MapHub<SimulationTelemetryHub>("/hubs/simulation");

app.MapPost("/api/telemetry/cstr/step", (CstrStreamRequest request) =>
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
    NativeReactorCoreInterop.StepDetailed(in p, ref ca, ref tempK, 0.02, 400.0, out var state);
    double coolingDutyKw = (p.UaJPerMinK / 60.0) * (tempK - p.TCoolantKelvin) / 1000.0;

    return Results.Ok(new CstrTelemetryFrameDto(
        FrameIndex: 1,
        TimeSeconds: 1.2,
        ReactantConcentrationMolPerL: Math.Round(state.CaMolPerL, 4),
        TemperatureKelvin: Math.Round(state.TempKelvin, 2),
        TemperatureCelsius: Math.Round(state.TempKelvin - 273.15, 2),
        ConversionPct: Math.Round(state.ConversionPct, 2),
        ReactionRateMolPerLMin: Math.Round(state.ReactionRateMolPerLMin, 4),
        CoolantTemperatureKelvin: Math.Round(p.TCoolantKelvin, 2),
        CoolingDutyKilowatts: Math.Round(coolingDutyKw, 2),
        IsThermalRunaway: state.IsRunaway,
        Status: state.IsRunaway ? "CRITICAL_RUNAWAY" : (state.TempKelvin > 380.0 ? "WARNING_HIGH_TEMP" : "NORMAL"),
        EngineMode: NativeReactorCoreInterop.IsNativeAvailable
            ? $"Native C11 ({NativeReactorCoreInterop.VersionString})"
            : "Managed C# RK4"
    ));
})
.WithName("StepCstrSimulation")
.WithOpenApi();

app.MapPost("/api/transport/properties", (
    TransportPropertiesRequest request,
    ICompoundRepository compoundRepo,
    PengRobinsonThermodynamicPackage prPkg) =>
{
    var compounds = request.CompoundIds.Select(id => compoundRepo.GetById(id)
        ?? throw new InvalidOperationException($"Compound '{id}' not found.")).ToList();

    ITransportPropertiesPackage pkg = string.Equals(request.Package, "Ely-Hanley", StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.Package, "TRAPP", StringComparison.OrdinalIgnoreCase)
        ? new ElyHanleyTransportPackage()
        : new ChungTransportPackage();

    var stream = new MaterialStream("TEMP-STREAM", "Stream for Transport Evaluation", compounds);
    stream.UpdateState(
        molarFlowRateMolPerSec: 10.0,
        temperatureKelvin: request.TemperatureKelvin,
        pressureKpa: request.PressureKpa,
        compositionZ: request.CompositionZ,
        thermo: prPkg
    );

    var res = stream.GetTransportProperties(pkg);

    return Results.Ok(new TransportPropertiesResponse(
        ModelName: res.ModelName,
        Phase: res.Phase.ToString(),
        TemperatureKelvin: Math.Round(res.TemperatureKelvin, 2),
        TemperatureCelsius: Math.Round(res.TemperatureCelsius, 2),
        PressureKpa: Math.Round(res.PressureKpa, 2),
        DynamicViscosityPaS: res.DynamicViscosityPaS,
        DynamicViscosityCp: Math.Round(res.DynamicViscosityCp, 4),
        KinematicViscosityM2PerS: res.KinematicViscosityM2PerS,
        KinematicViscosityCSt: Math.Round(res.KinematicViscosityCSt, 4),
        ThermalConductivityWPerMK: Math.Round(res.ThermalConductivityWPerMK, 5),
        DensityKgPerM3: Math.Round(res.DensityKgPerM3, 2),
        PrandtlNumber: Math.Round(res.PrandtlNumber, 3)
    ));
})
.WithName("CalculateTransportProperties")
.WithOpenApi();

app.MapPost("/api/telemetry/cstr/battery", (
    CstrBatterySimulationRequest request,
    ICompoundRepository compoundRepo,
    PengRobinsonThermodynamicPackage prPkg) =>
{
    var reactant = compoundRepo.GetById(request.ReactantCompoundId)
        ?? throw new InvalidOperationException($"Compound '{request.ReactantCompoundId}' not found.");
    var product = compoundRepo.GetById(request.ProductCompoundId)
        ?? throw new InvalidOperationException($"Compound '{request.ProductCompoundId}' not found.");

    var compounds = new List<ChemicalCompound> { reactant, product };
    var composition = request.CompositionZ ?? new[] { 0.80, 0.20 };

    var sIn = new MaterialStream("FEED-BATTERY", "Feed to CSTR Battery", compounds);
    sIn.UpdateState(
        molarFlowRateMolPerSec: request.TotalFeedFlowMolPerSec,
        temperatureKelvin: request.FeedTemperatureKelvin,
        pressureKpa: request.FeedPressureKpa,
        compositionZ: composition,
        thermo: prPkg
    );

    var sOut = new MaterialStream("EFF-BATTERY", "Effluent from CSTR Battery", compounds);

    ITransportPropertiesPackage transportPkg = string.Equals(request.TransportPackage, "Ely-Hanley", StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.TransportPackage, "TRAPP", StringComparison.OrdinalIgnoreCase)
        ? new ElyHanleyTransportPackage()
        : new ChungTransportPackage();

    var battery = new ContinuousStirredTankReactorBattery(
        id: "CSTR-BATTERY-SIM",
        name: $"Parallel CSTR Battery ({request.ReactorCount} Reactors)",
        inletStream: sIn,
        outletStream: sOut,
        reactorCount: request.ReactorCount,
        reactantCompoundId: request.ReactantCompoundId,
        productCompoundId: request.ProductCompoundId,
        reactorVolumeLiters: request.ReactorVolumeLiters,
        coolantTemperatureKelvin: request.CoolantTemperatureKelvin,
        nominalHeatTransferUaWattsPerKelvin: request.CoolingUaWattsPerKelvin,
        maxSafeTemperatureKelvin: request.MaxSafeTemperatureKelvin,
        transportPackage: transportPkg
    );

    battery.Solve(prPkg);

    var reactorStatesDto = new List<CstrBatteryReactorStateDto>(request.ReactorCount);
    for (int i = 0; i < request.ReactorCount; i++)
    {
        var s = battery.ReactorStates[i];
        var tp = battery.ReactorTransportProperties[i];
        reactorStatesDto.Add(new CstrBatteryReactorStateDto(
            ReactorIndex: i,
            ReactantConcentrationMolPerL: Math.Round(s.CaMolPerL, 4),
            TemperatureKelvin: Math.Round(s.TempKelvin, 2),
            TemperatureCelsius: Math.Round(s.TempKelvin - 273.15, 2),
            ConversionPct: Math.Round(s.ConversionPct, 2),
            ReactionRateMolPerLMin: Math.Round(s.ReactionRateMolPerLMin, 4),
            IsRunaway: s.IsRunaway,
            DynamicViscosityCp: Math.Round(tp.DynamicViscosityCp, 4),
            ThermalConductivityWPerMK: Math.Round(tp.ThermalConductivityWPerMK, 5),
            PrandtlNumber: Math.Round(tp.PrandtlNumber, 3)
        ));
    }

    return Results.Ok(new CstrBatterySimulationResponse(
        BatteryId: battery.Id,
        Name: battery.Name,
        ReactorCount: request.ReactorCount,
        MeanConversionPct: Math.Round(battery.MeanConversion, 2),
        MeanTemperatureKelvin: Math.Round(battery.MeanTemperatureKelvin, 2),
        MeanTemperatureCelsius: Math.Round(battery.MeanTemperatureKelvin - 273.15, 2),
        MeanOutletConcentrationMolPerL: Math.Round(battery.MeanOutletConcentrationMolPerL, 4),
        HasThermalRunaway: battery.HasThermalRunaway,
        IsUsingNativeSimd: battery.IsUsingNativeSimd,
        EnergyDutyKilowatts: Math.Round(battery.EnergyDutyWatts / 1000.0, 2),
        ReactorStates: reactorStatesDto
    ));
})
.WithName("SimulateCstrBattery")
.WithOpenApi();

app.MapPost("/api/telemetry/cstr/series-parallel/step", (SeriesParallelStreamRequest request) =>
{
    int steps = Math.Clamp(request.MaxFrames, 1, 1000);
    List<SeriesParallelTelemetryFrameDto> frames = SeriesParallelTelemetryHelper.SimulateTrajectory(request, steps);
    return Results.Ok(frames[^1]);
})
.WithName("StepSeriesParallelCstrTelemetry")
.WithOpenApi();

app.MapPost("/api/telemetry/cstr/series-parallel/profile", (SeriesParallelRunawayProfileRequest request) =>
{
    int steps = Math.Clamp(request.Steps, 10, 1000);
    double safeTempK = Math.Max(300.0, request.MaxSafeTemperatureKelvin);

    var nominalRequest = new SeriesParallelStreamRequest(
        VolumetricFlowLPerMin: request.VolumetricFlowLPerMin,
        ReactorVolumeLiters: request.ReactorVolumeLiters,
        FeedCaMolPerL: request.FeedCaMolPerL,
        FeedCbMolPerL: 0.0,
        FeedTemperatureKelvin: request.NominalFeedTemperatureKelvin,
        JacketInletTemperatureKelvin: request.JacketInletTemperatureKelvin,
        CoolingUaWattsPerKelvin: request.NominalCoolingUaWattsPerKelvin,
        MaxSafeTemperatureKelvin: safeTempK,
        TimeStepMinutes: request.TimeStepMinutes,
        MaxFrames: steps,
        IntervalMs: 0);

    var runawayRequest = nominalRequest with
    {
        FeedTemperatureKelvin = request.RunawayFeedTemperatureKelvin,
        CoolingUaWattsPerKelvin = request.RunawayCoolingUaWattsPerKelvin
    };

    List<SeriesParallelTelemetryFrameDto> nominalTrajectory = SeriesParallelTelemetryHelper.SimulateTrajectory(nominalRequest, steps);
    List<SeriesParallelTelemetryFrameDto> runawayTrajectory = SeriesParallelTelemetryHelper.SimulateTrajectory(runawayRequest, steps);

    double nominalPeakC = nominalTrajectory.Max(f => f.TemperatureCelsius);
    bool nominalHasRunaway = nominalTrajectory.Any(f => f.IsThermalRunaway);
    double runawayPeakC = runawayTrajectory.Max(f => f.TemperatureCelsius);
    bool runawayTriggered = runawayTrajectory.Any(f => f.IsThermalRunaway);

    return Results.Ok(new SeriesParallelRunawayProfileResponse(
        EngineMode: SeriesParallelTelemetryHelper.GetEngineModeLabel(),
        MaxSafeTemperatureKelvin: Math.Round(safeTempK, 2),
        MaxSafeTemperatureCelsius: Math.Round(safeTempK - 273.15, 2),
        NominalPeakTemperatureCelsius: Math.Round(nominalPeakC, 2),
        NominalHasRunaway: nominalHasRunaway,
        RunawayPeakTemperatureCelsius: Math.Round(runawayPeakC, 2),
        RunawayTriggered: runawayTriggered,
        NominalTrajectory: nominalTrajectory,
        RunawayTrajectory: runawayTrajectory
    ));
})
.WithName("ProfileSeriesParallelRunaway")
.WithOpenApi();

app.Run();

public partial class Program { }
