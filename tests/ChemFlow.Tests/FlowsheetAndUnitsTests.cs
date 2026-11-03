using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;

namespace ChemFlow.Tests;

public class FlowsheetAndUnitsTests
{
    private readonly IThermodynamicPackage _thermo = new RaoultThermodynamicPackage();
    private readonly IReadOnlyList<ChemicalCompound> _compounds =
    [
        DefaultCompounds.Benzene,
        DefaultCompounds.Toluene
    ];

    [Fact]
    public void Mixer_AdiabaticMixing_ConservesMassAndEnthalpy()
    {
        // Arrange: Stream 1 (60 mol/s at 310 K, 80% Benzene) + Stream 2 (40 mol/s at 350 K, 20% Benzene)
        var s1 = new MaterialStream("S1", "Cold Benzene-Rich Feed", _compounds, 60.0, 310.0, 150.0, [0.80, 0.20]);
        var s2 = new MaterialStream("S2", "Hot Toluene-Rich Feed", _compounds, 40.0, 350.0, 120.0, [0.20, 0.80]);
        var sOut = new MaterialStream("S3", "Mixed Stream", _compounds);

        s1.SolveEquilibrium(_thermo);
        s2.SolveEquilibrium(_thermo);

        var mixer = new Mixer("MIX-101", "Feed Mixer", [s1, s2], sOut);

        // Act
        mixer.Solve(_thermo);

        // Assert: Total flow = 100 mol/s, P_out = min(150, 120) = 120 kPa
        Assert.Equal(100.0, sOut.MolarFlowRateMolPerSec, precision: 6);
        Assert.Equal(120.0, sOut.PressureKpa, precision: 6);

        // Component balance: (60*0.8 + 40*0.2) / 100 = 0.56 Benzene
        Assert.Equal(0.56, sOut.CompositionZ[0], precision: 6);
        Assert.Equal(0.44, sOut.CompositionZ[1], precision: 6);

        // Adiabatic energy balance: H_dot_out == H_dot_1 + H_dot_2 (within 0.1 W)
        double expectedEnthalpyWatts = s1.TotalEnthalpyFlowWatts + s2.TotalEnthalpyFlowWatts;
        Assert.InRange(sOut.TotalEnthalpyFlowWatts, expectedEnthalpyWatts - 0.5, expectedEnthalpyWatts + 0.5);
    }

    [Fact]
    public void ConversionReactor_StoichiometricReaction_ComputesCorrectOutletComposition()
    {
        // Arrange: Isomerization / model reaction A -> B with 75% conversion of A
        var feed = new MaterialStream("R-IN", "Reactor Feed", _compounds, 100.0, 340.0, 200.0, [0.80, 0.20]);
        var product = new MaterialStream("R-OUT", "Reactor Effluent", _compounds);
        feed.SolveEquilibrium(_thermo);

        var stoichiometry = new Dictionary<string, double>
        {
            ["C6H6"] = -1.0,
            ["C7H8"] = +1.0
        };

        var reactor = new ConversionReactor(
            id: "R-101",
            name: "Conversion Reactor",
            inletStream: feed,
            outletStream: product,
            keyReactantId: "C6H6",
            fractionalConversion: 0.75,
            stoichiometry: stoichiometry,
            outletTemperatureKelvin: 360.0,
            standardReactionEnthalpyJPerMol: -50000.0);

        // Act
        reactor.Solve(_thermo);

        // Assert: 80 mol/s C6H6 in; 75% reacts (60 mol/s) -> 20 mol/s C6H6 left, 20 + 60 = 80 mol/s C7H8 out
        Assert.Equal(100.0, product.MolarFlowRateMolPerSec, precision: 6);
        Assert.Equal(0.20, product.CompositionZ[0], precision: 6);
        Assert.Equal(0.80, product.CompositionZ[1], precision: 6);
    }

    [Fact]
    public void ProcessFlowsheet_WithScrambledUnitRegistration_SolvesInTopologicalOrderAndConservesPlantMass()
    {
        // Arrange: Plant topology:
        // [Feed 1] \
        //           --> (MIX-101) --> [Mixed] --> (E-101 Heater) --> [Heated] --> (V-101 Flash Drum) --> [Vapor] & [Liquid]
        // [Feed 2] /
        var feed1 = new MaterialStream("S-1", "Benzene Feed", _compounds, 50.0, 300.0, 120.0, [0.90, 0.10]);
        var feed2 = new MaterialStream("S-2", "Toluene Feed", _compounds, 50.0, 310.0, 120.0, [0.10, 0.90]);
        var mixed = new MaterialStream("S-3", "Mixer Outlet", _compounds);
        var heated = new MaterialStream("S-4", "Heater Outlet", _compounds);
        var vaporTop = new MaterialStream("S-5", "Overhead Vapor", _compounds);
        var liquidBottom = new MaterialStream("S-6", "Bottoms Liquid", _compounds);

        var mixer = new Mixer("MIX-101", "Feed Mixer", [feed1, feed2], mixed);
        var heater = new HeaterCooler("E-101", "Feed Preheater", mixed, heated, targetTemperatureKelvin: 368.15, pressureDropKpa: 18.675);
        var flashDrum = new FlashDrum("V-101", "VLE Separator", heated, vaporTop, liquidBottom);

        var flowsheet = new ProcessFlowsheet("Benzene-Toluene Separation Plant", _thermo);

        // Register units intentionally OUT OF ORDER (FlashDrum first, then Heater, then Mixer)
        // to prove Kahn's Topological Sort discovers the right sequence!
        flowsheet.AddUnit(flashDrum);
        flowsheet.AddUnit(heater);
        flowsheet.AddUnit(mixer);

        // Act
        IReadOnlyList<IUnitOperation> executedOrder = flowsheet.Solve();

        // Assert 1: Kahn's algorithm ordered execution as MIX-101 -> E-101 -> V-101
        Assert.Equal(["MIX-101", "E-101", "V-101"], executedOrder.Select(u => u.Id).ToArray());

        // Assert 2: Overall plant mass balance (Feed 1 + Feed 2 == Vapor Top + Liquid Bottom)
        double totalFeedFlow = feed1.MolarFlowRateMolPerSec + feed2.MolarFlowRateMolPerSec;
        double totalProductFlow = vaporTop.MolarFlowRateMolPerSec + liquidBottom.MolarFlowRateMolPerSec;
        Assert.Equal(totalFeedFlow, totalProductFlow, precision: 6);

        // Assert 3: Vapor stream is enriched in Benzene, Liquid stream is enriched in Toluene
        Assert.True(vaporTop.CompositionZ[0] > 0.50);
        Assert.True(liquidBottom.CompositionZ[1] > 0.50);
        Assert.True(heater.EnergyDutyWatts > 0.0);
    }

    [Fact]
    public void ProcessFlowsheet_WithCyclicGraph_ThrowsInvalidOperationException()
    {
        // Arrange: Unit A -> Stream 1 -> Unit B -> Stream 2 -> Unit A (cycle!)
        var s1 = new MaterialStream("S1", "Stream 1", _compounds);
        var s2 = new MaterialStream("S2", "Stream 2", _compounds);

        var unitA = new HeaterCooler("E-1", "Heater A", s2, s1, 350.0);
        var unitB = new HeaterCooler("E-2", "Heater B", s1, s2, 360.0);

        var flowsheet = new ProcessFlowsheet("Cyclic Plant", _thermo);
        flowsheet.AddUnit(unitA);
        flowsheet.AddUnit(unitB);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => flowsheet.GetTopologicalExecutionOrder());
        Assert.Contains("Cycle detected", ex.Message);
    }
}
