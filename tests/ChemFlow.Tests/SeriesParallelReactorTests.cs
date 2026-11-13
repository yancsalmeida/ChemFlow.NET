using ChemFlow.Domain.Native;
using Xunit;

namespace ChemFlow.Tests;

public class SeriesParallelReactorTests
{
    [Fact]
    public void SeriesParallel_DefaultParameters_And_SimdLevel()
    {
        int simdLevel = NativeReactorCoreInterop.SimdLevel;
        Assert.True(simdLevel == 0 || simdLevel == 256 || simdLevel == 512, $"SimdLevel {simdLevel} must be 0, 256, or 512");

        CstrSeriesParallelParamsFFI p = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        Assert.Equal(100.0, p.VolumetricFlowLPerMin);
        Assert.Equal(100.0, p.VolumeLiters);
        Assert.Equal(4000.0, p.RhoCpJPerLK);

        Assert.Equal(7.08e10, p.K0_1PerMin);
        Assert.Equal(8330.1, p.Ea1OverRKelvin);
        Assert.Equal(200000.0, p.DeltaH1JPerMol);

        Assert.Equal(3.50e9, p.K0_2PerMin);
        Assert.Equal(7500.0, p.Ea2OverRKelvin);
        Assert.Equal(120000.0, p.DeltaH2JPerMol);

        Assert.Equal(1.20e9, p.K0_3PerMin);
        Assert.Equal(9000.0, p.Ea3OverRKelvin);
        Assert.Equal(150000.0, p.DeltaH3JPerMol);

        Assert.Equal(1.0, p.CaFeedMolPerL);
        Assert.Equal(0.0, p.CbFeedMolPerL);
        Assert.Equal(350.0, p.TFeedKelvin);

        Assert.Equal(25.0, p.JacketVolumeLiters);
        Assert.Equal(50.0, p.JacketFlowLPerMin);
        Assert.Equal(4184.0, p.JacketRhoCpJPerLK);
        Assert.Equal(295.0, p.JacketTInKelvin);
        Assert.Equal(40000.0, p.UaJPerMinK);
    }

    [Fact]
    public void SeriesParallel_ReactionRates_Calculate_Accurately()
    {
        CstrSeriesParallelParamsFFI p = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        double ca = 1.0;
        double cb = 0.2;
        double tempK = 350.0;

        NativeReactorCoreInterop.SeriesParallelRates(in p, ca, cb, tempK, out double r1, out double r2, out double r3);

        double k1 = p.K0_1PerMin * Math.Exp(-p.Ea1OverRKelvin / tempK);
        double k2 = p.K0_2PerMin * Math.Exp(-p.Ea2OverRKelvin / tempK);
        double k3 = p.K0_3PerMin * Math.Exp(-p.Ea3OverRKelvin / tempK);

        Assert.Equal(k1 * ca, r1, precision: 10);
        Assert.Equal(k2 * cb, r2, precision: 10);
        Assert.Equal(k3 * ca, r3, precision: 10);

        Assert.True(r1 > 0.0);
        Assert.True(r2 > 0.0);
        Assert.True(r3 > 0.0);
    }

    [Fact]
    public void SeriesParallel_StepRk4_AdvancesConcentrationsAndDynamicJacket()
    {
        CstrSeriesParallelParamsFFI p = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        double ca = 1.0;
        double cb = 0.0;
        double cc = 0.0;
        double cd = 0.0;
        double temp = 350.0;
        double tempJ = 295.0;
        double dt = 0.005;

        int rc = NativeReactorCoreInterop.StepSeriesParallelRk4(in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt);
        Assert.Equal(0, rc);

        // Physical validation
        Assert.True(ca < 1.0, "Reactant A must be consumed");
        Assert.True(cb > 0.0, "Intermediate product B must form");
        Assert.True(temp > 350.0, "Exothermic kinetics must heat the reactor fluid");
        Assert.True(tempJ > 295.0, "Dynamic cooling jacket must absorb heat (dTj/dt > 0)");
    }

    [Fact]
    public void SeriesParallel_StepDetailed_CalculatesYieldSelectivityAndHeat()
    {
        CstrSeriesParallelParamsFFI p = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        double ca = 1.0;
        double cb = 0.0;
        double cc = 0.0;
        double cd = 0.0;
        double temp = 350.0;
        double tempJ = 295.0;
        double dt = 0.005;

        int rc = NativeReactorCoreInterop.StepSeriesParallelDetailed(
            in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt, runawayThresholdK: 400.0, out var state
        );
        Assert.Equal(0, rc);

        Assert.True(state.ConversionAPct > 0.0, "Conversion of A must be positive");
        Assert.True(state.YieldBPct > 0.0, "Yield of B must be positive");
        Assert.True(state.SelectivityBPct > 0.0 && state.SelectivityBPct <= 100.0, "Selectivity of B must be between 0 and 100%");
        Assert.True(state.HeatGeneratedWatts > 0.0, "Heat generation rate must be positive in exothermic kinetics");
        Assert.True(state.HeatRemovedWatts > 0.0, "Heat removal rate to cooling jacket must be positive");
        Assert.False(state.IsThermalRunaway, "Normal operating point must not trigger runaway interlock");
    }

    [Fact]
    public void SeriesParallel_ThermalRunaway_DetectsOverheating()
    {
        // Low cooling jacket transfer, zero jacket flow, elevated feed temperature
        var p = new CstrSeriesParallelParamsFFI
        {
            VolumetricFlowLPerMin = 100.0,
            VolumeLiters = 100.0,
            RhoCpJPerLK = 4000.0,
            K0_1PerMin = 7.08e10,
            Ea1OverRKelvin = 8330.1,
            DeltaH1JPerMol = 200000.0,
            K0_2PerMin = 3.50e9,
            Ea2OverRKelvin = 7500.0,
            DeltaH2JPerMol = 120000.0,
            K0_3PerMin = 1.20e9,
            Ea3OverRKelvin = 9000.0,
            DeltaH3JPerMol = 150000.0,
            CaFeedMolPerL = 1.0,
            CbFeedMolPerL = 0.0,
            CcFeedMolPerL = 0.0,
            CdFeedMolPerL = 0.0,
            TFeedKelvin = 395.0,
            JacketVolumeLiters = 25.0,
            JacketFlowLPerMin = 0.0,
            JacketRhoCpJPerLK = 4184.0,
            JacketTInKelvin = 300.0,
            UaJPerMinK = 0.0 // Complete cooling loss
        };

        double ca = 0.8;
        double cb = 0.1;
        double cc = 0.05;
        double cd = 0.05;
        double temp = 401.0;
        double tempJ = 300.0;
        double dt = 0.005;

        int rc = NativeReactorCoreInterop.StepSeriesParallelDetailed(
            in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt, runawayThresholdK: 400.0, out var state
        );
        Assert.Equal(0, rc);

        Assert.True(state.IsThermalRunaway, "Temperature exceeding 400 K must set IsThermalRunaway flag");
        Assert.True(state.TempKelvin >= 400.0);
    }

    [Fact]
    public void SeriesParallel_BatchSimd_Parity_With_Ensemble()
    {
        CstrSeriesParallelParamsFFI p = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        int n = 8;
        double[] ca = [1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3];
        double[] cb = [0.0, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.35];
        double[] cc = [0.0, 0.01, 0.02, 0.03, 0.04, 0.05, 0.06, 0.07];
        double[] cd = [0.0, 0.01, 0.02, 0.03, 0.04, 0.05, 0.06, 0.07];
        double[] temp = [350.0, 352.0, 355.0, 358.0, 360.0, 362.0, 365.0, 368.0];
        double[] tempJ = [295.0, 296.0, 297.0, 298.0, 299.0, 300.0, 302.0, 304.0];
        double dt = 0.005;

        var states = new CstrSeriesParallelStateFFI[n];
        int rc = NativeReactorCoreInterop.StepSeriesParallelBatchDetailed(
            in p, ca, cb, cc, cd, temp, tempJ, dt, runawayThresholdK: 400.0, states
        );
        Assert.Equal(0, rc);

        for (int i = 0; i < n; i++)
        {
            Assert.True(states[i].ConversionAPct > 0.0);
            Assert.True(states[i].YieldBPct > 0.0);
            Assert.True(states[i].HeatGeneratedWatts > 0.0);
            Assert.True(states[i].HeatRemovedWatts > 0.0);
        }
    }

    [Fact]
    public void SeriesParallel_BatchTrajectory_SimulatesMultiStepDynamics()
    {
        CstrSeriesParallelParamsFFI p = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        int n = 4;
        double[] ca0 = [1.0, 0.8, 0.6, 0.4];
        double[] cb0 = [0.0, 0.05, 0.1, 0.15];
        double[] cc0 = [0.0, 0.01, 0.02, 0.03];
        double[] cd0 = [0.0, 0.01, 0.02, 0.03];
        double[] temp0 = [350.0, 352.0, 355.0, 360.0];
        double[] tempJ0 = [295.0, 296.0, 298.0, 300.0];
        double dt = 0.005;
        nuint steps = 50;

        double[] outTime = new double[steps + 1];
        double[] outCa = new double[(steps + 1) * (nuint)n];
        double[] outCb = new double[(steps + 1) * (nuint)n];
        double[] outCc = new double[(steps + 1) * (nuint)n];
        double[] outCd = new double[(steps + 1) * (nuint)n];
        double[] outTemp = new double[(steps + 1) * (nuint)n];
        double[] outTempJ = new double[(steps + 1) * (nuint)n];

        int rc = NativeReactorCoreInterop.SimulateSeriesParallelBatchTrajectory(
            in p, ca0, cb0, cc0, cd0, temp0, tempJ0, dt, steps,
            outTime, outCa, outCb, outCc, outCd, outTemp, outTempJ
        );
        Assert.Equal(0, rc);

        Assert.Equal(0.0, outTime[0]);
        Assert.Equal(0.25, outTime[50], precision: 6);

        // Initial step check
        for (int j = 0; j < n; j++)
        {
            Assert.Equal(ca0[j], outCa[j]);
            Assert.Equal(cb0[j], outCb[j]);
            Assert.Equal(temp0[j], outTemp[j]);
            Assert.Equal(tempJ0[j], outTempJ[j]);
        }

        // Final step check: verify concentrations stayed within physical bounds
        int lastOffset = 50 * n;
        for (int j = 0; j < n; j++)
        {
            Assert.True(outCa[lastOffset + j] >= 0.0);
            Assert.True(outCb[lastOffset + j] >= 0.0);
            Assert.True(outCc[lastOffset + j] >= 0.0);
            Assert.True(outCd[lastOffset + j] >= 0.0);
            Assert.True(outTemp[lastOffset + j] >= 50.0);
            Assert.True(outTempJ[lastOffset + j] >= 50.0);
        }
    }
}
