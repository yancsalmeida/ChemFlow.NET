using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Native;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;
using ChemFlow.Domain.ValueObjects;
using Xunit;

namespace ChemFlow.Tests;

public class NativeReactorCoreTests
{
    private readonly ChemicalCompound _compoundA;
    private readonly ChemicalCompound _compoundB;
    private readonly IThermodynamicPackage _thermo;

    public NativeReactorCoreTests()
    {
        // 2-Methylpentane and 3-Methylpentane (isomers with M = 86.178 g/mol)
        _compoundA = new ChemicalCompound(
            "2-MP", "2-Methylpentane", "C6H14", 86.178, 497.5, 3010.0, 0.28,
            185.0, 100.0, 31500.0,
            new AntoineCoefficients(6.866, 1168.0, -47.0)
        );
        _compoundB = new ChemicalCompound(
            "3-MP", "3-Methylpentane", "C6H14", 86.178, 504.4, 3120.0, 0.29,
            185.0, 100.0, 32100.0,
            new AntoineCoefficients(6.877, 1175.0, -46.5)
        );
        _thermo = new RaoultThermodynamicPackage();
    }

    [Fact]
    public void NativeInterop_VersionString_IsPopulated()
    {
        string version = NativeReactorCoreInterop.VersionString;
        Assert.NotNull(version);
        Assert.NotEmpty(version);

        if (NativeReactorCoreInterop.IsNativeAvailable)
        {
            Assert.Contains("ReactorCore.C", version);
        }
        else
        {
            Assert.Contains("Managed C# Fallback", version);
        }
    }

    [Fact]
    public void NativeInterop_DefaultParameters_HaveValidPhysicalBounds()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        Assert.Equal(100.0, p.VolumetricFlowLPerMin);
        Assert.Equal(100.0, p.VolumeLiters);
        Assert.True(p.K0PerMin > 0.0);
        Assert.True(p.EaOverRKelvin > 0.0);
        Assert.True(p.DeltaHJPerMol > 0.0);
        Assert.True(p.TFeedKelvin > 0.0);
        Assert.True(p.TCoolantKelvin > 0.0);
    }

    [Fact]
    public void NativeInterop_ReactionRate_MatchesArrheniusLaw()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        double ca = 1.0;
        double tempK = 350.0;

        double rate = NativeReactorCoreInterop.ReactionRate(in p, ca, tempK);
        double expectedRate = p.K0PerMin * Math.Exp(-p.EaOverRKelvin / tempK) * ca;

        Assert.Equal(expectedRate, rate, precision: 10);
    }

    [Fact]
    public void NativeInterop_StepRk4_AdvancesStatePhysically()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        double ca = 1.0;
        double tempK = 350.0;
        double dt = 0.005; // 0.3 seconds

        int rc = NativeReactorCoreInterop.StepRk4(in p, ref ca, ref tempK, dt);
        Assert.Equal(0, rc);

        // In exothermic reaction, Ca decreases and Temp increases
        Assert.True(ca < 1.0, "Reactant Ca must be depleted");
        Assert.True(tempK > 350.0, "Exothermic reaction must elevate temperature");
    }

    [Fact]
    public void NativeInterop_SimulateTrajectory_GeneratesParityCurves()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        int steps = 100;
        double dt = 0.005;

        double[] times = new double[steps + 1];
        double[] caCurve = new double[steps + 1];
        double[] tempCurve = new double[steps + 1];

        int rc = NativeReactorCoreInterop.SimulateTrajectory(in p, 1.0, 350.0, dt, steps, times, caCurve, tempCurve);
        Assert.Equal(0, rc);

        Assert.Equal(0.0, times[0]);
        Assert.Equal(1.0, caCurve[0]);
        Assert.Equal(350.0, tempCurve[0]);

        // Monotonic trends during initial transient
        Assert.True(caCurve[steps] < caCurve[0]);
        Assert.True(tempCurve[steps] > tempCurve[0]);
    }

    [Fact]
    public void ContinuousStirredTankReactor_SolvesAndConservesMassAndEnergy()
    {
        var sIn = new MaterialStream("S-1", "Reactor Feed", [_compoundA, _compoundB]);
        sIn.UpdateState(
            molarFlowRateMolPerSec: 50.0,
            temperatureKelvin: 350.0,
            pressureKpa: 150.0,
            compositionZ: [0.90, 0.10],
            thermo: _thermo
        );

        var sOut = new MaterialStream("S-2", "Reactor Effluent", [_compoundA, _compoundB]);

        var cstr = new ContinuousStirredTankReactor(
            id: "R-101",
            name: "Native Dynamic CSTR",
            inletStream: sIn,
            outletStream: sOut,
            reactantCompoundId: "2-MP",
            productCompoundId: "3-MP",
            reactorVolumeLiters: 100.0,
            coolantTemperatureKelvin: 300.0,
            heatTransferUaWattsPerKelvin: 666.67
        );

        cstr.Solve(_thermo);

        // Conversion must be positive
        Assert.True(cstr.FractionalConversion > 0.0, "Conversion must be > 0");
        Assert.True(cstr.FractionalConversion <= 1.0, "Conversion must be <= 1");

        // Reactant A fraction decreased, Product B fraction increased
        Assert.True(sOut.CompositionZ[0] < sIn.CompositionZ[0], "Reactant A fraction must decrease");
        Assert.True(sOut.CompositionZ[1] > sIn.CompositionZ[1], "Product B fraction must increase");

        // Mass conservation (isomers with identical molar mass)
        double massIn = sIn.MassFlowRateKgPerSec;
        double massOut = sOut.MassFlowRateKgPerSec;
        Assert.Equal(massIn, massOut, precision: 6);

        // First Law Energy balance: H_in + Q = H_out
        double energyResidual = Math.Abs((sIn.TotalEnthalpyFlowWatts + cstr.EnergyDutyWatts) - sOut.TotalEnthalpyFlowWatts);
        Assert.True(energyResidual < 1e-4, $"First Law violated: residual = {energyResidual} W");
    }

    [Fact]
    public void ContinuousStirredTankReactor_InFlowsheet_GeneratesValidBalanceReport()
    {
        var flowsheet = new ProcessFlowsheet("CSTR Flowsheet", _thermo);

        var sFeed = new MaterialStream("S-101", "Feed", [_compoundA, _compoundB]);
        sFeed.UpdateState(40.0, 350.0, 150.0, [0.85, 0.15], _thermo);

        var sProduct = new MaterialStream("S-102", "Product", [_compoundA, _compoundB]);

        var cstr = new ContinuousStirredTankReactor(
            id: "R-101",
            name: "Catalytic CSTR",
            inletStream: sFeed,
            outletStream: sProduct,
            reactantCompoundId: "2-MP",
            productCompoundId: "3-MP",
            reactorVolumeLiters: 150.0,
            coolantTemperatureKelvin: 310.0
        );

        flowsheet.AddStream(sFeed);
        flowsheet.AddStream(sProduct);
        flowsheet.AddUnit(cstr);

        var executed = flowsheet.Solve();
        Assert.Single(executed);

        var report = flowsheet.GenerateBalanceReport();
        Assert.True(report.IsMassConserved, "Plant mass must be conserved");
        Assert.True(report.IsEnergyConserved, "Plant energy must be conserved");

        var topology = flowsheet.GetTopology();
        Assert.True(topology.IsAcyclic);
        Assert.Contains("R-101", topology.DotGraph);
    }

    [Fact]
    public void NativeInterop_StepAdaptive_AdvancesStateAndPopulatesStats()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        double ca = 1.0;
        double tempK = 350.0;
        double dt = 0.01;

        int rc = NativeReactorCoreInterop.StepAdaptive(in p, ref ca, ref tempK, dt, out CstrAdaptiveStatsFFI stats);
        Assert.Equal(0, rc);

        Assert.True(ca < 1.0, "Reactant Ca must be depleted");
        Assert.True(tempK > 350.0, "Exothermic reaction must elevate temperature");
        Assert.True(stats.StepsAccepted > 0, "At least one step must be accepted");
        Assert.True(stats.MinH > 0.0, "Min step size must be positive");
    }

    [Fact]
    public void NativeInterop_StepDetailedAdaptive_ComputesCompleteStateAndDiagnostics()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        double ca = 1.0;
        double tempK = 350.0;
        double dt = 0.01;

        int rc = NativeReactorCoreInterop.StepDetailedAdaptive(
            in p, ref ca, ref tempK, dt, 400.0, out CstrStateFFI state, out CstrAdaptiveStatsFFI stats);

        Assert.Equal(0, rc);
        Assert.True(state.ConversionPct > 0.0, "Conversion % must be positive");
        Assert.True(state.ReactionRateMolPerLMin > 0.0, "Reaction rate must be positive");
        Assert.False(state.IsRunaway, "Controlled initial step must not trigger runaway");
        Assert.True(stats.StepsAccepted > 0, "Adaptive steps must be accepted");
    }

    [Fact]
    public void NativeInterop_SimulateAdaptiveTrajectory_GeneratesParityCurves()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        int steps = 50;
        double dt = 0.01;

        double[] times = new double[steps + 1];
        double[] caCurve = new double[steps + 1];
        double[] tempCurve = new double[steps + 1];

        int rc = NativeReactorCoreInterop.SimulateAdaptiveTrajectory(
            in p, 1.0, 350.0, dt, steps, times, caCurve, tempCurve, out CstrAdaptiveStatsFFI stats);

        Assert.Equal(0, rc);
        Assert.Equal(0.0, times[0]);
        Assert.Equal(1.0, caCurve[0]);
        Assert.Equal(350.0, tempCurve[0]);

        // Monotonic trends
        Assert.True(caCurve[steps] < caCurve[0], "Concentration must drop along trajectory");
        Assert.True(tempCurve[steps] > tempCurve[0], "Temperature must rise due to heat of reaction");
        Assert.True(stats.StepsAccepted >= (nuint)steps, "Total accepted adaptive steps >= sample intervals");
    }

    [Fact]
    public void ContinuousStirredTankReactor_AdaptiveSolver_ConservesMassAndEnergy()
    {
        var sIn = new MaterialStream("S-1", "Feed", [_compoundA, _compoundB]);
        sIn.UpdateState(50.0, 350.0, 150.0, [0.90, 0.10], _thermo);

        var sOutAdaptive = new MaterialStream("S-2", "Effluent Adaptive", [_compoundA, _compoundB]);
        var cstrAdaptive = new ContinuousStirredTankReactor(
            id: "R-101-ADAPTIVE",
            name: "Adaptive RKF45 CSTR",
            inletStream: sIn,
            outletStream: sOutAdaptive,
            reactantCompoundId: "2-MP",
            productCompoundId: "3-MP",
            useAdaptiveSolver: true
        );

        cstrAdaptive.Solve(_thermo);

        Assert.True(cstrAdaptive.FractionalConversion > 0.0);
        Assert.True(cstrAdaptive.LastAdaptiveStats.StepsAccepted > 0);

        // Mass and First Law conservation
        Assert.Equal(sIn.MassFlowRateKgPerSec, sOutAdaptive.MassFlowRateKgPerSec, precision: 6);
        double energyResidual = Math.Abs((sIn.TotalEnthalpyFlowWatts + cstrAdaptive.EnergyDutyWatts) - sOutAdaptive.TotalEnthalpyFlowWatts);
        Assert.True(energyResidual < 1e-4, $"First Law violated in adaptive solver: residual = {energyResidual} W");

        // Compare with fixed-step RK4 solver
        var sOutFixed = new MaterialStream("S-3", "Effluent Fixed", [_compoundA, _compoundB]);
        var cstrFixed = new ContinuousStirredTankReactor(
            id: "R-101-FIXED",
            name: "Fixed RK4 CSTR",
            inletStream: sIn,
            outletStream: sOutFixed,
            reactantCompoundId: "2-MP",
            productCompoundId: "3-MP",
            useAdaptiveSolver: false
        );

        cstrFixed.Solve(_thermo);

        // Conversion agreement between RK4 and RKF45
        double diffConversion = Math.Abs(cstrAdaptive.FractionalConversion - cstrFixed.FractionalConversion);
        Assert.True(diffConversion < 0.05, $"Conversions diverge: adaptive={cstrAdaptive.FractionalConversion}, fixed={cstrFixed.FractionalConversion}");
    }
}
