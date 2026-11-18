using ChemFlow.Api.Contracts;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;

namespace ChemFlow.Api.Services;

/// <summary>
/// Shared flowsheet builder and runner for coupled Series-Parallel CSTR + Separation + Recycle plants.
/// Supports both FUG Distillation and Peng-Robinson/Raoult FlashDrum VLE separation topologies,
/// optional L-stable ROS3L(2) stiff reactor integration, and Single-Pass vs. Wegstein/Broyden comparisons.
/// </summary>
public static class SeriesParallelPlantRunner
{
    private const double FeedPriceUsdPerMol = 0.045;
    private const double ProductBPriceUsdPerMol = 0.115;
    private const double UtilityPriceUsdPerKwh = 0.025;

    public sealed record PlantExecutionOutcome(
        ProcessFlowsheet Flowsheet,
        FlowsheetSolveResult SolveResult,
        FlowsheetBalanceReport BalanceReport,
        SeriesParallelContinuousStirredTankReactor Reactor,
        MaterialStream FreshFeed,
        MaterialStream ProductStream,
        IReadOnlyList<MaterialStream> ExitStreams,
        string SeparationMode,
        string SolverMode,
        double NetProfitUsdPerHour,
        double SpecificCoolingDutyKwPerMolB,
        IReadOnlyList<TearStreamConvergenceStepDto> ConvergenceTrace
    );

    public static SeriesParallelPlantSimulationResponse ExecuteAndSave(
        SeriesParallelPlantSimulationRequest request,
        ICompoundRepository compoundRepo,
        ISimulationHistoryRepository historyRepo,
        PengRobinsonThermodynamicPackage prPackage,
        RaoultThermodynamicPackage raoultPackage)
    {
        PlantExecutionOutcome outcome = ExecuteInternal(
            request,
            compoundRepo,
            prPackage,
            raoultPackage,
            overrideMethod: null,
            forceOpenLoopSinglePass: false);

        long runId = historyRepo.SaveSimulationRun(outcome.Flowsheet, outcome.SolveResult.ExecutionOrder);
        return ToSimulationResponse(outcome, runId);
    }

    public static SeriesParallelPlantComparisonResponse CompareSinglePassVsRecycle(
        SeriesParallelPlantSimulationRequest request,
        ICompoundRepository compoundRepo,
        PengRobinsonThermodynamicPackage prPackage,
        RaoultThermodynamicPackage raoultPackage)
    {
        PlantExecutionOutcome singlePass = ExecuteInternal(
            request,
            compoundRepo,
            prPackage,
            raoultPackage,
            overrideMethod: RecycleConvergenceMethod.DirectSubstitution,
            forceOpenLoopSinglePass: true);

        PlantExecutionOutcome wegstein = ExecuteInternal(
            request,
            compoundRepo,
            prPackage,
            raoultPackage,
            overrideMethod: RecycleConvergenceMethod.Wegstein,
            forceOpenLoopSinglePass: false);

        PlantExecutionOutcome broyden = ExecuteInternal(
            request,
            compoundRepo,
            prPackage,
            raoultPackage,
            overrideMethod: RecycleConvergenceMethod.Broyden,
            forceOpenLoopSinglePass: false);

        SeriesParallelPlantCaseComparisonDto singleDto = ToComparisonDto("Single-Pass (Open Loop)", "OpenLoop", singlePass);
        SeriesParallelPlantCaseComparisonDto wegsteinDto = ToComparisonDto("Closed-Loop Recycle (Wegstein)", "Wegstein", wegstein);
        SeriesParallelPlantCaseComparisonDto broydenDto = ToComparisonDto("Closed-Loop Recycle (Broyden)", "Broyden", broyden);

        double convGain = Math.Round(wegsteinDto.OverallPlantConversionAPercent - singleDto.OverallPlantConversionAPercent, 2);
        double flowGainPct = singleDto.PurifiedBenzeneFlowMolPerSec > 1e-9
            ? Math.Round(((wegsteinDto.PurifiedBenzeneFlowMolPerSec - singleDto.PurifiedBenzeneFlowMolPerSec) / singleDto.PurifiedBenzeneFlowMolPerSec) * 100.0, 2)
            : 0.0;
        double profitGain = Math.Round(wegsteinDto.NetProfitUsdPerHour - singleDto.NetProfitUsdPerHour, 2);

        return new SeriesParallelPlantComparisonResponse(
            SeparationMode: wegstein.SeparationMode,
            SolverMode: wegstein.SolverMode,
            ThermodynamicModel: wegstein.Flowsheet.ThermodynamicPackage.Name,
            SinglePassCase: singleDto,
            WegsteinRecycleCase: wegsteinDto,
            BroydenRecycleCase: broydenDto,
            ConversionGainPercentagePoints: convGain,
            BenzeneFlowGainPercent: flowGainPct,
            HourlyProfitGainUsdPerHour: profitGain
        );
    }

    private static PlantExecutionOutcome ExecuteInternal(
        SeriesParallelPlantSimulationRequest request,
        ICompoundRepository compoundRepo,
        PengRobinsonThermodynamicPackage prPackage,
        RaoultThermodynamicPackage raoultPackage,
        RecycleConvergenceMethod? overrideMethod,
        bool forceOpenLoopSinglePass)
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
        double recycleFrac = forceOpenLoopSinglePass ? 0.0 : Math.Clamp(request.RecycleFraction, 0.05, 0.95);
        bool useFlashMode = string.Equals(request.SeparationMode, "FlashDrum", StringComparison.OrdinalIgnoreCase);
        string sepModeLabel = useFlashMode ? "FlashDrum" : "Distillation";
        string solverLabel = request.UseStiffRos3L ? "ROS3L" : "RK4";

        var freshFeed = new MaterialStream(
            "S-FEED",
            "Fresh Pentane Feed",
            compounds,
            molarFlowRateMolPerSec: freshFlow,
            temperatureKelvin: freshTempK,
            pressureKpa: pressureKpa,
            compositionZ: [0.97, 0.03, 0.0, 0.0]);

        var reactorFeed = forceOpenLoopSinglePass
            ? freshFeed
            : new MaterialStream("S-RXN-IN", "Mixed Reactor Feed", compounds);
        var reactorOut = new MaterialStream("S-RXN-OUT", "Reactor Effluent", compounds);

        var flowsheet = new ProcessFlowsheet(
            useFlashMode ? "Series-Parallel CSTR Flash-Recycle Plant" : "Series-Parallel CSTR Recycle Plant",
            thermo);

        MaterialStream? recycleTear = null;
        if (!forceOpenLoopSinglePass)
        {
            recycleTear = new MaterialStream(
                "S-RECYCLE",
                "Reactant A Recycle",
                compounds,
                molarFlowRateMolPerSec: Math.Round(freshFlow * 0.25, 4),
                temperatureKelvin: useFlashMode ? Math.Clamp(request.FlashTemperatureKelvin, 280.0, 430.0) : 330.0,
                pressureKpa: pressureKpa,
                compositionZ: [0.95, 0.05, 0.0, 0.0]);

            flowsheet.AddUnit(new Mixer("MIX-101", "Feed-Recycle Mixer", [freshFeed, recycleTear], reactorFeed));
        }

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
            maxSafeTemperatureKelvin: Math.Clamp(request.MaxSafeTemperatureKelvin, 350.0, 650.0),
            useStiffRos3LSolver: request.UseStiffRos3L);

        flowsheet.AddUnit(reactor);

        MaterialStream productB;
        var exitStreams = new List<MaterialStream>();

        if (useFlashMode)
        {
            double flashTempK = Math.Clamp(request.FlashTemperatureKelvin, 280.0, 430.0);
            var cooledEffluent = new MaterialStream("S-FLASH-IN", "Cooled Flash Feed", compounds);
            var flashVapor = new MaterialStream("S-FLASH-VAP", "Flash Vapor Overhead", compounds);
            var flashLiquid = new MaterialStream("S-PROD-B", "Benzene-Enriched Flash Liquid", compounds);
            var purgeOut = new MaterialStream("S-PURGE", "Light Purge", compounds);

            flowsheet.AddUnit(new HeaterCooler("E-101", "Effluent Flash Cooler", reactorOut, cooledEffluent, flashTempK));
            flowsheet.AddUnit(new FlashDrum("V-101", "VLE Flash Separator", cooledEffluent, flashVapor, flashLiquid, flashTempK, pressureKpa));

            if (forceOpenLoopSinglePass)
            {
                productB = flashLiquid;
                exitStreams.Add(flashLiquid);
                exitStreams.Add(flashVapor);
            }
            else
            {
                flowsheet.AddUnit(new Splitter("SPL-101", "Vapor Recycle/Purge Splitter", flashVapor, [recycleTear!, purgeOut], [recycleFrac, 1.0 - recycleFrac]));
                productB = flashLiquid;
                exitStreams.Add(flashLiquid);
                exitStreams.Add(purgeOut);
            }
        }
        else
        {
            var distRecoverA = new MaterialStream("S-DIST-A", "Recovered Reactant Overhead", compounds);
            var crudeProducts = new MaterialStream("S-CRUDE-PROD", "Crude Product Bottoms", compounds);
            productB = new MaterialStream("S-PROD-B", "Purified Benzene Product", compounds);
            var heaviesOut = new MaterialStream("S-HEAVIES", "Heavy Byproducts C+D", compounds);
            var purgeOut = new MaterialStream("S-PURGE", "Light Purge", compounds);

            flowsheet.AddUnit(new ShortcutDistillationColumn(
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
                columnPressureKpa: pressureKpa));

            flowsheet.AddUnit(new ShortcutDistillationColumn(
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
                columnPressureKpa: pressureKpa));

            if (forceOpenLoopSinglePass)
            {
                exitStreams.Add(distRecoverA);
                exitStreams.Add(productB);
                exitStreams.Add(heaviesOut);
            }
            else
            {
                flowsheet.AddUnit(new Splitter("SPL-101", "Recycle/Purge Splitter", distRecoverA, [recycleTear!, purgeOut], [recycleFrac, 1.0 - recycleFrac]));
                exitStreams.Add(purgeOut);
                exitStreams.Add(productB);
                exitStreams.Add(heaviesOut);
            }
        }

        RecycleConvergenceMethod method = overrideMethod ?? (request.Method.Trim().ToUpperInvariant() switch
        {
            "DIRECT" or "DIRECTSUBSTITUTION" => RecycleConvergenceMethod.DirectSubstitution,
            "BROYDEN" => RecycleConvergenceMethod.Broyden,
            _ => RecycleConvergenceMethod.Wegstein
        });

        FlowsheetSolveResult solveResult = flowsheet.SolveWithRecycle(
            new RecycleSolverOptions(
                Method: method,
                MaxIterations: 60,
                Tolerance: Math.Clamp(request.Tolerance, 1e-8, 1e-2)));

        FlowsheetBalanceReport balanceReport = flowsheet.GenerateBalanceReport();

        double freshA = freshFeed.GetComponentMolarFlowMolPerSec(0);
        double exitA = exitStreams.Sum(s => s.GetComponentMolarFlowMolPerSec(0));
        double overallConvA = freshA > 1e-12 ? Math.Clamp((freshA - exitA) / freshA * 100.0, 0.0, 100.0) : 0.0;

        double flowB = productB.GetComponentMolarFlowMolPerSec(1);
        double totalAbsDutyKw = flowsheet.Units.Sum(u => Math.Abs(u.EnergyDutyWatts) / 1000.0);
        double specificDuty = flowB > 1e-9 ? totalAbsDutyKw / flowB : 0.0;

        double revenuePerHr = flowB * 3600.0 * ProductBPriceUsdPerMol;
        double feedCostPerHr = freshFeed.MolarFlowRateMolPerSec * 3600.0 * FeedPriceUsdPerMol;
        double utilityCostPerHr = totalAbsDutyKw * UtilityPriceUsdPerKwh;
        double netProfitPerHr = revenuePerHr - feedCostPerHr - utilityCostPerHr;

        List<TearStreamConvergenceStepDto> trace = solveResult.ConvergenceHistory
            .Select(h => new TearStreamConvergenceStepDto(
                h.Iteration,
                h.StreamId,
                Math.Round(h.FlowErrorRelative, 8),
                Math.Round(h.TemperatureDeltaKelvin, 4),
                Math.Round(h.MaxCompositionDelta, 6),
                h.IsConverged))
            .ToList();

        return new PlantExecutionOutcome(
            Flowsheet: flowsheet,
            SolveResult: solveResult,
            BalanceReport: balanceReport,
            Reactor: reactor,
            FreshFeed: freshFeed,
            ProductStream: productB,
            ExitStreams: exitStreams,
            SeparationMode: sepModeLabel,
            SolverMode: solverLabel,
            NetProfitUsdPerHour: Math.Round(netProfitPerHr, 2),
            SpecificCoolingDutyKwPerMolB: Math.Round(specificDuty, 2),
            ConvergenceTrace: trace
        );
    }

    private static SeriesParallelPlantSimulationResponse ToSimulationResponse(PlantExecutionOutcome o, long runId)
    {
        double freshA = o.FreshFeed.GetComponentMolarFlowMolPerSec(0);
        double exitA = o.ExitStreams.Sum(s => s.GetComponentMolarFlowMolPerSec(0));
        double overallConvA = freshA > 1e-12 ? Math.Clamp((freshA - exitA) / freshA * 100.0, 0.0, 100.0) : 0.0;

        var unitDtos = o.SolveResult.ExecutionOrder
            .Select(u => new UnitResultDto(u.Id, u.Name, u.UnitType, Math.Round(u.EnergyDutyWatts / 1000.0, 3)))
            .ToList();

        var streamDtos = o.Flowsheet.Streams
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

        return new SeriesParallelPlantSimulationResponse(
            SavedSimulationRunId: runId,
            FlowsheetName: o.Flowsheet.Name,
            ThermodynamicModel: o.Flowsheet.ThermodynamicPackage.Name,
            TopologicalExecutionOrder: o.SolveResult.ExecutionOrder.Select(u => u.Id).ToList(),
            Units: unitDtos,
            Streams: streamDtos,
            IsMassConserved: o.BalanceReport.IsMassConserved,
            IsEnergyConserved: o.BalanceReport.IsEnergyConserved,
            MassBalanceRelativeError: Math.Round(o.BalanceReport.GlobalBalance.RelativeMassError, 8),
            EnergyBalanceRelativeError: Math.Round(o.BalanceReport.GlobalBalance.RelativeEnthalpyError, 8),
            TopologyUrl: $"/api/simulations/{runId}/topology",
            BalanceReportUrl: $"/api/simulations/{runId}/report",
            HasRecycle: o.SolveResult.HasRecycle,
            RecycleIterations: o.SolveResult.Iterations,
            TearStreams: o.SolveResult.TearStreamIds,
            SinglePassConversionAPercent: Math.Round(o.Reactor.ConversionAPercent, 2),
            OverallPlantConversionAPercent: Math.Round(overallConvA, 2),
            YieldBPercent: Math.Round(o.Reactor.YieldBPercent, 2),
            SelectivityBPercent: Math.Round(o.Reactor.SelectivityBPercent, 2),
            ReactorTemperatureKelvin: Math.Round(o.Reactor.OutletTemperatureKelvin, 2),
            ReactorTemperatureCelsius: Math.Round(o.Reactor.OutletTemperatureKelvin - 273.15, 2),
            JacketOutletTemperatureKelvin: Math.Round(o.Reactor.JacketOutletTemperatureKelvin, 2),
            JacketOutletTemperatureCelsius: Math.Round(o.Reactor.JacketOutletTemperatureKelvin - 273.15, 2),
            OutletCaMolPerL: Math.Round(o.Reactor.OutletCaMolPerL, 4),
            OutletCbMolPerL: Math.Round(o.Reactor.OutletCbMolPerL, 4),
            OutletCcMolPerL: Math.Round(o.Reactor.OutletCcMolPerL, 4),
            OutletCdMolPerL: Math.Round(o.Reactor.OutletCdMolPerL, 4),
            HeatGeneratedWatts: Math.Round(o.Reactor.HeatGeneratedWatts, 1),
            HeatRemovedWatts: Math.Round(o.Reactor.HeatRemovedWatts, 1),
            IsThermalRunaway: o.Reactor.IsThermalRunaway,
            IsUsingNativeCore: o.Reactor.IsUsingNativeCore,
            ProductBPurityMoleFraction: Math.Round(o.ProductStream.CompositionZ[1], 4),
            PurifiedBenzeneFlowMolPerSec: Math.Round(o.ProductStream.GetComponentMolarFlowMolPerSec(1), 4),
            SolverMode: o.SolverMode,
            SeparationMode: o.SeparationMode,
            NetProfitUsdPerHour: o.NetProfitUsdPerHour,
            SpecificCoolingDutyKwPerMolB: o.SpecificCoolingDutyKwPerMolB,
            ConvergenceTrace: o.ConvergenceTrace
        );
    }

    private static SeriesParallelPlantCaseComparisonDto ToComparisonDto(
        string caseName,
        string method,
        PlantExecutionOutcome o)
    {
        double freshA = o.FreshFeed.GetComponentMolarFlowMolPerSec(0);
        double exitA = o.ExitStreams.Sum(s => s.GetComponentMolarFlowMolPerSec(0));
        double overallConvA = freshA > 1e-12 ? Math.Clamp((freshA - exitA) / freshA * 100.0, 0.0, 100.0) : 0.0;

        return new SeriesParallelPlantCaseComparisonDto(
            CaseName: caseName,
            Method: method,
            IsConverged: o.SolveResult.IsConverged,
            Iterations: o.SolveResult.Iterations,
            OverallPlantConversionAPercent: Math.Round(overallConvA, 2),
            SinglePassConversionAPercent: Math.Round(o.Reactor.ConversionAPercent, 2),
            YieldBPercent: Math.Round(o.Reactor.YieldBPercent, 2),
            SelectivityBPercent: Math.Round(o.Reactor.SelectivityBPercent, 2),
            PurifiedBenzeneFlowMolPerSec: Math.Round(o.ProductStream.GetComponentMolarFlowMolPerSec(1), 4),
            ProductBPurityMoleFraction: Math.Round(o.ProductStream.CompositionZ[1], 4),
            NetProfitUsdPerHour: o.NetProfitUsdPerHour,
            SpecificCoolingDutyKwPerMolB: o.SpecificCoolingDutyKwPerMolB,
            IsMassConserved: o.BalanceReport.IsMassConserved,
            IsEnergyConserved: o.BalanceReport.IsEnergyConserved,
            ConvergenceTrace: o.ConvergenceTrace
        );
    }
}
