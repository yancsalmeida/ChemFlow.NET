using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;
using ChemFlow.Domain.ValueObjects;
using ChemFlow.Infrastructure.Persistence;
using ChemFlow.Infrastructure.Repositories;

namespace ChemFlow.Tests;

public class FlowsheetTopologyAndBalanceTests
{
    private readonly IThermodynamicPackage _thermo = new RaoultThermodynamicPackage();
    private readonly IReadOnlyList<ChemicalCompound> _compounds =
    [
        DefaultCompounds.Benzene,
        DefaultCompounds.Toluene
    ];

    private (ProcessFlowsheet flowsheet, MaterialStream feed1, MaterialStream feed2, MaterialStream vapor, MaterialStream liquid) CreateSolvedSeparationPlant()
    {
        var feed1 = new MaterialStream("S-101", "Benzene-Rich Feed", _compounds, 60.0, 300.0, 120.0, [0.85, 0.15]);
        var feed2 = new MaterialStream("S-102", "Toluene-Rich Feed", _compounds, 40.0, 310.0, 120.0, [0.15, 0.85]);
        var mixed = new MaterialStream("S-103", "Mixed Feed", _compounds);
        var heated = new MaterialStream("S-104", "Preheated Feed", _compounds);
        var vapor = new MaterialStream("S-105", "Overhead Vapor", _compounds);
        var liquid = new MaterialStream("S-106", "Bottoms Liquid", _compounds);

        var mixer = new Mixer("M-100", "Feed Mixer", [feed1, feed2], mixed);
        var preheater = new HeaterCooler("E-100", "Feed Preheater", mixed, heated, targetTemperatureKelvin: 368.15, pressureDropKpa: 5.0);
        var flashDrum = new FlashDrum("V-100", "VLE Separator", heated, vapor, liquid, operatingPressureKpa: 101.325);

        var flowsheet = new ProcessFlowsheet("Benzene-Toluene Separation Plant", _thermo);
        flowsheet.AddUnit(mixer);
        flowsheet.AddUnit(preheater);
        flowsheet.AddUnit(flashDrum);

        flowsheet.Solve();

        return (flowsheet, feed1, feed2, vapor, liquid);
    }

    [Fact]
    public void GetTopology_ExtractsCorrectNodesEdgesAndAdjacency()
    {
        // Arrange
        var (flowsheet, _, _, _, _) = CreateSolvedSeparationPlant();

        // Act
        FlowsheetTopology topology = flowsheet.GetTopology();

        // Assert: Graph metadata
        Assert.Equal("Benzene-Toluene Separation Plant", topology.FlowsheetName);
        Assert.True(topology.IsAcyclic);
        Assert.Equal(["M-100", "E-100", "V-100"], topology.ExecutionOrder);

        // Assert: Units
        Assert.Equal(3, topology.Units.Count);
        var mixerNode = topology.Units.Single(u => u.Id == "M-100");
        Assert.Equal("Mixer", mixerNode.UnitType);
        Assert.Equal(["S-101", "S-102"], mixerNode.InletStreamIds);
        Assert.Equal(["S-103"], mixerNode.OutletStreamIds);

        // Assert: Streams
        Assert.Equal(6, topology.Streams.Count);
        var feedStream = topology.Streams.Single(s => s.StreamId == "S-101");
        Assert.True(feedStream.IsExternalFeed);
        Assert.False(feedStream.IsFinalProduct);
        Assert.Null(feedStream.SourceUnitId);
        Assert.Equal("M-100", feedStream.TargetUnitId);
        Assert.True(feedStream.MassFlowKgPerSec > 0.0);

        var vaporProduct = topology.Streams.Single(s => s.StreamId == "S-105");
        Assert.False(vaporProduct.IsExternalFeed);
        Assert.True(vaporProduct.IsFinalProduct);
        Assert.Equal("V-100", vaporProduct.SourceUnitId);
        Assert.Null(vaporProduct.TargetUnitId);

        // Assert: Adjacency
        Assert.Contains("E-100", topology.AdjacencyList["M-100"]);
        Assert.Contains("V-100", topology.AdjacencyList["E-100"]);
        Assert.Empty(topology.AdjacencyList["V-100"]);
    }

    [Fact]
    public void ToDot_GeneratesSyntacticallyValidGraphvizRepresentation()
    {
        // Arrange
        var (flowsheet, _, _, _, _) = CreateSolvedSeparationPlant();

        // Act
        string dot = flowsheet.ToDot();

        // Assert
        Assert.StartsWith("digraph \"Benzene-Toluene Separation Plant\"", dot);
        Assert.Contains("rankdir=LR;", dot);
        Assert.Contains("subgraph cluster_feeds", dot);
        Assert.Contains("subgraph cluster_products", dot);
        Assert.Contains("\"M_100\" [label=\"{M-100 (Feed Mixer)|Mixer", dot);
        Assert.Contains("\"E_100\" [label=\"{E-100 (Feed Preheater)|HeaterCooler", dot);
        Assert.Contains("\"V_100\" [label=\"{V-100 (VLE Separator)|FlashDrum", dot);
        Assert.Contains("\"feed_S_101\" -> \"M_100\"", dot);
        Assert.Contains("\"M_100\" -> \"E_100\"", dot);
        Assert.Contains("\"E_100\" -> \"V_100\"", dot);
        Assert.Contains("\"V_100\" -> \"prod_S_105\"", dot);
        Assert.Contains("\"V_100\" -> \"prod_S_106\"", dot);
        Assert.EndsWith("}\n", dot);
    }

    [Fact]
    public void GenerateBalanceReport_ValidatesGlobalMassAndEnergyConservation()
    {
        // Arrange
        var (flowsheet, feed1, feed2, vapor, liquid) = CreateSolvedSeparationPlant();

        // Act
        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();

        // Assert: Overall status
        Assert.True(report.IsMassConserved);
        Assert.True(report.IsEnergyConserved);

        // Assert: Global mass balance closure (Feeds == Products within 1e-6)
        double totalFeedMass = feed1.MassFlowRateKgPerSec + feed2.MassFlowRateKgPerSec;
        double totalProductMass = vapor.MassFlowRateKgPerSec + liquid.MassFlowRateKgPerSec;
        Assert.Equal(totalFeedMass, report.GlobalBalance.TotalFeedMassFlowKgPerSec, precision: 6);
        Assert.Equal(totalProductMass, report.GlobalBalance.TotalProductMassFlowKgPerSec, precision: 6);
        Assert.True(report.GlobalBalance.RelativeMassError < 1e-6);
        Assert.True(report.GlobalBalance.IsMassBalanced);

        // Assert: Global energy balance closure (H_feed + Q_units == H_products within 1 W)
        Assert.True(report.GlobalBalance.RelativeEnthalpyError < 1e-4);
        Assert.True(Math.Abs(report.GlobalBalance.EnthalpyDeltaWatts) < 1.0);
        Assert.True(report.GlobalBalance.IsEnergyBalanced);

        // Assert: Component species balances
        Assert.Equal(2, report.ComponentBalances.Count);
        foreach (ComponentBalanceSummary comp in report.ComponentBalances)
        {
            Assert.True(comp.IsConserved);
            Assert.True(comp.RelativeMassError < 1e-6);
            Assert.True(Math.Abs(comp.MassDeltaKgPerSec) < 1e-6);
        }

        // Assert: Unit local balances
        Assert.Equal(3, report.UnitBalances.Count);
        foreach (UnitBalanceSummary unit in report.UnitBalances)
        {
            Assert.True(unit.IsMassBalanced);
            Assert.True(unit.IsEnergyBalanced);
            Assert.True(unit.RelativeMassError < 1e-6);
            Assert.True(Math.Abs(unit.EnthalpyDeltaWatts) < 1.0);
        }
    }

    [Fact]
    public void GenerateBalanceReport_WithConversionReactor_ConservesTotalPlantMass()
    {
        // Arrange: Model catalytic isomerization reaction A -> B (same molar mass) with 50% conversion
        var isomerA = new ChemicalCompound("ISO-A", "Isomer A", "C6H14", 86.178, 507.6, 3025.0, 0.301, 195.6, 143.1, 28850.0, new AntoineCoefficients(13.8193, 2696.04, -48.83));
        var isomerB = new ChemicalCompound("ISO-B", "Isomer B", "C6H14", 86.178, 507.6, 3025.0, 0.301, 195.6, 143.1, 28850.0, new AntoineCoefficients(13.8193, 2696.04, -48.83));
        IReadOnlyList<ChemicalCompound> isomerCompounds = [isomerA, isomerB];

        var feed = new MaterialStream("R-IN", "Reactor Inlet", isomerCompounds, 100.0, 320.0, 150.0, [0.80, 0.20]);
        var product = new MaterialStream("R-OUT", "Reactor Outlet", isomerCompounds);

        var stoichiometry = new Dictionary<string, double>
        {
            ["ISO-A"] = -1.0,
            ["ISO-B"] = +1.0
        };

        var reactor = new ConversionReactor(
            id: "R-101",
            name: "Catalytic Isomerization Reactor",
            inletStream: feed,
            outletStream: product,
            keyReactantId: "ISO-A",
            fractionalConversion: 0.50,
            stoichiometry: stoichiometry,
            outletTemperatureKelvin: 350.0,
            standardReactionEnthalpyJPerMol: -25000.0);

        var flowsheet = new ProcessFlowsheet("Reactor Flowsheet", _thermo);
        flowsheet.AddUnit(reactor);
        flowsheet.Solve();

        // Act
        FlowsheetBalanceReport report = flowsheet.GenerateBalanceReport();

        // Assert: Total mass in == Total mass out
        Assert.True(report.GlobalBalance.IsMassBalanced);
        Assert.Equal(feed.MassFlowRateKgPerSec, product.MassFlowRateKgPerSec, precision: 6);
        Assert.True(report.UnitBalances[0].IsMassBalanced);
    }

    [Fact]
    public void SqliteSimulationHistoryRepository_SavesAndRetrievesTopologyAndBalanceReport()
    {
        // Arrange: Temporary SQLite test database
        string testDbName = $"test_history_{Guid.NewGuid():N}.db";
        string connStr = $"Data Source={testDbName}";
        var initializer = new SqliteDatabaseInitializer(connStr);
        initializer.InitializeAndSeed();

        var repo = new SqliteSimulationHistoryRepository(initializer);
        var (flowsheet, _, _, _, _) = CreateSolvedSeparationPlant();
        IReadOnlyList<IUnitOperation> executedOrder = flowsheet.GetTopologicalExecutionOrder();

        try
        {
            // Act: Save run
            long runId = repo.SaveSimulationRun(flowsheet, executedOrder);
            Assert.True(runId > 0);

            // Act: Retrieve record
            SimulationRunRecord? record = repo.GetById(runId);
            Assert.NotNull(record);
            Assert.Equal("Benzene-Toluene Separation Plant", record.FlowsheetName);
            Assert.NotNull(record.TopologyDot);
            Assert.Contains("digraph", record.TopologyDot);

            // Act: Retrieve topology model
            FlowsheetTopology? topology = repo.GetTopology(runId);
            Assert.NotNull(topology);
            Assert.Equal(3, topology.Units.Count);
            Assert.Equal(6, topology.Streams.Count);
            Assert.True(topology.IsAcyclic);

            // Act: Retrieve balance report model
            FlowsheetBalanceReport? report = repo.GetBalanceReport(runId);
            Assert.NotNull(report);
            Assert.True(report.IsMassConserved);
            Assert.True(report.IsEnergyConserved);
            Assert.Equal(2, report.ComponentBalances.Count);
            Assert.Equal(3, report.UnitBalances.Count);
        }
        finally
        {
            if (File.Exists(testDbName))
                File.Delete(testDbName);
        }
    }
}
