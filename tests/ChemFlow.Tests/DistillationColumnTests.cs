using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Mathematics;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;

namespace ChemFlow.Tests;

public class DistillationColumnTests
{
    private readonly IThermodynamicPackage _raoult = new RaoultThermodynamicPackage();
    private readonly IThermodynamicPackage _pengRobinson = new PengRobinsonThermodynamicPackage();
    private readonly IReadOnlyList<ChemicalCompound> _compounds =
    [
        DefaultCompounds.Benzene,
        DefaultCompounds.Toluene
    ];

    [Fact]
    public void ThomasAlgorithm_SolvesStandardTridiagonalSystem_Accurately()
    {
        // System:
        // [ 2 -1  0 ] [x1]   [ 1 ]
        // [-1  2 -1 ] [x2] = [ 0 ]
        // [ 0 -1  2 ] [x3]   [ 1 ]
        // Analytical solution is x = [1, 1, 1]
        double[] a = [0.0, -1.0, -1.0];
        double[] b = [2.0, 2.0, 2.0];
        double[] c = [-1.0, -1.0, 0.0];
        double[] d = [1.0, 0.0, 1.0];

        double[] x = ThomasAlgorithm.Solve(a, b, c, d);

        Assert.Equal(3, x.Length);
        Assert.Equal(1.0, x[0], 6);
        Assert.Equal(1.0, x[1], 6);
        Assert.Equal(1.0, x[2], 6);
    }

    [Fact]
    public void DistillationColumn_RaoultPackage_SolvesBinarySeparation_EnrichesDistillateAndBottoms()
    {
        // Arrange
        // Equimolar Benzene/Toluene feed: 50 mol/s at 340 K (bubble point is ~365 K)
        var feed = new MaterialStream("S-FEED", "Column Feed", _compounds, 50.0, 340.0, 101.325, [0.50, 0.50]);
        var distillate = new MaterialStream("S-DIST", "Overhead Distillate", _compounds);
        var bottoms = new MaterialStream("S-BTM", "Bottoms Residue", _compounds);
        feed.SolveEquilibrium(_raoult);

        var column = new DistillationColumn(
            id: "C-100",
            name: "Benzene-Toluene Fractionator",
            feedStream: feed,
            distillateOutlet: distillate,
            bottomsOutlet: bottoms,
            numberOfStages: 10,
            feedStage: 5,
            refluxRatio: 2.0,
            distillateRateMolPerSec: 25.0,
            columnPressureKpa: 101.325
        );

        // Act
        column.Solve(_raoult);

        // Assert: Convergence
        Assert.True(column.IsConverged, $"Column failed to converge after {column.Iterations} iterations.");
        Assert.True(column.Iterations <= 40, $"Column converged in {column.Iterations} iterations, expected <= 40.");

        // Assert: Stream states
        Assert.True(distillate.IsSolved);
        Assert.True(bottoms.IsSolved);
        Assert.Equal(25.0, distillate.MolarFlowRateMolPerSec, 4);
        Assert.Equal(25.0, bottoms.MolarFlowRateMolPerSec, 4);

        // Distillate should be strongly enriched in Benzene (Light Key, > 90%)
        Assert.True(distillate.CompositionZ[0] > 0.90, $"Distillate Benzene purity was {distillate.CompositionZ[0]:P2}");

        // Bottoms should be strongly enriched in Toluene (Heavy Key, > 90%)
        Assert.True(bottoms.CompositionZ[1] > 0.90, $"Bottoms Toluene purity was {bottoms.CompositionZ[1]:P2}");

        // Condenser duty must be cooling (< 0) and Reboiler duty heating (> 0)
        Assert.True(column.CondenserDutyWatts < 0.0, "Condenser duty must be negative (cooling)");
        Assert.True(column.ReboilerDutyWatts > 0.0, "Reboiler duty must be positive (heating)");

        // Temperature profile must increase monotonically down the column
        Assert.Equal(10, column.StageProfiles.Count);
        for (int i = 0; i < column.StageProfiles.Count - 1; i++)
        {
            Assert.True(
                column.StageProfiles[i].TemperatureKelvin <= column.StageProfiles[i + 1].TemperatureKelvin + 0.1,
                $"Stage {i + 1} T ({column.StageProfiles[i].TemperatureKelvin:F1} K) was higher than stage {i + 2} T ({column.StageProfiles[i + 1].TemperatureKelvin:F1} K)"
            );
        }

        // Component mole balance closure
        double benzeneIn = feed.GetComponentMolarFlowMolPerSec(0);
        double benzeneOut = distillate.GetComponentMolarFlowMolPerSec(0) + bottoms.GetComponentMolarFlowMolPerSec(0);
        Assert.Equal(benzeneIn, benzeneOut, 4);

        double tolueneIn = feed.GetComponentMolarFlowMolPerSec(1);
        double tolueneOut = distillate.GetComponentMolarFlowMolPerSec(1) + bottoms.GetComponentMolarFlowMolPerSec(1);
        Assert.Equal(tolueneIn, tolueneOut, 4);

        // First-Law overall energy closure
        double enthalpyIn = feed.TotalEnthalpyFlowWatts + column.EnergyDutyWatts;
        double enthalpyOut = distillate.TotalEnthalpyFlowWatts + bottoms.TotalEnthalpyFlowWatts;
        Assert.Equal(enthalpyIn, enthalpyOut, 1);
    }

    [Fact]
    public void DistillationColumn_PengRobinsonPackage_ConvergesRigorousRealFluidVLE()
    {
        // Arrange
        var feed = new MaterialStream("S-FEED-PR", "PR Column Feed", _compounds, 60.0, 335.0, 101.325, [0.55, 0.45]);
        var distillate = new MaterialStream("S-DIST-PR", "PR Distillate", _compounds);
        var bottoms = new MaterialStream("S-BTM-PR", "PR Bottoms", _compounds);
        feed.SolveEquilibrium(_pengRobinson);

        var column = new DistillationColumn(
            id: "C-200",
            name: "PR Fractionator Column",
            feedStream: feed,
            distillateOutlet: distillate,
            bottomsOutlet: bottoms,
            numberOfStages: 12,
            feedStage: 6,
            refluxRatio: 2.5,
            distillateRateMolPerSec: 33.0,
            columnPressureKpa: 101.325
        );

        // Act
        column.Solve(_pengRobinson);

        // Assert
        Assert.True(column.IsConverged, $"PR column failed to converge after {column.Iterations} iterations.");
        Assert.True(distillate.CompositionZ[0] > 0.85);
        Assert.True(bottoms.CompositionZ[1] > 0.85);
        Assert.True(column.CondenserDutyWatts < 0.0);
        Assert.True(column.ReboilerDutyWatts > 0.0);
    }

    [Fact]
    public void DistillationColumn_InFlowsheet_SolvesTopologicalDAGAndPassesConservationReport()
    {
        // Arrange
        var feedStream = new MaterialStream("S-10", "Plant Crude Feed", _compounds, 50.0, 340.0, 101.325, [0.50, 0.50]);
        var distillate = new MaterialStream("S-11", "High-Purity Distillate", _compounds);
        var bottoms = new MaterialStream("S-12", "Purified Bottoms", _compounds);

        var column = new DistillationColumn(
            id: "COL-01",
            name: "Crude Distillation Unit",
            feedStream: feedStream,
            distillateOutlet: distillate,
            bottomsOutlet: bottoms,
            numberOfStages: 10,
            feedStage: 5,
            refluxRatio: 2.0,
            distillateRateMolPerSec: 25.0
        );

        var flowsheet = new ProcessFlowsheet("Fractionation Plant", _raoult);
        flowsheet.AddUnit(column);

        // Act
        IReadOnlyList<IUnitOperation> order = flowsheet.Solve();
        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();
        string dot = flowsheet.ToDot();

        // Assert
        Assert.Single(order);
        Assert.Equal("COL-01", order[0].Id);
        Assert.True(report.IsMassConserved);
        Assert.True(report.IsEnergyConserved);
        Assert.True(report.GlobalBalance.RelativeMassError < 1e-6);

        // Graphviz DOT styling verification
        Assert.Contains("DistillationColumn", dot);
        Assert.Contains("#EDE9FE", dot);
        Assert.Contains("Qc=", dot);
        Assert.Contains("Qr=", dot);
    }

    [Fact]
    public void DistillationColumn_InvalidParameters_ThrowsArgumentOutOfRangeException()
    {
        var feed = new MaterialStream("S-F", "Feed", _compounds, 50.0, 300.0, 101.325, [0.5, 0.5]);
        var d = new MaterialStream("S-D", "Dist", _compounds);
        var b = new MaterialStream("S-B", "Btm", _compounds);

        // Fewer than 3 stages
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DistillationColumn("C-1", "Col", feed, d, b, numberOfStages: 2, feedStage: 1));

        // Feed stage out of range
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DistillationColumn("C-1", "Col", feed, d, b, numberOfStages: 10, feedStage: 10));

        // Non-positive reflux ratio
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DistillationColumn("C-1", "Col", feed, d, b, numberOfStages: 10, feedStage: 5, refluxRatio: -1.0));
    }
}
