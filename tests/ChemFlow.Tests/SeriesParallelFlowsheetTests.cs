using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;

namespace ChemFlow.Tests;

public sealed class SeriesParallelFlowsheetTests
{
    private readonly IThermodynamicPackage _thermo = new RaoultThermodynamicPackage();

    private static IReadOnlyList<ChemicalCompound> CreateHydrocarbonReactionCompounds()
    {
        ChemicalCompound pentane = DefaultCompounds.All.Single(c => c.Id == "n-C5H12");
        ChemicalCompound benzene = DefaultCompounds.Benzene;
        ChemicalCompound toluene = DefaultCompounds.Toluene;
        ChemicalCompound pXylene = DefaultCompounds.All.Single(c => c.Id == "p-C8H10");
        return [pentane, benzene, toluene, pXylene];
    }

    private static IReadOnlyList<ChemicalCompound> CreateSolventDilutedCompounds()
    {
        ChemicalCompound pentane = DefaultCompounds.All.Single(c => c.Id == "n-C5H12");
        ChemicalCompound benzene = DefaultCompounds.Benzene;
        ChemicalCompound toluene = DefaultCompounds.Toluene;
        ChemicalCompound pXylene = DefaultCompounds.All.Single(c => c.Id == "p-C8H10");
        ChemicalCompound heptane = DefaultCompounds.NHeptane; // Inert diluent
        return [pentane, benzene, toluene, pXylene, heptane];
    }

    [Fact]
    public void SeriesParallelCstr_DefaultKineticsInSolventFlowsheet_ConservesBalancesAndPreservesInert()
    {
        // Uses default ReactorCore.C kinetics (~1.0 M feed A in inert n-C7H16 solvent, plus small recycle-like B/C/D)
        IReadOnlyList<ChemicalCompound> compounds = CreateSolventDilutedCompounds();

        var feed = new MaterialStream(
            "S-FEED",
            "Diluted Reactor Feed",
            compounds,
            molarFlowRateMolPerSec: 20.0,
            temperatureKelvin: 312.0,
            pressureKpa: 180.0,
            compositionZ: [0.12, 0.015, 0.005, 0.005, 0.855]);

        var outlet = new MaterialStream("S-OUT", "Reactor Effluent", compounds);

        var cstr = new SeriesParallelContinuousStirredTankReactor(
            id: "R-101",
            name: "Series-Parallel CSTR",
            inletStream: feed,
            outletStream: outlet,
            reactantACompoundId: "n-C5H12",
            productBCompoundId: "C6H6",
            byproductCCompoundId: "C7H8",
            byproductDCompoundId: "p-C8H10",
            reactorVolumeLiters: 25.0,
            coolingUaWattsPerKelvin: 1500.0,
            jacketInletTemperatureKelvin: 295.0,
            maxSafeTemperatureKelvin: 450.0);

        var flowsheet = new ProcessFlowsheet("Series-Parallel CSTR Unit Test", _thermo);
        flowsheet.AddUnit(cstr);

        IReadOnlyList<IUnitOperation> executionOrder = flowsheet.Solve();
        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();
        string dot = flowsheet.ToDot();

        Assert.Single(executionOrder);
        Assert.Equal("R-101", executionOrder[0].Id);
        Assert.True(outlet.IsSolved);

        // Strict plant-level mass & enthalpy closure even with differing species molar masses
        Assert.True(report.IsMassConserved, $"Mass error: {report.GlobalBalance.RelativeMassError:E3}");
        Assert.True(report.IsEnergyConserved, $"Energy error: {report.GlobalBalance.RelativeEnthalpyError:E3}");
        Assert.True(report.GlobalBalance.RelativeMassError < 1e-6);
        Assert.True(report.GlobalBalance.RelativeEnthalpyError < 1e-4);

        // Reaction progress & selectivity metrics
        Assert.InRange(cstr.ConversionAPercent, 1.0, 99.0);
        Assert.InRange(cstr.SelectivityBPercent, 1.0, 100.0);
        Assert.True(cstr.YieldBPercent > 0.0, $"YieldB={cstr.YieldBPercent:F3}, X_A={cstr.ConversionAPercent:F2}, T_out={cstr.OutletTemperatureKelvin:F2}");
        Assert.True(cstr.OutletCaMolPerL < cstr.InletCaMolPerL);
        Assert.True(cstr.OutletCbMolPerL > cstr.InletCbMolPerL);
        Assert.True(cstr.OutletCdMolPerL > cstr.InletCdMolPerL);

        // Inert n-C7H16 remains non-reacting fraction of total reacting/non-reacting pool
        Assert.Equal(0.855, outlet.CompositionZ[4], precision: 6);

        // Dynamic cooling jacket thermal coupling
        Assert.True(cstr.HeatGeneratedWatts > 0.0);
        Assert.True(cstr.HeatRemovedWatts > 0.0);
        Assert.True(cstr.JacketOutletTemperatureKelvin > cstr.JacketInletTemperatureKelvin);
        Assert.False(cstr.IsThermalRunaway);

        // Graphviz DOT visualization
        Assert.Contains("SeriesParallelContinuousStirredTankReactor", dot);
        Assert.Contains("#FCE7F3", dot);
        Assert.Contains("S_B=", dot);
        Assert.Contains("Tj=", dot);
    }

    [Fact]
    public void SeriesParallelCstr_LowCoolingUa_DetectsThermalRunawayAndTagsDotGraph()
    {
        IReadOnlyList<ChemicalCompound> compounds = CreateSolventDilutedCompounds();

        var feedCold = new MaterialStream("S-IN-1", "Cooled Case Feed", compounds, 20.0, 312.0, 200.0, [0.12, 0.0, 0.0, 0.0, 0.88]);
        var outCold = new MaterialStream("S-OUT-1", "Cooled Case Outlet", compounds);
        var cooledCstr = new SeriesParallelContinuousStirredTankReactor(
            id: "R-SAFE",
            name: "Well-Cooled CSTR",
            inletStream: feedCold,
            outletStream: outCold,
            reactantACompoundId: "n-C5H12",
            productBCompoundId: "C6H6",
            byproductCCompoundId: "C7H8",
            byproductDCompoundId: "p-C8H10",
            reactorVolumeLiters: 25.0,
            coolingUaWattsPerKelvin: 2500.0,
            jacketInletTemperatureKelvin: 292.0,
            maxSafeTemperatureKelvin: 400.0);

        var feedHot = new MaterialStream("S-IN-2", "Runaway Case Feed", compounds, 20.0, 345.0, 200.0, [0.25, 0.0, 0.0, 0.0, 0.75]);
        var outHot = new MaterialStream("S-OUT-2", "Runaway Case Outlet", compounds);
        var runawayCstr = new SeriesParallelContinuousStirredTankReactor(
            id: "R-RUNAWAY",
            name: "Undercooled CSTR",
            inletStream: feedHot,
            outletStream: outHot,
            reactantACompoundId: "n-C5H12",
            productBCompoundId: "C6H6",
            byproductCCompoundId: "C7H8",
            byproductDCompoundId: "p-C8H10",
            reactorVolumeLiters: 180.0,
            coolingUaWattsPerKelvin: 5.0,
            jacketInletTemperatureKelvin: 375.0,
            maxSafeTemperatureKelvin: 400.0);

        var flowsheet = new ProcessFlowsheet("Thermal Runaway Comparison", _thermo);
        flowsheet.AddUnit(cooledCstr);
        flowsheet.AddUnit(runawayCstr);

        flowsheet.Solve();
        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();
        string dot = flowsheet.ToDot();

        Assert.True(report.IsMassConserved);
        Assert.True(report.IsEnergyConserved);
        Assert.False(cooledCstr.IsThermalRunaway, $"Cooled T_out={cooledCstr.OutletTemperatureKelvin:F2}, X_A={cooledCstr.ConversionAPercent:F2}");
        Assert.True(runawayCstr.IsThermalRunaway);
        Assert.True(runawayCstr.OutletTemperatureKelvin > cooledCstr.OutletTemperatureKelvin + 20.0);
        Assert.Contains("[RUNAWAY]", dot);
    }

    [Fact]
    public void SeriesParallelCstr_ClosedLoopRecycleWithDistillation_ConvergesWithWegsteinAndClosesBalances()
    {
        // Complete Reaction-Separation-Recycle Flowsheet:
        // [Fresh A] + [Recycle A] -> (MIX-101) -> (R-101 Series-Parallel CSTR) -> (C-101 Recovery Column)
        //   - C-101 Distillate (unreacted A) -> (SPL-101) -> 85% [Recycle A] + 15% [Purge]
        //   - C-101 Bottoms (B + C + D) -> (C-102 Product Column) -> [Product B (C6H6)] + [Heavies C+D]
        IReadOnlyList<ChemicalCompound> compounds = CreateHydrocarbonReactionCompounds();

        var freshFeed = new MaterialStream(
            "S-FRESH",
            "Fresh n-Pentane Feed",
            compounds,
            molarFlowRateMolPerSec: 12.0,
            temperatureKelvin: 345.0,
            pressureKpa: 150.0,
            compositionZ: [0.97, 0.03, 0.0, 0.0]);

        var recycleTear = new MaterialStream(
            "S-RECYCLE",
            "Reactant A Recycle",
            compounds,
            molarFlowRateMolPerSec: 3.0,
            temperatureKelvin: 330.0,
            pressureKpa: 150.0,
            compositionZ: [0.95, 0.05, 0.0, 0.0]);

        var reactorFeed = new MaterialStream("S-RXN-IN", "Mixed Reactor Feed", compounds);
        var reactorOut = new MaterialStream("S-RXN-OUT", "Reactor Effluent", compounds);
        var distRecoverA = new MaterialStream("S-DIST-A", "Recovered Reactant Overhead", compounds);
        var crudeProducts = new MaterialStream("S-CRUDE-PROD", "Crude Product Bottoms", compounds);
        var productB = new MaterialStream("S-PROD-B", "Purified Benzene Product", compounds);
        var heaviesOut = new MaterialStream("S-HEAVIES", "Heavy Byproducts C+D", compounds);
        var purgeOut = new MaterialStream("S-PURGE", "Light Purge", compounds);

        var mixer = new Mixer("MIX-101", "Feed-Recycle Mixer", [freshFeed, recycleTear], reactorFeed);

        // Van-Heerden-stable hydrocarbon isomerization/aromatization kinetics with active cooling jacket
        var reactor = new SeriesParallelContinuousStirredTankReactor(
            id: "R-101",
            name: "Series-Parallel CSTR",
            inletStream: reactorFeed,
            outletStream: reactorOut,
            reactantACompoundId: "n-C5H12",
            productBCompoundId: "C6H6",
            byproductCCompoundId: "C7H8",
            byproductDCompoundId: "p-C8H10",
            reactorVolumeLiters: 110.0,
            rhoCpJPerLK: 2200.0,
            k0_1PerMin: 2.0e6,
            ea1OverRKelvin: 5000.0,
            deltaH1JPerMol: 8000.0,
            k0_2PerMin: 3.5e5,
            ea2OverRKelvin: 5200.0,
            deltaH2JPerMol: 6000.0,
            k0_3PerMin: 1.5e5,
            ea3OverRKelvin: 5400.0,
            deltaH3JPerMol: 7000.0,
            coolingUaWattsPerKelvin: 4000.0,
            jacketInletTemperatureKelvin: 345.0,
            maxSafeTemperatureKelvin: 450.0);

        var colRecoverA = new ShortcutDistillationColumn(
            id: "C-101",
            name: "Reactant A Recovery Column",
            feedStream: reactorOut,
            distillateOutlet: distRecoverA,
            bottomsOutlet: crudeProducts,
            lightKeyIndex: 0,  // n-C5H12 (A)
            heavyKeyIndex: 1,  // C6H6 (B)
            lightKeyRecoveryDistillate: 0.96,
            heavyKeyRecoveryBottoms: 0.96,
            refluxRatioMultiplier: 1.25,
            columnPressureKpa: 150.0);

        var colPurifyB = new ShortcutDistillationColumn(
            id: "C-102",
            name: "Benzene Product Column",
            feedStream: crudeProducts,
            distillateOutlet: productB,
            bottomsOutlet: heaviesOut,
            lightKeyIndex: 1,  // C6H6 (B)
            heavyKeyIndex: 2,  // C7H8 (C)
            lightKeyRecoveryDistillate: 0.96,
            heavyKeyRecoveryBottoms: 0.96,
            refluxRatioMultiplier: 1.25,
            columnPressureKpa: 150.0);

        var splitter = new Splitter(
            id: "SPL-101",
            name: "Recycle/Purge Splitter",
            inletStream: distRecoverA,
            outletStreams: [recycleTear, purgeOut],
            splitFractions: [0.85, 0.15]);

        var flowsheet = new ProcessFlowsheet("Series-Parallel CSTR Recycle Plant", _thermo);
        flowsheet.AddUnit(mixer);
        flowsheet.AddUnit(reactor);
        flowsheet.AddUnit(colRecoverA);
        flowsheet.AddUnit(colPurifyB);
        flowsheet.AddUnit(splitter);

        FlowsheetSolveResult result = flowsheet.SolveWithRecycle(
            new RecycleSolverOptions(
                Method: RecycleConvergenceMethod.Wegstein,
                MaxIterations: 60,
                Tolerance: 1e-5));

        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();
        string dot = flowsheet.ToDot();

        Assert.True(result.HasRecycle);
        Assert.True(result.IsConverged, $"Recycle loop did not converge after {result.Iterations} iterations.");
        Assert.True(result.Iterations <= 30, $"Expected fast Wegstein convergence, took {result.Iterations} iterations.");
        Assert.Contains("S-RECYCLE", result.TearStreamIds);
        Assert.False(reactor.IsThermalRunaway);

        // Verify chemical engineering separation and recycle behavior
        Assert.InRange(reactor.ConversionAPercent, 20.0, 90.0);
        Assert.True(recycleTear.MolarFlowRateMolPerSec > 0.5, "Expected significant unreacted reactant A in recycle stream.");
        Assert.True(recycleTear.CompositionZ[0] > 0.80, $"Expected n-C5H12-rich recycle, got {recycleTear.CompositionZ[0]:P2}.");
        Assert.True(productB.CompositionZ[1] > 0.70, $"Expected purified Benzene product stream, got {productB.CompositionZ[1]:P2}.");
        Assert.True(heaviesOut.CompositionZ[2] + heaviesOut.CompositionZ[3] > 0.60,
            $"Expected C+D heavies enrichment in C-102 bottoms, got {(heaviesOut.CompositionZ[2] + heaviesOut.CompositionZ[3]):P2}.");

        // Overall conversion of A across the integrated plant exceeds single-pass reactor conversion
        double freshA = freshFeed.GetComponentMolarFlowMolPerSec(0);
        double exitA = purgeOut.GetComponentMolarFlowMolPerSec(0)
                     + productB.GetComponentMolarFlowMolPerSec(0)
                     + heaviesOut.GetComponentMolarFlowMolPerSec(0);
        double overallConversionA = (freshA - exitA) / freshA * 100.0;
        Assert.True(overallConversionA > reactor.ConversionAPercent,
            $"Expected overall plant conversion ({overallConversionA:F2}%) > single-pass CSTR conversion ({reactor.ConversionAPercent:F2}%).");

        // Closed plant mass & energy balances
        Assert.True(report.IsMassConserved, $"Global mass error: {report.GlobalBalance.RelativeMassError:E3}");
        Assert.True(report.IsEnergyConserved, $"Global energy error: {report.GlobalBalance.RelativeEnthalpyError:E3}");

        // Graphviz DOT contains reactor and both separation columns
        Assert.Contains("R_101", dot);
        Assert.Contains("C_101", dot);
        Assert.Contains("C_102", dot);
        Assert.Contains("SPL_101", dot);
    }

    [Fact]
    public void SeriesParallelCstr_CoupledWithRigorousMeshDistillationColumn_ConvergesAndConservesBalances()
    {
        IReadOnlyList<ChemicalCompound> compounds = CreateHydrocarbonReactionCompounds();

        var feed = new MaterialStream("S-FEED", "Fresh Feed", compounds, 12.0, 345.0, 150.0, [0.95, 0.05, 0.0, 0.0]);
        var rxnOut = new MaterialStream("S-RXN-OUT", "Reactor Effluent", compounds);
        var distShort = new MaterialStream("S-DIST-SHORT", "Shortcut Distillate", compounds);
        var btmShort = new MaterialStream("S-BTM-SHORT", "Shortcut Bottoms", compounds);

        var reactor = new SeriesParallelContinuousStirredTankReactor(
            id: "R-201",
            name: "Series-Parallel CSTR",
            inletStream: feed,
            outletStream: rxnOut,
            reactantACompoundId: "n-C5H12",
            productBCompoundId: "C6H6",
            byproductCCompoundId: "C7H8",
            byproductDCompoundId: "p-C8H10",
            reactorVolumeLiters: 60.0,
            rhoCpJPerLK: 2200.0,
            k0_1PerMin: 2.0e6,
            ea1OverRKelvin: 5000.0,
            deltaH1JPerMol: 8000.0,
            k0_2PerMin: 3.5e5,
            ea2OverRKelvin: 5200.0,
            deltaH2JPerMol: 6000.0,
            k0_3PerMin: 1.5e5,
            ea3OverRKelvin: 5400.0,
            deltaH3JPerMol: 7000.0,
            coolingUaWattsPerKelvin: 4000.0,
            jacketInletTemperatureKelvin: 345.0,
            maxSafeTemperatureKelvin: 450.0);

        feed.SolveEquilibrium(_thermo);
        reactor.Solve(_thermo);

        // Size a shortcut column for overhead unreacted A recovery, then upgrade to rigorous MESH column
        var shortcut = new ShortcutDistillationColumn(
            id: "C-201-FUG",
            name: "FUG Initializer",
            feedStream: rxnOut,
            distillateOutlet: distShort,
            bottomsOutlet: btmShort,
            lightKeyIndex: 0,
            heavyKeyIndex: 1,
            lightKeyRecoveryDistillate: 0.95,
            heavyKeyRecoveryBottoms: 0.95,
            refluxRatioMultiplier: 1.30,
            columnPressureKpa: 150.0);
        shortcut.Solve(_thermo);

        var distRigorous = new MaterialStream("S-DIST-MESH", "MESH Overhead A", compounds);
        var btmRigorous = new MaterialStream("S-BTM-MESH", "MESH Bottoms Products", compounds);

        DistillationColumn rigorousCol = shortcut.ToRigorousColumn(
            id: "C-201-MESH",
            name: "Rigorous MESH Recovery Column",
            distillateOutlet: distRigorous,
            bottomsOutlet: btmRigorous);

        var flowsheet = new ProcessFlowsheet("Reactor + Rigorous MESH Column", _thermo);
        flowsheet.AddUnit(reactor);
        flowsheet.AddUnit(rigorousCol);

        flowsheet.Solve();
        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();

        Assert.True(rigorousCol.IsConverged, $"Rigorous MESH column did not converge after {rigorousCol.Iterations} iterations.");
        Assert.True(distRigorous.CompositionZ[0] > 0.80,
            $"Expected unreacted A concentrated overhead, got {distRigorous.CompositionZ[0]:P2}.");
        Assert.True(btmRigorous.CompositionZ[1] + btmRigorous.CompositionZ[2] + btmRigorous.CompositionZ[3] > 0.75);
        Assert.True(report.IsMassConserved);
        Assert.True(report.IsEnergyConserved);
    }

    [Fact]
    public void SeriesParallelCstr_WithPengRobinsonAndBroydenRecycle_ConvergesAndConservesBalances()
    {
        var pengRobinson = new PengRobinsonThermodynamicPackage();
        IReadOnlyList<ChemicalCompound> compounds = CreateHydrocarbonReactionCompounds();

        var freshFeed = new MaterialStream("S-PR-FRESH", "Fresh Feed", compounds, 12.0, 345.0, 150.0, [0.96, 0.04, 0.0, 0.0]);
        var recycleTear = new MaterialStream("S-PR-REC", "Recycle Tear", compounds, 2.5, 330.0, 150.0, [0.95, 0.05, 0.0, 0.0]);
        var rxnIn = new MaterialStream("S-PR-IN", "Reactor Feed", compounds);
        var rxnOut = new MaterialStream("S-PR-OUT", "Reactor Effluent", compounds);
        var dist = new MaterialStream("S-PR-DIST", "Recovered A Overhead", compounds);
        var btm = new MaterialStream("S-PR-BTM", "Product Bottoms", compounds);
        var purge = new MaterialStream("S-PR-PURGE", "Purge", compounds);

        var mixer = new Mixer("MIX-PR", "Feed Mixer", [freshFeed, recycleTear], rxnIn);
        var reactor = new SeriesParallelContinuousStirredTankReactor(
            id: "R-PR",
            name: "PR Series-Parallel CSTR",
            inletStream: rxnIn,
            outletStream: rxnOut,
            reactantACompoundId: "n-C5H12",
            productBCompoundId: "C6H6",
            byproductCCompoundId: "C7H8",
            byproductDCompoundId: "p-C8H10",
            reactorVolumeLiters: 110.0,
            rhoCpJPerLK: 2200.0,
            k0_1PerMin: 2.0e6,
            ea1OverRKelvin: 5000.0,
            deltaH1JPerMol: 8000.0,
            k0_2PerMin: 3.5e5,
            ea2OverRKelvin: 5200.0,
            deltaH2JPerMol: 6000.0,
            k0_3PerMin: 1.5e5,
            ea3OverRKelvin: 5400.0,
            deltaH3JPerMol: 7000.0,
            coolingUaWattsPerKelvin: 4000.0,
            jacketInletTemperatureKelvin: 345.0);

        var column = new ShortcutDistillationColumn(
            id: "C-PR",
            name: "PR Recovery Column",
            feedStream: rxnOut,
            distillateOutlet: dist,
            bottomsOutlet: btm,
            lightKeyIndex: 0,
            heavyKeyIndex: 1,
            lightKeyRecoveryDistillate: 0.96,
            heavyKeyRecoveryBottoms: 0.96,
            refluxRatioMultiplier: 1.25,
            columnPressureKpa: 150.0);

        var splitter = new Splitter("SPL-PR", "Purge Splitter", dist, [recycleTear, purge], [0.85, 0.15]);

        var flowsheet = new ProcessFlowsheet("Peng-Robinson Recycle CSTR Plant", pengRobinson);
        flowsheet.AddUnit(mixer);
        flowsheet.AddUnit(reactor);
        flowsheet.AddUnit(column);
        flowsheet.AddUnit(splitter);

        FlowsheetSolveResult result = flowsheet.SolveWithRecycle(
            new RecycleSolverOptions(Method: RecycleConvergenceMethod.Broyden, MaxIterations: 60, Tolerance: 1e-5));
        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();

        Assert.True(result.IsConverged, $"Broyden recycle failed to converge after {result.Iterations} iterations.");
        Assert.True(report.IsMassConserved, $"Mass error: {report.GlobalBalance.RelativeMassError:E3}");
        Assert.True(report.IsEnergyConserved, $"Energy error: {report.GlobalBalance.RelativeEnthalpyError:E3}");
        Assert.InRange(reactor.ConversionAPercent, 15.0, 90.0);
        Assert.True(btm.CompositionZ[1] > 0.50);
    }

    [Fact]
    public void SeriesParallelCstr_InvalidConfigurationOrMissingCompound_ThrowsExplicitExceptions()
    {
        IReadOnlyList<ChemicalCompound> compounds = CreateHydrocarbonReactionCompounds();
        var feed = new MaterialStream("S-1", "Feed", compounds, 10.0, 340.0, 150.0, [1.0, 0.0, 0.0, 0.0]);
        var outlet = new MaterialStream("S-2", "Outlet", compounds);

        Assert.Throws<ArgumentException>(() => new SeriesParallelContinuousStirredTankReactor(
            "R-ERR-1", "Duplicate IDs", feed, outlet, "n-C5H12", "n-C5H12", "C7H8", "p-C8H10"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new SeriesParallelContinuousStirredTankReactor(
            "R-ERR-2", "Invalid Volume", feed, outlet, "n-C5H12", "C6H6", "C7H8", "p-C8H10", reactorVolumeLiters: 0.0));

        var missingCompoundReactor = new SeriesParallelContinuousStirredTankReactor(
            "R-ERR-3", "Missing Compound", feed, outlet, "n-C5H12", "C6H6", "C7H8", "H2O");
        feed.SolveEquilibrium(_thermo);
        Assert.Throws<InvalidOperationException>(() => missingCompoundReactor.Solve(_thermo));
    }
}
