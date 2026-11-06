using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;

namespace ChemFlow.Tests;

public class RecycleConvergenceTests
{
    private readonly IThermodynamicPackage _thermo = new RaoultThermodynamicPackage();
    private readonly IReadOnlyList<ChemicalCompound> _compounds =
    [
        DefaultCompounds.Benzene,
        DefaultCompounds.Toluene
    ];

    [Fact]
    public void Accelerator_DirectSubstitution_ReturnsCalculatedStateDirectly()
    {
        var options = new RecycleSolverOptions(Method: RecycleConvergenceMethod.DirectSubstitution);
        var accelerator = new WegsteinRecycleAccelerator(options);

        var guess1 = new StreamStateVector(10.0, 300.0, 101.325, [0.5, 0.5]);
        var calc1 = new StreamStateVector(15.0, 310.0, 101.325, [0.4, 0.6]);

        StreamStateVector next = accelerator.Accelerate("S-REC", guess1, calc1, iteration: 1);
        Assert.Equal(15.0, next.MolarFlowRateMolPerSec);
        Assert.Equal(310.0, next.TemperatureKelvin);

        var guess2 = next;
        var calc2 = new StreamStateVector(18.0, 315.0, 101.325, [0.35, 0.65]);
        StreamStateVector next2 = accelerator.Accelerate("S-REC", guess2, calc2, iteration: 2);
        // Direct substitution always copies calc state
        Assert.Equal(18.0, next2.MolarFlowRateMolPerSec);
        Assert.Equal(315.0, next2.TemperatureKelvin);
    }

    [Fact]
    public void Accelerator_Wegstein_AcceleratesAndClampsSecant()
    {
        var options = new RecycleSolverOptions(
            Method: RecycleConvergenceMethod.Wegstein,
            WegsteinLowerBound: -5.0,
            WegsteinUpperBound: 0.0
        );
        var accelerator = new WegsteinRecycleAccelerator(options);

        // Iteration 1: initial step saves history and returns calc1
        var x1 = new StreamStateVector(10.0, 300.0, 101.325, [0.5, 0.5]);
        var y1 = new StreamStateVector(15.0, 300.0, 101.325, [0.5, 0.5]);
        var x2 = accelerator.Accelerate("S-1", x1, y1, iteration: 1);
        Assert.Equal(15.0, x2.MolarFlowRateMolPerSec);

        // Iteration 2:
        // prevX = 10, curX = 15 => dx = 5
        // prevY = 15, curY = 17 => dy = 2
        // s = dy / dx = 2 / 5 = 0.4
        // q = s / (s - 1) = 0.4 / (0.4 - 1.0) = 0.4 / (-0.6) = -0.66667
        // Bound: within [-5.0, 0.0] => q = -0.66667
        // nextVal = q * curX + (1 - q) * curY = -0.66667 * 15 + (1 - (-0.66667)) * 17 = -10 + 1.66667 * 17 = 18.33333
        var y2 = new StreamStateVector(17.0, 300.0, 101.325, [0.5, 0.5]);
        var x3 = accelerator.Accelerate("S-1", x2, y2, iteration: 2);

        Assert.True(x3.MolarFlowRateMolPerSec > 17.0, "Wegstein should extrapolate beyond y2 when 0 < s < 1");
        Assert.InRange(x3.MolarFlowRateMolPerSec, 18.3, 18.4);
    }

    private (ProcessFlowsheet flowsheet, MaterialStream freshFeed, MaterialStream recycleStream, MaterialStream vaporProduct, MaterialStream liquidProduct)
        CreateRecycleSeparationFlowsheet(string flowsheetName = "Recycle Separation Plant")
    {
        var freshFeed = new MaterialStream("S-01", "Fresh Feed", _compounds, 50.0, 320.0, 120.0, [0.60, 0.40]);
        var recycleStream = new MaterialStream("S-04", "Recycle Stream", _compounds, 20.0, 350.0, 101.325, [0.30, 0.70]);
        var mixedFeed = new MaterialStream("S-02", "Mixed Feed", _compounds);
        var vaporProduct = new MaterialStream("S-03", "Vapor Distillate", _compounds);
        var liquidBottoms = new MaterialStream("S-BTM", "Separator Bottoms", _compounds);
        var liquidProduct = new MaterialStream("S-05", "Liquid Purge Product", _compounds);

        var mixer = new Mixer("M-01", "Feed & Recycle Mixer", [freshFeed, recycleStream], mixedFeed);
        var flashDrum = new FlashDrum("V-01", "Flash Column", mixedFeed, vaporProduct, liquidBottoms, operatingTemperatureKelvin: 365.0, operatingPressureKpa: 101.325);
        // Split liquid bottoms: 65% recycled back to mixer, 35% purged as product
        var splitter = new Splitter("SP-01", "Recycle Splitter", liquidBottoms, [recycleStream, liquidProduct], [0.65, 0.35]);

        var flowsheet = new ProcessFlowsheet(flowsheetName, _thermo);
        flowsheet.AddUnit(mixer);
        flowsheet.AddUnit(flashDrum);
        flowsheet.AddUnit(splitter);

        return (flowsheet, freshFeed, recycleStream, vaporProduct, liquidProduct);
    }

    [Fact]
    public void ProcessFlowsheet_UnTornCycle_ThrowsInvalidOperationException()
    {
        // Arrange
        var (flowsheet, _, _, _, _) = CreateRecycleSeparationFlowsheet();

        // Act & Assert: Kahn's algorithm without ignoreTearStreams detects cycle
        var ex = Assert.Throws<InvalidOperationException>(() => flowsheet.GetTopologicalExecutionOrder(ignoreTearStreams: false));
        Assert.Contains("Cycle detected", ex.Message);
    }

    [Fact]
    public void ProcessFlowsheet_SolveWithRecycle_ExplicitTearStream_ConvergesWithMassAndEnergyConservation()
    {
        // Arrange
        var (flowsheet, freshFeed, recycleStream, vaporProd, liquidProd) = CreateRecycleSeparationFlowsheet();
        flowsheet.AddTearStream(recycleStream);

        Assert.True(flowsheet.IsTearStream("S-04"));
        Assert.Contains("S-04", flowsheet.TearStreamIds);

        // Act
        var options = new RecycleSolverOptions(
            Method: RecycleConvergenceMethod.Wegstein,
            MaxIterations: 40,
            Tolerance: 1e-5
        );
        FlowsheetSolveResult result = flowsheet.SolveWithRecycle(options);

        // Assert: Convergence
        Assert.True(result.HasRecycle);
        Assert.True(result.IsConverged, $"Recycle did not converge after {result.Iterations} iterations.");
        Assert.True(result.Iterations <= 25, $"Wegstein should converge in fewer than 25 iterations, took {result.Iterations}.");
        Assert.NotEmpty(result.ConvergenceHistory);

        // Assert: Stream states are physically sound
        Assert.True(recycleStream.IsSolved);
        Assert.True(recycleStream.MolarFlowRateMolPerSec > 0.0);
        Assert.True(vaporProd.MolarFlowRateMolPerSec > 0.0);
        Assert.True(liquidProd.MolarFlowRateMolPerSec > 0.0);

        // Assert: Global Mass Balance Closure
        FlowsheetBalanceReport balanceReport = flowsheet.GenerateBalanceReport();
        Assert.True(balanceReport.IsMassConserved, $"Mass balance not closed: {balanceReport.GlobalBalance.RelativeMassError:E3}");
        Assert.True(balanceReport.IsEnergyConserved, $"Energy balance not closed: {balanceReport.GlobalBalance.RelativeEnthalpyError:E3}");

        // Total input mass = Total output mass within solver tolerance
        double massIn = freshFeed.MassFlowRateKgPerSec;
        double massOut = vaporProd.MassFlowRateKgPerSec + liquidProd.MassFlowRateKgPerSec;
        double massErrorRel = Math.Abs(massIn - massOut) / massIn;
        Assert.True(massErrorRel < 1e-4, $"Plant mass balance error: {massErrorRel:E3}");
    }

    [Fact]
    public void ProcessFlowsheet_Solve_AutomaticallyDetectsAndTearsRecycleLoop()
    {
        // Arrange: flowsheet without explicit AddTearStream
        var (flowsheet, _, recycleStream, _, _) = CreateRecycleSeparationFlowsheet("Auto Tear Plant");

        // Act: Solve() should auto-identify back-edge tear stream and solve
        IReadOnlyList<IUnitOperation> order = flowsheet.Solve();

        // Assert
        Assert.NotEmpty(order);
        Assert.True(recycleStream.IsSolved);
        Assert.True(recycleStream.MolarFlowRateMolPerSec > 0.0);

        FlowsheetBalanceReport balance = flowsheet.GenerateBalanceReport();
        Assert.True(balance.IsMassConserved);
    }

    [Fact]
    public void ProcessFlowsheet_Wegstein_ConvergesFasterThanDirectSubstitution()
    {
        // Run with Direct Substitution
        var (fsDirect, _, recDirect, _, _) = CreateRecycleSeparationFlowsheet("Direct Substitution Plant");
        fsDirect.AddTearStream(recDirect);
        var directResult = fsDirect.SolveWithRecycle(new RecycleSolverOptions(
            Method: RecycleConvergenceMethod.DirectSubstitution,
            MaxIterations: 60,
            Tolerance: 1e-5
        ));

        // Run with Wegstein
        var (fsWegstein, _, recWegstein, _, _) = CreateRecycleSeparationFlowsheet("Wegstein Plant");
        fsWegstein.AddTearStream(recWegstein);
        var wegsteinResult = fsWegstein.SolveWithRecycle(new RecycleSolverOptions(
            Method: RecycleConvergenceMethod.Wegstein,
            MaxIterations: 60,
            Tolerance: 1e-5
        ));

        Assert.True(directResult.IsConverged);
        Assert.True(wegsteinResult.IsConverged);
        Assert.True(
            wegsteinResult.Iterations < directResult.Iterations,
            $"Wegstein ({wegsteinResult.Iterations} iters) should converge in fewer iterations than Direct Substitution ({directResult.Iterations} iters)."
        );
    }

    [Fact]
    public void ProcessFlowsheet_ToDot_IncludesRecycleStreamStyling()
    {
        // Arrange
        var (flowsheet, _, recycleStream, _, _) = CreateRecycleSeparationFlowsheet("DOT Styled Plant");
        flowsheet.AddTearStream(recycleStream);
        flowsheet.Solve();

        // Act
        string dot = flowsheet.ToDot();

        // Assert: Graphviz DOT contains tear stream purple styling and label
        Assert.Contains("color=\"#a855f7\"", dot);
        Assert.Contains("style=\"dashed\"", dot);
        Assert.Contains("[Recycle/Tear]", dot);
    }
}
