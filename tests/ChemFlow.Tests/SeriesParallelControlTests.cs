using ChemFlow.Domain.Control;
using ChemFlow.Domain.Native;

namespace ChemFlow.Tests;

public class SeriesParallelControlTests
{
    [Fact]
    public void SeriesParallelEkf6_ConvergesFromWrongInitialGuess_UsingOnlyTemperatureMeasurements()
    {
        var p = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        double dt = 0.05;
        double trueCa = 0.52, trueCb = 0.28, trueCc = 0.06, trueCd = 0.03;
        double trueT = 352.0, trueTj = 310.0;

        // Intentionally initialize EKF with large concentration errors
        var ekf = new SeriesParallelEkf6(
            ca0: 0.85,
            cb0: 0.05,
            cc0: 0.01,
            cd0: 0.01,
            tempK0: trueT,
            jacketTempK0: trueTj);

        var rng = new Random(123);
        SpEkfUpdateResult? lastUpdate = null;

        for (int i = 0; i < 30; i++)
        {
            double valvePct = 42.0;
            var pAct = SeriesParallelActuatorModel.ApplyValveCommand(in p, valvePct);
            NativeReactorCoreInterop.StepSeriesParallelRk4(
                in pAct, ref trueCa, ref trueCb, ref trueCc, ref trueCd, ref trueT, ref trueTj, dt);
            SeriesParallelActuatorModel.ClampState(ref trueCa, ref trueCb, ref trueCc, ref trueCd, ref trueT, ref trueTj);

            ekf.Predict(in p, valvePct, dt);
            double noiseT = (rng.NextDouble() - 0.5) * 0.4;
            double noiseTj = (rng.NextDouble() - 0.5) * 0.4;
            lastUpdate = ekf.Update(trueT + noiseT, trueTj + noiseTj);
        }

        Assert.NotNull(lastUpdate);
        Assert.True(Math.Abs(lastUpdate.CaMolPerL - trueCa) < 0.03, $"Expected Ca error < 0.03, got {Math.Abs(lastUpdate.CaMolPerL - trueCa):F4}");
        Assert.True(Math.Abs(lastUpdate.CbMolPerL - trueCb) < 0.03, $"Expected Cb error < 0.03, got {Math.Abs(lastUpdate.CbMolPerL - trueCb):F4}");
        Assert.False(lastUpdate.SensorFaultDetected);
    }

    [Fact]
    public void SeriesParallelEkf6_DetectsSensorFault_OnSuddenThermocoupleBiasJump()
    {
        var p = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        var ekf = new SeriesParallelEkf6(
            ca0: 0.60, cb0: 0.25, cc0: 0.05, cd0: 0.02, tempK0: 350.0, jacketTempK0: 310.0);

        double trueCa = 0.60, trueCb = 0.25, trueCc = 0.05, trueCd = 0.02, trueT = 350.0, trueTj = 310.0;
        var pAct = SeriesParallelActuatorModel.ApplyValveCommand(in p, 35.0);
        NativeReactorCoreInterop.StepSeriesParallelRk4(
            in pAct, ref trueCa, ref trueCb, ref trueCc, ref trueCd, ref trueT, ref trueTj, 0.05);

        ekf.Predict(in p, 35.0, 0.05);
        SpEkfUpdateResult faulted = ekf.Update(trueT + 18.0, trueTj);

        Assert.True(faulted.SensorFaultDetected);
        Assert.True(faulted.Nis > 20.0, $"Expected NIS > 20.0 on +18K thermocouple spike, got {faulted.Nis:F2}");
    }

    [Fact]
    public void SeriesParallelClosedLoopSimulator_PreventsRunawayAndMaximizesCbUnderFeedDisturbance()
    {
        var scenario = new SpClosedLoopScenario(
            TotalSteps: 80,
            SampleTimeMin: 0.05,
            InitialValvePct: 15.0,
            NominalFeedTempC: 51.85,
            DisturbanceFeedTempC: 76.85,
            DisturbanceStep: 12);

        SpClosedLoopComparisonResult result = SeriesParallelClosedLoopSimulator.RunComparison(scenario);

        Assert.Equal(80, result.Trajectory.Count);
        Assert.True(result.OpenLoopRunawayTriggered, $"Expected open-loop runaway, peak was {result.OpenLoopPeakTempC} C");
        Assert.True(result.OpenLoopPeakTempC > result.MaxSafeTempC && result.OpenLoopPeakTempC < 400.0,
            $"Open-loop runaway peak should be physical (in ({result.MaxSafeTempC}, 400) C), got {result.OpenLoopPeakTempC} C");
        Assert.False(result.ClosedLoopRunawayTriggered, $"Expected zero closed-loop runaway, peak was {result.ClosedLoopPeakTempC} C");
        Assert.True(result.ClosedLoopPeakTempC < result.MaxSafeTempC,
            $"Closed-loop peak {result.ClosedLoopPeakTempC} C exceeded safety trip {result.MaxSafeTempC} C");
        Assert.True(result.ClosedLoopFinalCbMolPerL > result.OpenLoopFinalCbMolPerL,
            $"Expected e-NMPC Cb ({result.ClosedLoopFinalCbMolPerL}) > Open-Loop Cb ({result.OpenLoopFinalCbMolPerL})");
        Assert.True(result.ClosedLoopFinalSelectivityBPct > result.OpenLoopFinalSelectivityBPct);
        Assert.True(result.EkfMeanCbRmseMolPerL < 0.06, $"Expected EKF Cb RMSE < 0.06 M, got {result.EkfMeanCbRmseMolPerL:F4}");
        Assert.True(result.EkfFinalCbAbsErrorMolPerL < 0.02, $"Expected final EKF Cb error < 0.02 M, got {result.EkfFinalCbAbsErrorMolPerL:F4}");
        Assert.Equal(0, result.FaultCount);
    }

    [Fact]
    public void SeriesParallelEconomicNmpc_RespectsTargetCbAndValveSlewRate()
    {
        double maxSlew = 20.0;
        var scenario = new SpClosedLoopScenario(
            TotalSteps: 60,
            SampleTimeMin: 0.05,
            InitialValvePct: 20.0,
            NmpcConfig: new SpNmpcConfig(
                WeightCbEconomic: 120.0,
                TargetCbMolPerL: 0.24,
                MaxSlewPerStepPct: maxSlew));

        SpClosedLoopComparisonResult result = SeriesParallelClosedLoopSimulator.RunComparison(scenario);

        Assert.False(result.ClosedLoopRunawayTriggered);
        double prevValve = scenario.InitialValvePct;
        foreach (var pt in result.Trajectory)
        {
            Assert.InRange(pt.ClosedLoopValvePct, 0.0, 100.0);
            Assert.True(Math.Abs(pt.ClosedLoopValvePct - prevValve) <= maxSlew + 0.05,
                $"Valve slew violated at step {pt.Step}: {prevValve:F2}% -> {pt.ClosedLoopValvePct:F2}%");
            prevValve = pt.ClosedLoopValvePct;
        }
    }

    [Fact]
    public void SeriesParallelClosedLoop_WithStiffRos3Solver_MaintainsSafeBoundedTrajectory()
    {
        var scenario = new SpClosedLoopScenario(
            TotalSteps: 80,
            SampleTimeMin: 0.05,
            InitialValvePct: 15.0,
            NominalFeedTempC: 51.85,
            DisturbanceFeedTempC: 76.85,
            DisturbanceStep: 12,
            UseStiffRos3Solver: true);

        SpClosedLoopComparisonResult result = SeriesParallelClosedLoopSimulator.RunComparison(scenario);

        Assert.Equal(80, result.Trajectory.Count);
        Assert.True(result.OpenLoopRunawayTriggered);
        Assert.False(result.ClosedLoopRunawayTriggered);
        Assert.True(result.ClosedLoopPeakTempC < result.MaxSafeTempC);
        Assert.True(result.ClosedLoopFinalCbMolPerL > result.OpenLoopFinalCbMolPerL);
        Assert.True(result.EkfMeanCbRmseMolPerL < 0.06);
        Assert.Equal(0, result.FaultCount);
    }

    [Fact]
    public void SeriesParallelSolverBenchmark_Ros3RemainsStableWhenExplicitRk4FailsAtLargeStiffStep()
    {
        SpSolverBenchmarkResult bench = SeriesParallelClosedLoopSimulator.RunSolverStiffBenchmark(
            new SpSolverBenchmarkScenario(
                Steps: 40,
                StepDtMin: 0.08,
                InitialTempC: 115.0,
                InitialJacketTempC: 75.0,
                ValvePct: 5.0));

        Assert.Equal(40, bench.Trajectory.Count);
        Assert.True(bench.Ros3RemainedStable, $"ROS3L should stay bounded, got peak {bench.Ros3PeakTempC} C");
        Assert.True(bench.Ros3PeakTempC > 115.0 && bench.Ros3PeakTempC < 450.0,
            $"Unexpected ROS3L peak temperature: {bench.Ros3PeakTempC} C");
        Assert.True(bench.Rk4BlowupDetected,
            $"Expected raw explicit RK4 at dt=0.08 min to diverge/blow up, peak was {bench.Rk4PeakTempC} C");
    }

    [Fact]
    public void SeriesParallelEkf6_Sustained40KSpike_FreezesMeasurementUpdateAndPreventsRunaway()
    {
        var p = ChemFlow.Domain.Native.NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        var ekf = new SeriesParallelEkf6(0.48, 0.28, 0.21, 0.03, 335.15, 309.15);

        double trueCa = 0.48, trueCb = 0.28, trueCc = 0.21, trueCd = 0.03;
        double trueT = 335.15, trueTj = 309.15;

        for (int step = 0; step < 30; step++)
        {
            double valvePct = 55.0;
            var pAct = SeriesParallelActuatorModel.ApplyValveCommand(in p, valvePct);
            ChemFlow.Domain.Native.NativeReactorCoreInterop.StepSeriesParallelRk4(
                in pAct, ref trueCa, ref trueCb, ref trueCc, ref trueCd, ref trueT, ref trueTj, 0.05);
            ekf.Predict(in p, valvePct, 0.05);
            var upd = ekf.Update(trueT + 40.0, trueTj);
            Assert.True(upd.SensorFaultDetected, $"Expected fault detection on step {step}");
            Assert.True(Math.Abs(upd.TempKelvin - trueT) < 5.0,
                $"EKF6 drifted {upd.TempKelvin - trueT:F2} K during sustained +40K spike at step {step}");
        }
    }
}
