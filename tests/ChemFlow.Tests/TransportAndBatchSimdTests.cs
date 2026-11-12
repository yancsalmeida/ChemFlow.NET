using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Native;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Transport;
using ChemFlow.Domain.Units;
using Xunit;

namespace ChemFlow.Tests;

public class TransportAndBatchSimdTests
{
    private readonly ChemicalCompound _benzene = DefaultCompounds.Benzene;
    private readonly ChemicalCompound _toluene = DefaultCompounds.Toluene;
    private readonly ChemicalCompound _nHexane = DefaultCompounds.NHexane;
    private readonly ChemicalCompound _nHeptane = DefaultCompounds.NHeptane;
    private readonly IThermodynamicPackage _thermo = new RaoultThermodynamicPackage();

    [Fact]
    public void ChungTransport_PureVapor_HasRealisticPhysicalMagnitudes()
    {
        var package = new ChungTransportPackage();
        var compounds = new[] { _benzene };
        var y = new[] { 1.0 };
        double tempK = 380.0; // above boiling point of benzene (353 K)
        double pressureKpa = 101.325;

        var result = package.CalculateVaporProperties(compounds, y, tempK, pressureKpa);

        Assert.Equal(PhaseState.SuperheatedVapor, result.Phase);
        Assert.Equal(tempK, result.TemperatureKelvin);
        Assert.Equal(pressureKpa, result.PressureKpa);

        // Dilute vapor dynamic viscosity: typical organic vapors are ~0.007 to 0.015 cP (7e-6 to 1.5e-5 Pa·s)
        Assert.InRange(result.DynamicViscosityPaS, 5e-6, 3e-5);
        Assert.InRange(result.DynamicViscosityCp, 0.005, 0.03);

        // Dilute vapor thermal conductivity: typical organic vapors are ~0.01 to 0.05 W/(m·K)
        Assert.InRange(result.ThermalConductivityWPerMK, 0.008, 0.06);

        // Gas Prandtl number: typically 0.65 to 0.95
        Assert.InRange(result.PrandtlNumber, 0.5, 1.2);
    }

    [Fact]
    public void ChungTransport_MulticomponentLiquid_CalculatesViscosityAndConductivity()
    {
        var package = new ChungTransportPackage();
        var compounds = new[] { _benzene, _toluene };
        var x = new[] { 0.5, 0.5 };
        double tempK = 298.15; // 25 °C
        double pressureKpa = 101.325;

        var result = package.CalculateLiquidProperties(compounds, x, tempK, pressureKpa);

        Assert.Equal(PhaseState.SubcooledLiquid, result.Phase);

        // Liquid benzene/toluene viscosity at 25 °C is ~0.5 to 0.7 cP (5e-4 to 7e-4 Pa·s)
        Assert.InRange(result.DynamicViscosityCp, 0.2, 1.5);
        Assert.InRange(result.DynamicViscosityPaS, 2e-4, 1.5e-3);

        // Liquid organic thermal conductivity is ~0.10 to 0.20 W/(m·K)
        Assert.InRange(result.ThermalConductivityWPerMK, 0.08, 0.25);

        // Liquid Prandtl number Pr = Cp * μ / λ: typically 4 to 12
        Assert.InRange(result.PrandtlNumber, 2.0, 20.0);
        Assert.True(result.KinematicViscosityM2PerS > 0.0);
    }

    [Fact]
    public void ElyHanleyTransport_ExtendedCorrespondingStates_MatchesPhysicalBounds()
    {
        var package = new ElyHanleyTransportPackage();
        var compounds = new[] { _nHexane, _nHeptane };
        var y = new[] { 0.6, 0.4 };
        double tempK = 390.0;
        double pressureKpa = 101.325;

        var resultVapor = package.CalculateVaporProperties(compounds, y, tempK, pressureKpa);

        Assert.Equal(PhaseState.SuperheatedVapor, resultVapor.Phase);
        Assert.InRange(resultVapor.DynamicViscosityCp, 0.005, 0.03);
        Assert.InRange(resultVapor.ThermalConductivityWPerMK, 0.01, 0.06);
        Assert.InRange(resultVapor.PrandtlNumber, 0.5, 1.5);

        var x = new[] { 0.6, 0.4 };
        var resultLiquid = package.CalculateLiquidProperties(compounds, x, 298.15, pressureKpa);

        Assert.Equal(PhaseState.SubcooledLiquid, resultLiquid.Phase);
        Assert.InRange(resultLiquid.DynamicViscosityCp, 0.2, 1.2);
        Assert.InRange(resultLiquid.ThermalConductivityWPerMK, 0.08, 0.25);
    }

    [Fact]
    public void MaterialStream_GetTransportProperties_SolvesAndReturnsProperties()
    {
        var stream = new MaterialStream("S-100", "Feed Stream", [_benzene, _toluene]);
        stream.UpdateState(
            molarFlowRateMolPerSec: 10.0,
            temperatureKelvin: 298.15,
            pressureKpa: 101.325,
            compositionZ: [0.5, 0.5],
            thermo: _thermo
        );

        var props = stream.GetTransportProperties();

        Assert.NotNull(props);
        Assert.Equal(PhaseState.SubcooledLiquid, props.Phase);
        Assert.True(props.DynamicViscosityPaS > 0.0);
        Assert.True(props.ThermalConductivityWPerMK > 0.0);
        Assert.True(props.PrandtlNumber > 0.0);
    }

    [Fact]
    public void NativeInterop_HasNativeSimdSupport_ReportsActiveOnLinuxWithAVX2()
    {
        if (NativeReactorCoreInterop.IsNativeAvailable)
        {
            Assert.True(NativeReactorCoreInterop.HasNativeSimdSupport, "ReactorCore.C should support SIMD AVX2 and FMA");
        }
        else
        {
            Assert.False(NativeReactorCoreInterop.HasNativeSimdSupport);
        }
    }

    [Fact]
    public void NativeInterop_StepBatchRk4_AdvancesMultipleReactorsSimultaneously()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        int nReactors = 8;
        double[] ca = new double[nReactors];
        double[] tempK = new double[nReactors];
        for (int i = 0; i < nReactors; i++)
        {
            ca[i] = 1.0;
            tempK[i] = 340.0 + i * 2.0;
        }

        double dt = 0.01;
        int rc = NativeReactorCoreInterop.StepBatchRk4(in p, ca, tempK, dt);
        Assert.Equal(0, rc);

        for (int i = 0; i < nReactors; i++)
        {
            Assert.True(ca[i] < 1.0, $"Reactor {i}: Ca must be consumed");
            Assert.True(tempK[i] > (340.0 + i * 2.0), $"Reactor {i}: Temperature must rise");
        }
    }

    [Fact]
    public void NativeInterop_StepBatchDetailed_PopulatesConversionAndRunaway()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        int nReactors = 4;
        double[] ca = [1.0, 1.0, 1.0, 1.0];
        double[] tempK = [330.0, 350.0, 370.0, 410.0];
        var states = new CstrStateFFI[nReactors];

        int rc = NativeReactorCoreInterop.StepBatchDetailed(in p, ca, tempK, 0.01, 400.0, states);
        Assert.Equal(0, rc);

        Assert.False(states[0].IsRunaway, "Reactor 0 is below threshold");
        Assert.False(states[1].IsRunaway, "Reactor 1 is below threshold");
        Assert.False(states[2].IsRunaway, "Reactor 2 is below threshold");
        Assert.True(states[3].IsRunaway, "Reactor 3 at 410K exceeds 400K runaway threshold");

        for (int i = 0; i < nReactors; i++)
        {
            Assert.True(states[i].ConversionPct > 0.0);
            Assert.True(states[i].ReactionRateMolPerLMin >= 0.0);
        }
        Assert.True(states[0].ReactionRateMolPerLMin > 0.0);
        Assert.True(states[1].ReactionRateMolPerLMin > 0.0);
        Assert.True(states[2].ReactionRateMolPerLMin > 0.0);
    }

    [Fact]
    public void NativeInterop_SimulateBatchTrajectory_MatchesDimensions()
    {
        CstrParamsFFI p = NativeReactorCoreInterop.GetDefaultParameters();
        int nReactors = 4;
        int steps = 20;
        double dt = 0.01;

        double[] ca0 = [1.0, 1.0, 1.0, 1.0];
        double[] temp0 = [340.0, 350.0, 360.0, 370.0];

        double[] times = new double[steps + 1];
        double[] outCa = new double[(steps + 1) * nReactors];
        double[] outTemp = new double[(steps + 1) * nReactors];

        int rc = NativeReactorCoreInterop.SimulateBatchTrajectory(
            in p, ca0, temp0, dt, steps, times, outCa, outTemp);

        Assert.Equal(0, rc);
        Assert.Equal(0.0, times[0]);
        Assert.Equal(0.20, times[steps], precision: 6);

        // Final concentrations must be lower than initial
        for (int i = 0; i < nReactors; i++)
        {
            Assert.Equal(ca0[i], outCa[0 * nReactors + i]);
            Assert.True(outCa[steps * nReactors + i] < ca0[i]);
        }
    }

    [Fact]
    public void ContinuousStirredTankReactorBattery_SimulatesEnsembleWithTransportProperties()
    {
        var sIn = new MaterialStream("FEED-1", "Feed to Battery", [_benzene, _toluene]);
        sIn.UpdateState(
            molarFlowRateMolPerSec: 100.0,
            temperatureKelvin: 350.0,
            pressureKpa: 150.0,
            compositionZ: [0.80, 0.20],
            thermo: _thermo
        );

        var sOut = new MaterialStream("EFF-1", "Effluent from Battery", [_benzene, _toluene]);

        int reactorCount = 8;
        var battery = new ContinuousStirredTankReactorBattery(
            id: "CSTR-BATTERY-01",
            name: "Train of 8 Parallel CSTRs",
            inletStream: sIn,
            outletStream: sOut,
            reactorCount: reactorCount,
            reactantCompoundId: "C6H6",
            productCompoundId: "C7H8",
            reactorVolumeLiters: 100.0,
            coolantTemperatureKelvin: 300.0,
            nominalHeatTransferUaWattsPerKelvin: 666.67
        );

        battery.Solve(_thermo);

        // Verification
        Assert.True(battery.MeanConversion > 0.0, "Battery achieved conversion");
        Assert.Equal(reactorCount, battery.ReactorStates.Count);
        Assert.Equal(reactorCount, battery.ReactorTransportProperties.Count);
        Assert.False(battery.HasThermalRunaway, "Normal operation has no runaway");

        // Verify transport properties in each reactor of the battery
        for (int i = 0; i < reactorCount; i++)
        {
            var tp = battery.ReactorTransportProperties[i];
            Assert.InRange(tp.DynamicViscosityCp, 0.1, 2.0);
            Assert.InRange(tp.ThermalConductivityWPerMK, 0.08, 0.30);
            Assert.InRange(tp.PrandtlNumber, 1.0, 25.0);
        }

        // Mass and First Law conservation across the battery
        Assert.Equal(sIn.MassFlowRateKgPerSec, sOut.MassFlowRateKgPerSec, precision: 4);
        double energyResidual = Math.Abs((sIn.TotalEnthalpyFlowWatts + battery.EnergyDutyWatts) - sOut.TotalEnthalpyFlowWatts);
        Assert.True(energyResidual < 1e-3, $"First Law violated in battery: residual = {energyResidual} W");
    }

    [Fact]
    public void ContinuousStirredTankReactorBattery_DetectsThermalRunaway()
    {
        var sIn = new MaterialStream("FEED-2", "Feed to Battery", [_benzene, _toluene]);
        sIn.UpdateState(
            molarFlowRateMolPerSec: 50.0,
            temperatureKelvin: 370.0,
            pressureKpa: 150.0,
            compositionZ: [0.90, 0.10],
            thermo: _thermo
        );

        var sOut = new MaterialStream("EFF-2", "Effluent", [_benzene, _toluene]);

        // Loss of cooling jacket UA -> runaway
        var battery = new ContinuousStirredTankReactorBattery(
            id: "CSTR-BATTERY-RUNAWAY",
            name: "Uncooled Runaway Battery",
            inletStream: sIn,
            outletStream: sOut,
            reactorCount: 4,
            reactantCompoundId: "C6H6",
            productCompoundId: "C7H8",
            coolantTemperatureKelvin: 360.0,
            nominalHeatTransferUaWattsPerKelvin: 0.0, // Cooling failure
            maxSafeTemperatureKelvin: 380.0
        );

        battery.Solve(_thermo);

        Assert.True(battery.HasThermalRunaway, "Battery must flag thermal runaway upon cooling failure");
        Assert.True(battery.MeanTemperatureKelvin > 380.0, "Temperature must exceed max safe threshold");
    }
}
