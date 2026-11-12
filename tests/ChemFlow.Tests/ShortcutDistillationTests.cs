using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Mathematics;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;

namespace ChemFlow.Tests;

public sealed class ShortcutDistillationTests
{
    private readonly IThermodynamicPackage _raoult = new RaoultThermodynamicPackage();
    private readonly IThermodynamicPackage _pengRobinson = new PengRobinsonThermodynamicPackage();

    [Theory]
    [InlineData(1.0)]   // Saturated liquid
    [InlineData(0.5)]   // 50% vaporized (two-phase)
    [InlineData(0.0)]   // Saturated vapor
    [InlineData(1.2)]   // Subcooled liquid
    [InlineData(-0.2)]  // Superheated vapor
    public void SolveUnderwoodTheta_FindsAccurateRoot_AcrossThermalStates(double qFeed)
    {
        // 4-component mixture: alpha = [3.5, 2.2, 1.0, 0.4] (LK = index 1 with alpha=2.2, HK = index 2 with alpha=1.0)
        double[] alpha = [3.5, 2.2, 1.0, 0.4];
        double[] zFeed = [0.15, 0.35, 0.35, 0.15];
        int lkIdx = 1;
        int hkIdx = 2;

        double theta = FugDistillationDesigner.SolveUnderwoodTheta(alpha, zFeed, qFeed, lkIdx, hkIdx);

        // Root theta must lie strictly between alpha_HK (1.0) and alpha_LK (2.2)
        Assert.True(theta > 1.0, $"Theta {theta:F6} must be > alpha_HK (1.0)");
        Assert.True(theta < 2.2, $"Theta {theta:F6} must be < alpha_LK (2.2)");

        // Underwood equation residual must be close to zero
        double residual = 0.0;
        for (int i = 0; i < alpha.Length; i++)
        {
            residual += (alpha[i] * zFeed[i]) / (alpha[i] - theta);
        }
        residual -= (1.0 - qFeed);

        Assert.True(Math.Abs(residual) < 1e-8, $"Underwood residual {residual:E4} was not within tolerance.");
    }

    [Fact]
    public void GillilandCorrelations_MolokanovAndEduljee_BehaveConsistentlyWithLiterature()
    {
        double nMin = 8.5;
        double rMin = 1.45;

        // At R = 1.30 * R_min
        double r1 = 1.30 * rMin;
        double nMolokanov = FugDistillationDesigner.CalculateGillilandStages(nMin, rMin, r1, GillilandCorrelationType.Molokanov);
        double nEduljee = FugDistillationDesigner.CalculateGillilandStages(nMin, rMin, r1, GillilandCorrelationType.Eduljee);

        Assert.True(nMolokanov > nMin, "Actual stages must exceed minimum stages.");
        Assert.True(nEduljee > nMin, "Actual stages must exceed minimum stages.");
        Assert.InRange(nMolokanov, 15.0, 26.0);
        Assert.InRange(nEduljee, 15.0, 26.0);

        // As R increases, N must strictly decrease towards N_min
        double r2 = 2.0 * rMin;
        double nHighReflux = FugDistillationDesigner.CalculateGillilandStages(nMin, rMin, r2, GillilandCorrelationType.Molokanov);
        Assert.True(nHighReflux < nMolokanov, $"N at high reflux ({nHighReflux:F1}) must be less than N at moderate reflux ({nMolokanov:F1}).");
    }

    [Fact]
    public void KirkbrideFeedStage_CalculatesValidStageIndices()
    {
        (double nr, double ns, int feedStage) = FugDistillationDesigner.CalculateKirkbrideFeedStage(
            zLK: 0.40,
            zHK: 0.40,
            xB_LK: 0.02,
            xD_HK: 0.02,
            totalBottoms: 50.0,
            totalDistillate: 50.0,
            totalStages: 16
        );

        Assert.True(nr > 0.0);
        Assert.True(ns > 0.0);
        Assert.Equal(16.0, nr + ns, 4);
        Assert.InRange(feedStage, 2, 15);
    }

    [Fact]
    public void ShortcutDistillationColumn_BinaryBenzeneToluene_SolvesAccurateDesign()
    {
        // Equimolar Benzene (LK, idx 0) / Toluene (HK, idx 1): 50 mol/s at 340 K, 101.325 kPa
        var compounds = new[] { DefaultCompounds.Benzene, DefaultCompounds.Toluene };
        var feed = new MaterialStream("S-FEED-FUG", "FUG Feed", compounds, 50.0, 340.0, 101.325, [0.50, 0.50]);
        var distillate = new MaterialStream("S-DIST-FUG", "FUG Distillate", compounds);
        var bottoms = new MaterialStream("S-BTM-FUG", "FUG Bottoms", compounds);
        feed.SolveEquilibrium(_raoult);

        var shortcut = new ShortcutDistillationColumn(
            id: "C-101-FUG",
            name: "Benzene-Toluene Shortcut Column",
            feedStream: feed,
            distillateOutlet: distillate,
            bottomsOutlet: bottoms,
            lightKeyIndex: 0,
            heavyKeyIndex: 1,
            lightKeyRecoveryDistillate: 0.98,
            heavyKeyRecoveryBottoms: 0.98,
            refluxRatioMultiplier: 1.25,
            columnPressureKpa: 101.325
        );

        shortcut.Solve(_raoult);

        Assert.NotNull(shortcut.DesignResult);
        Assert.True(shortcut.MinimumStagesNmin >= 5.0 && shortcut.MinimumStagesNmin <= 12.0, $"N_min was {shortcut.MinimumStagesNmin:F2}");
        Assert.True(shortcut.MinimumRefluxRatioRmin >= 0.8 && shortcut.MinimumRefluxRatioRmin <= 2.5, $"R_min was {shortcut.MinimumRefluxRatioRmin:F2}");
        Assert.True(shortcut.OperatingRefluxRatio > shortcut.MinimumRefluxRatioRmin);
        Assert.True(shortcut.RecommendedNumberOfStages >= 10 && shortcut.RecommendedNumberOfStages <= 30);
        Assert.InRange(shortcut.OptimalFeedStage, 2, shortcut.RecommendedNumberOfStages - 1);

        // Stream purity assertions (> 97% for specified keys)
        Assert.True(distillate.IsSolved);
        Assert.True(bottoms.IsSolved);
        Assert.True(distillate.CompositionZ[0] > 0.97, $"Distillate Benzene was {distillate.CompositionZ[0]:P2}");
        Assert.True(bottoms.CompositionZ[1] > 0.97, $"Bottoms Toluene was {bottoms.CompositionZ[1]:P2}");

        // Duty and Diameter
        Assert.True(shortcut.CondenserDutyWatts < 0.0, "Condenser duty must be cooling (< 0)");
        Assert.True(shortcut.ReboilerDutyWatts > 0.0, "Reboiler duty must be heating (> 0)");
        Assert.True(shortcut.EstimatedDiameterMeters > 0.2 && shortcut.EstimatedDiameterMeters < 3.0,
            $"Column diameter was {shortcut.EstimatedDiameterMeters:F2} m");

        // Component mass balances
        double bzIn = feed.GetComponentMolarFlowMolPerSec(0);
        double bzOut = distillate.GetComponentMolarFlowMolPerSec(0) + bottoms.GetComponentMolarFlowMolPerSec(0);
        Assert.Equal(bzIn, bzOut, 4);

        double tolIn = feed.GetComponentMolarFlowMolPerSec(1);
        double tolOut = distillate.GetComponentMolarFlowMolPerSec(1) + bottoms.GetComponentMolarFlowMolPerSec(1);
        Assert.Equal(tolIn, tolOut, 4);
    }

    [Fact]
    public void ShortcutDistillationColumn_MulticomponentHydrocarbon_Depentanizer()
    {
        // 5-component mixture:
        // 0: Propane (Light non-key)
        // 1: Isobutane (Light non-key)
        // 2: n-Butane (Light Key - LK)
        // 3: Isopentane (Heavy Key - HK)
        // 4: n-Pentane (Heavy non-key)
        var compounds = new[]
        {
            DefaultCompounds.Propane,
            DefaultCompounds.Isobutane,
            DefaultCompounds.NButane,
            DefaultCompounds.Isopentane,
            DefaultCompounds.NPentane
        };

        double[] zFeed = [0.08, 0.15, 0.32, 0.25, 0.20];
        var feed = new MaterialStream("S-HC-FEED", "Hydrocarbon Crude", compounds, 100.0, 320.0, 500.0, zFeed);
        var distillate = new MaterialStream("S-HC-DIST", "LPG Overhead", compounds);
        var bottoms = new MaterialStream("S-HC-BTM", "Heavy Pentanes Bottoms", compounds);
        feed.SolveEquilibrium(_raoult);

        var shortcut = new ShortcutDistillationColumn(
            id: "C-201-DEBUTANIZER",
            name: "LPG Debutanizer Column",
            feedStream: feed,
            distillateOutlet: distillate,
            bottomsOutlet: bottoms,
            lightKeyIndex: 2, // n-Butane
            heavyKeyIndex: 3, // Isopentane
            lightKeyRecoveryDistillate: 0.99,
            heavyKeyRecoveryBottoms: 0.99,
            refluxRatioMultiplier: 1.20,
            columnPressureKpa: 500.0
        );

        shortcut.Solve(_raoult);

        Assert.NotNull(shortcut.DesignResult);
        Assert.True(shortcut.MinimumStagesNmin > 5.0);
        Assert.True(shortcut.MinimumRefluxRatioRmin > 0.4);
        Assert.True(shortcut.RecommendedNumberOfStages >= 12);

        // Light non-keys (Propane, Isobutane) should distribute > 99.5% into overhead distillate
        double propFeed = feed.GetComponentMolarFlowMolPerSec(0);
        double propDist = distillate.GetComponentMolarFlowMolPerSec(0);
        Assert.True(propDist / propFeed > 0.99, $"Propane recovery in distillate was {propDist / propFeed:P2}");

        double iButFeed = feed.GetComponentMolarFlowMolPerSec(1);
        double iButDist = distillate.GetComponentMolarFlowMolPerSec(1);
        Assert.True(iButDist / iButFeed > 0.99, $"Isobutane recovery in distillate was {iButDist / iButFeed:P2}");

        // Heavy non-key (n-Pentane) should distribute > 99.5% into bottoms residue
        double nPentFeed = feed.GetComponentMolarFlowMolPerSec(4);
        double nPentBtm = bottoms.GetComponentMolarFlowMolPerSec(4);
        Assert.True(nPentBtm / nPentFeed > 0.99, $"n-Pentane recovery in bottoms was {nPentBtm / nPentFeed:P2}");

        // Keys satisfy specified recoveries
        double lkFeed = feed.GetComponentMolarFlowMolPerSec(2);
        double lkDist = distillate.GetComponentMolarFlowMolPerSec(2);
        Assert.True(lkDist / lkFeed >= 0.985);

        double hkFeed = feed.GetComponentMolarFlowMolPerSec(3);
        double hkBtm = bottoms.GetComponentMolarFlowMolPerSec(3);
        Assert.True(hkBtm / hkFeed >= 0.985);
    }

    [Fact]
    public void ShortcutDistillation_ToRigorousColumn_ConvergesWangHenkeMesh()
    {
        // 1. Solve Shortcut FUG Column for Benzene / Toluene
        var compounds = new[] { DefaultCompounds.Benzene, DefaultCompounds.Toluene };
        var feed = new MaterialStream("S-FEED-PAIR", "Feed Pair", compounds, 60.0, 340.0, 101.325, [0.50, 0.50]);
        var distShortcut = new MaterialStream("S-DIST-SHORT", "Dist Shortcut", compounds);
        var btmShortcut = new MaterialStream("S-BTM-SHORT", "Btm Shortcut", compounds);
        feed.SolveEquilibrium(_raoult);

        var shortcut = new ShortcutDistillationColumn(
            id: "C-SHORT",
            name: "Shortcut Initializer",
            feedStream: feed,
            distillateOutlet: distShortcut,
            bottomsOutlet: btmShortcut,
            lightKeyIndex: 0,
            heavyKeyIndex: 1,
            lightKeyRecoveryDistillate: 0.95,
            heavyKeyRecoveryBottoms: 0.95,
            refluxRatioMultiplier: 1.30,
            columnPressureKpa: 101.325
        );

        shortcut.Solve(_raoult);

        // 2. Generate Rigorous MESH Column from Shortcut Design
        var distRigorous = new MaterialStream("S-DIST-RIG", "Dist Rigorous", compounds);
        var btmRigorous = new MaterialStream("S-BTM-RIG", "Btm Rigorous", compounds);

        DistillationColumn rigorous = shortcut.ToRigorousColumn(
            id: "C-RIGOROUS",
            name: "Rigorous MESH Tower",
            distillateOutlet: distRigorous,
            bottomsOutlet: btmRigorous
        );

        // 3. Solve Rigorous Wang-Henke Bubble Point MESH
        rigorous.Solve(_raoult);

        // 4. Assert rigorous MESH convergence and consistency with shortcut specifications
        Assert.True(rigorous.IsConverged, $"Rigorous column failed to converge after {rigorous.Iterations} iters.");
        Assert.True(rigorous.Iterations <= 40);
        Assert.Equal(shortcut.RecommendedNumberOfStages, rigorous.NumberOfStages);
        Assert.Equal(shortcut.OptimalFeedStage, rigorous.FeedStage);

        // Rigorous purity closely tracks or exceeds shortcut target (>= 90%)
        Assert.True(distRigorous.CompositionZ[0] > 0.90, $"Rigorous Benzene purity was {distRigorous.CompositionZ[0]:P2}");
        Assert.True(btmRigorous.CompositionZ[1] > 0.90, $"Rigorous Toluene purity was {btmRigorous.CompositionZ[1]:P2}");

        // Rigorous diameter is computed
        Assert.True(rigorous.EstimatedDiameterMeters > 0.1);
    }

    [Fact]
    public void ShortcutDistillation_InProcessFlowsheet_SolvesTopologicalDAGAndGeneratesReport()
    {
        var compounds = new[] { DefaultCompounds.Benzene, DefaultCompounds.Toluene };
        var feedStream = new MaterialStream("S-CRUDE", "Crude Stream", compounds, 50.0, 340.0, 101.325, [0.50, 0.50]);
        var distStream = new MaterialStream("S-DIST", "Distillate Product", compounds);
        var btmStream = new MaterialStream("S-BTM", "Bottoms Residue", compounds);

        var shortcutCol = new ShortcutDistillationColumn(
            id: "C-FUG-01",
            name: "FUG Fractionator",
            feedStream: feedStream,
            distillateOutlet: distStream,
            bottomsOutlet: btmStream,
            lightKeyIndex: 0,
            heavyKeyIndex: 1,
            lightKeyRecoveryDistillate: 0.98,
            heavyKeyRecoveryBottoms: 0.98,
            refluxRatioMultiplier: 1.25
        );

        var flowsheet = new ProcessFlowsheet("FUG Fractionation Train", _raoult);
        flowsheet.AddUnit(shortcutCol);

        IReadOnlyList<IUnitOperation> order = flowsheet.Solve();
        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();
        string dot = flowsheet.ToDot();

        Assert.Single(order);
        Assert.Equal("C-FUG-01", order[0].Id);
        Assert.True(report.IsMassConserved);
        Assert.True(report.IsEnergyConserved);
        Assert.True(report.GlobalBalance.RelativeMassError < 1e-6);

        Assert.Contains("ShortcutDistillationColumn", dot);
        Assert.Contains("#DDD6FE", dot);
        Assert.Contains("FUG:", dot);
    }
}
