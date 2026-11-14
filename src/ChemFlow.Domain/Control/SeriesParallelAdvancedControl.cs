using System;
using System.Collections.Generic;
using ChemFlow.Domain.Native;

namespace ChemFlow.Domain.Control;

/// <summary>
/// Configuration parameters for the 6-state Series-Parallel CSTR Extended Kalman Filter.
/// </summary>
public sealed record SpEkfConfig(
    double SigmaTempMeasK = 0.35,
    double SigmaJacketTempMeasK = 0.35,
    double ProcessNoiseConc = 1e-5,
    double ProcessNoiseTempK = 0.04,
    double ProcessNoiseJacketTempK = 0.04,
    double NisFaultThreshold2Dof = 13.8155 // chi2(df=2, p=0.999)
);

/// <summary>
/// Per-step EKF state estimate and innovation diagnostics.
/// </summary>
public sealed record SpEkfUpdateResult(
    double CaMolPerL,
    double CbMolPerL,
    double CcMolPerL,
    double CdMolPerL,
    double TempKelvin,
    double JacketTempKelvin,
    double InnovationTempK,
    double InnovationJacketTempK,
    double Nis,
    bool SensorFaultDetected
);

/// <summary>
/// Economic Nonlinear Model Predictive Control (e-NMPC) tuning parameters
/// for the series-parallel CSTR (A -> B -> C, A -> D).
/// </summary>
public sealed record SpNmpcConfig(
    int HorizonSteps = 10,
    int ControlBlocks = 4,
    double SampleTimeMin = 0.05,
    double WeightCbEconomic = 60.0,
    double WeightByproduct = 10.0,
    double WeightDeltaU = 0.015,
    double WeightTempSoft = 0.08,
    double WeightTempHard = 200.0,
    double MaxSafeTempK = 400.0,
    double SafetyMarginK = 10.0,
    double? TargetCbMolPerL = null,
    double ValveMinPct = 0.0,
    double ValveMaxPct = 100.0,
    double MaxSlewPerStepPct = 35.0,
    int MaxIterations = 12
);

/// <summary>
/// Single-step output returned by <see cref="SeriesParallelEconomicNmpc"/>.
/// </summary>
public sealed record SpNmpcControlAction(
    double ValveCommandPct,
    double ObjectiveCost,
    double PredictedPeakTempK,
    double PredictedEndCbMolPerL,
    double[] PlannedValveBlocksPct
);

/// <summary>
/// Input scenario specification for an Open-Loop vs Closed-Loop (EKF6 + e-NMPC) comparison benchmark.
/// </summary>
public sealed record SpClosedLoopScenario(
    int TotalSteps = 80,
    double SampleTimeMin = 0.05,
    double InitialValvePct = 15.0,
    double NominalFeedTempC = 51.85,
    double DisturbanceFeedTempC = 76.85,
    int DisturbanceStep = 12,
    double CaFeedMolPerL = 1.0,
    double JacketTInC = 21.85,
    double InitialCaMolPerL = 0.60,
    double InitialCbMolPerL = 0.25,
    double InitialCcMolPerL = 0.05,
    double InitialCdMolPerL = 0.02,
    double InitialTempC = 76.85,
    double InitialJacketTempC = 36.85,
    double InitialEkfCaBias = 0.20,
    double InitialEkfCbBias = -0.10,
    int RngSeed = 42,
    SpEkfConfig? EkfConfig = null,
    SpNmpcConfig? NmpcConfig = null
);

/// <summary>
/// One synchronized timestamp comparison between Open-Loop and Closed-Loop trajectories.
/// </summary>
public sealed record SpClosedLoopStepPoint(
    int Step,
    double TimeMin,
    double FeedTempC,
    // Open-loop baseline (fixed initial valve)
    double OpenLoopTempC,
    double OpenLoopJacketTempC,
    double OpenLoopCaMolPerL,
    double OpenLoopCbMolPerL,
    double OpenLoopSelectivityBPct,
    double OpenLoopValvePct,
    bool OpenLoopRunaway,
    // Closed-loop plant (EKF6 + e-NMPC)
    double ClosedLoopTempC,
    double ClosedLoopJacketTempC,
    double ClosedLoopCaMolPerL,
    double ClosedLoopCbMolPerL,
    double ClosedLoopCcMolPerL,
    double ClosedLoopCdMolPerL,
    double ClosedLoopConversionAPct,
    double ClosedLoopYieldBPct,
    double ClosedLoopSelectivityBPct,
    double ClosedLoopValvePct,
    bool ClosedLoopRunaway,
    // EKF6 observer & fault diagnostics
    double EkfCaMolPerL,
    double EkfCbMolPerL,
    double EkfTempC,
    double EkfJacketTempC,
    double Nis,
    bool SensorFaultDetected,
    double NmpcObjectiveCost
);

/// <summary>
/// Full Open-Loop vs Closed-Loop trajectory comparison and aggregated performance KPIs.
/// </summary>
public sealed record SpClosedLoopComparisonResult(
    string EngineMode,
    double MaxSafeTempC,
    double OpenLoopPeakTempC,
    double OpenLoopFinalCbMolPerL,
    double OpenLoopFinalSelectivityBPct,
    bool OpenLoopRunawayTriggered,
    double ClosedLoopPeakTempC,
    double ClosedLoopFinalCbMolPerL,
    double ClosedLoopMeanCbMolPerL,
    double ClosedLoopFinalSelectivityBPct,
    bool ClosedLoopRunawayTriggered,
    double EkfFinalCbAbsErrorMolPerL,
    double EkfMeanCbRmseMolPerL,
    double MaxNis,
    int FaultCount,
    IReadOnlyList<SpClosedLoopStepPoint> Trajectory
);

/// <summary>
/// Shared valve-actuator physics and RK4 helper utilities backed by <see cref="NativeReactorCoreInterop"/>.
/// </summary>
public static class SeriesParallelActuatorModel
{
    public const double KelvinOffset = 273.15;

    /// <summary>
    /// Maps a control valve command [0..100 %] to jacket heat-transfer coefficient (UA)
    /// and coolant volumetric flowrate while preserving reactor kinetics and feed settings.
    /// </summary>
    public static CstrSeriesParallelParamsFFI ApplyValveCommand(
        in CstrSeriesParallelParamsFFI baseParams,
        double valveCommandPct)
    {
        double clamped = Math.Clamp(valveCommandPct, 0.0, 100.0);
        double valveFrac = clamped / 100.0;

        var actuated = baseParams;
        actuated.UaJPerMinK = (0.04 + 0.96 * valveFrac) * 600_000.0;
        actuated.JacketFlowLPerMin = 15.0 + 225.0 * valveFrac;
        return actuated;
    }

    public static void ClampState(ref double ca, ref double cb, ref double cc, ref double cd, ref double tempK, ref double tempJK)
    {
        if (ca < 0.0 || double.IsNaN(ca)) ca = 0.0;
        if (cb < 0.0 || double.IsNaN(cb)) cb = 0.0;
        if (cc < 0.0 || double.IsNaN(cc)) cc = 0.0;
        if (cd < 0.0 || double.IsNaN(cd)) cd = 0.0;
        tempK = double.IsNaN(tempK) ? 350.0 : Math.Clamp(tempK, 200.0, 1500.0);
        tempJK = double.IsNaN(tempJK) ? 300.0 : Math.Clamp(tempJK, 200.0, 1500.0);
    }
}

/// <summary>
/// 6-state Continuous-Discrete Extended Kalman Filter estimating unmeasured species concentrations
/// [Ca, Cb, Cc, Cd] and temperatures [T, Tj] solely from reactor/jacket thermocouple readings.
/// Uses <see cref="NativeReactorCoreInterop.StepSeriesParallelBatchRk4"/> for SIMD Jacobian sensitivity rollouts.
/// </summary>
public sealed class SeriesParallelEkf6
{
    private const int StateDim = 6;
    private static readonly double[] FiniteDiffEps = [1e-4, 1e-4, 1e-4, 1e-4, 1e-2, 1e-2];

    private readonly double[] _x = new double[StateDim];
    private readonly double[,] _p = new double[StateDim, StateDim];
    private readonly double[] _qDiag;
    private readonly double _rT;
    private readonly double _rTj;
    private readonly double _nisThreshold;

    // Preallocated scratch buffers for 12 central finite-difference Jacobian rollouts (batch AVX2/AVX-512)
    private readonly double[] _jacCa = new double[12];
    private readonly double[] _jacCb = new double[12];
    private readonly double[] _jacCc = new double[12];
    private readonly double[] _jacCd = new double[12];
    private readonly double[] _jacT = new double[12];
    private readonly double[] _jacTj = new double[12];

    public SeriesParallelEkf6(
        double ca0 = 0.5,
        double cb0 = 0.2,
        double cc0 = 0.02,
        double cd0 = 0.01,
        double tempK0 = 350.0,
        double jacketTempK0 = 310.0,
        SpEkfConfig? config = null)
    {
        var cfg = config ?? new SpEkfConfig();
        _x[0] = Math.Max(0.0, ca0);
        _x[1] = Math.Max(0.0, cb0);
        _x[2] = Math.Max(0.0, cc0);
        _x[3] = Math.Max(0.0, cd0);
        _x[4] = Math.Clamp(tempK0, 200.0, 1500.0);
        _x[5] = Math.Clamp(jacketTempK0, 200.0, 1500.0);

        double[] p0Diag = [0.04, 0.04, 0.01, 0.01, 1.0, 1.0];
        for (int i = 0; i < StateDim; i++)
            _p[i, i] = p0Diag[i];

        _qDiag =
        [
            cfg.ProcessNoiseConc,
            cfg.ProcessNoiseConc,
            cfg.ProcessNoiseConc,
            cfg.ProcessNoiseConc,
            cfg.ProcessNoiseTempK,
            cfg.ProcessNoiseJacketTempK
        ];
        _rT = cfg.SigmaTempMeasK * cfg.SigmaTempMeasK;
        _rTj = cfg.SigmaJacketTempMeasK * cfg.SigmaJacketTempMeasK;
        _nisThreshold = cfg.NisFaultThreshold2Dof;
    }

    public ReadOnlySpan<double> State => _x;

    public void Predict(in CstrSeriesParallelParamsFFI baseParams, double valveCommandPct, double dtMin)
    {
        var pAct = SeriesParallelActuatorModel.ApplyValveCommand(in baseParams, valveCommandPct);

        // Prepare 12 central finite-difference perturbation states around current estimate _x
        for (int j = 0; j < StateDim; j++)
        {
            int posIdx = 2 * j;
            int negIdx = 2 * j + 1;

            _jacCa[posIdx] = _x[0] + (j == 0 ? FiniteDiffEps[j] : 0.0);
            _jacCb[posIdx] = _x[1] + (j == 1 ? FiniteDiffEps[j] : 0.0);
            _jacCc[posIdx] = _x[2] + (j == 2 ? FiniteDiffEps[j] : 0.0);
            _jacCd[posIdx] = _x[3] + (j == 3 ? FiniteDiffEps[j] : 0.0);
            _jacT[posIdx] = _x[4] + (j == 4 ? FiniteDiffEps[j] : 0.0);
            _jacTj[posIdx] = _x[5] + (j == 5 ? FiniteDiffEps[j] : 0.0);

            _jacCa[negIdx] = Math.Max(0.0, _x[0] - (j == 0 ? FiniteDiffEps[j] : 0.0));
            _jacCb[negIdx] = Math.Max(0.0, _x[1] - (j == 1 ? FiniteDiffEps[j] : 0.0));
            _jacCc[negIdx] = Math.Max(0.0, _x[2] - (j == 2 ? FiniteDiffEps[j] : 0.0));
            _jacCd[negIdx] = Math.Max(0.0, _x[3] - (j == 3 ? FiniteDiffEps[j] : 0.0));
            _jacT[negIdx] = _x[4] - (j == 4 ? FiniteDiffEps[j] : 0.0);
            _jacTj[negIdx] = _x[5] - (j == 5 ? FiniteDiffEps[j] : 0.0);
        }

        // Propagate nominal state and 12 Jacobian perturbations through unified FFI RK4 engine
        double ca = _x[0], cb = _x[1], cc = _x[2], cd = _x[3], t = _x[4], tj = _x[5];
        NativeReactorCoreInterop.StepSeriesParallelRk4(in pAct, ref ca, ref cb, ref cc, ref cd, ref t, ref tj, dtMin);
        SeriesParallelActuatorModel.ClampState(ref ca, ref cb, ref cc, ref cd, ref t, ref tj);

        NativeReactorCoreInterop.StepSeriesParallelBatchRk4(
            in pAct, _jacCa, _jacCb, _jacCc, _jacCd, _jacT, _jacTj, dtMin);

        var phi = new double[StateDim, StateDim];
        for (int j = 0; j < StateDim; j++)
        {
            int posIdx = 2 * j;
            int negIdx = 2 * j + 1;
            double negVal = _x[j] - FiniteDiffEps[j];
            double effectiveNeg = j < 4 ? Math.Max(0.0, negVal) : negVal;
            double denom = (_x[j] + FiniteDiffEps[j]) - effectiveNeg;
            if (denom <= 1e-12) denom = FiniteDiffEps[j];

            phi[0, j] = (_jacCa[posIdx] - _jacCa[negIdx]) / denom;
            phi[1, j] = (_jacCb[posIdx] - _jacCb[negIdx]) / denom;
            phi[2, j] = (_jacCc[posIdx] - _jacCc[negIdx]) / denom;
            phi[3, j] = (_jacCd[posIdx] - _jacCd[negIdx]) / denom;
            phi[4, j] = (_jacT[posIdx] - _jacT[negIdx]) / denom;
            phi[5, j] = (_jacTj[posIdx] - _jacTj[negIdx]) / denom;
        }

        // Advance mean state
        _x[0] = ca;
        _x[1] = cb;
        _x[2] = cc;
        _x[3] = cd;
        _x[4] = t;
        _x[5] = tj;

        // Propagate covariance: P = Phi * P * Phi^T + Q
        var temp = new double[StateDim, StateDim];
        for (int i = 0; i < StateDim; i++)
        {
            for (int k = 0; k < StateDim; k++)
            {
                double sum = 0.0;
                for (int m = 0; m < StateDim; m++)
                    sum += phi[i, m] * _p[m, k];
                temp[i, k] = sum;
            }
        }

        var nextP = new double[StateDim, StateDim];
        for (int i = 0; i < StateDim; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = i == j ? _qDiag[i] : 0.0;
                for (int k = 0; k < StateDim; k++)
                    sum += temp[i, k] * phi[j, k];
                nextP[i, j] = sum;
                nextP[j, i] = sum;
            }
        }

        Array.Copy(nextP, _p, StateDim * StateDim);
    }

    public SpEkfUpdateResult Update(double measuredTempK, double measuredJacketTempK)
    {
        double nu0 = measuredTempK - _x[4];
        double nu1 = measuredJacketTempK - _x[5];

        // H selects indices 4 and 5: S = H*P*H^T + R (2x2)
        double s00 = _p[4, 4] + _rT;
        double s01 = _p[4, 5];
        double s11 = _p[5, 5] + _rTj;

        double det = s00 * s11 - s01 * s01;
        if (Math.Abs(det) < 1e-12) det = 1e-12;
        double invDet = 1.0 / det;

        double invS00 = s11 * invDet;
        double invS01 = -s01 * invDet;
        double invS11 = s00 * invDet;

        double nis = nu0 * (invS00 * nu0 + invS01 * nu1) + nu1 * (invS01 * nu0 + invS11 * nu1);
        bool fault = nis > _nisThreshold;

        // Kalman gain K = P * H^T * S^-1 (6x2)
        var gain = new double[StateDim, 2];
        for (int i = 0; i < StateDim; i++)
        {
            double p4 = _p[i, 4];
            double p5 = _p[i, 5];
            gain[i, 0] = p4 * invS00 + p5 * invS01;
            gain[i, 1] = p4 * invS01 + p5 * invS11;
        }

        // State update x = x + K * nu
        for (int i = 0; i < StateDim; i++)
            _x[i] += gain[i, 0] * nu0 + gain[i, 1] * nu1;

        SeriesParallelActuatorModel.ClampState(ref _x[0], ref _x[1], ref _x[2], ref _x[3], ref _x[4], ref _x[5]);

        // Joseph stabilized covariance update: P = (I - K H) P (I - K H)^T + K R K^T
        var iMinusKh = new double[StateDim, StateDim];
        for (int i = 0; i < StateDim; i++)
        {
            for (int j = 0; j < StateDim; j++)
                iMinusKh[i, j] = i == j ? 1.0 : 0.0;
            iMinusKh[i, 4] -= gain[i, 0];
            iMinusKh[i, 5] -= gain[i, 1];
        }

        var tmp = new double[StateDim, StateDim];
        for (int i = 0; i < StateDim; i++)
        {
            for (int k = 0; k < StateDim; k++)
            {
                double sum = 0.0;
                for (int m = 0; m < StateDim; m++)
                    sum += iMinusKh[i, m] * _p[m, k];
                tmp[i, k] = sum;
            }
        }

        var nextP = new double[StateDim, StateDim];
        for (int i = 0; i < StateDim; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = gain[i, 0] * _rT * gain[j, 0] + gain[i, 1] * _rTj * gain[j, 1];
                for (int k = 0; k < StateDim; k++)
                    sum += tmp[i, k] * iMinusKh[j, k];
                if (i == j && sum < 1e-9)
                    sum = 1e-9;
                nextP[i, j] = sum;
                nextP[j, i] = sum;
            }
        }

        Array.Copy(nextP, _p, StateDim * StateDim);

        return new SpEkfUpdateResult(
            CaMolPerL: _x[0],
            CbMolPerL: _x[1],
            CcMolPerL: _x[2],
            CdMolPerL: _x[3],
            TempKelvin: _x[4],
            JacketTempKelvin: _x[5],
            InnovationTempK: nu0,
            InnovationJacketTempK: nu1,
            Nis: nis,
            SensorFaultDetected: fault
        );
    }
}

/// <summary>
/// Move-blocked Economic Nonlinear Model Predictive Controller (e-NMPC) maximizing
/// desired intermediate Cb yield while suppressing byproducts (Cc + Cd) and enforcing
/// smooth valve actuation and strict thermal runaway boundaries.
/// </summary>
public sealed class SeriesParallelEconomicNmpc
{
    private readonly SpNmpcConfig _cfg;
    private double[] _warmPlan;

    public SeriesParallelEconomicNmpc(SpNmpcConfig? config = null, double initialValvePct = 15.0)
    {
        _cfg = config ?? new SpNmpcConfig();
        if (_cfg.HorizonSteps < 2 || _cfg.ControlBlocks < 1 || _cfg.ControlBlocks > _cfg.HorizonSteps)
            throw new ArgumentException("NMPC HorizonSteps must be >= 2 and 1 <= ControlBlocks <= HorizonSteps.");

        _warmPlan = new double[_cfg.ControlBlocks];
        double clamped = Math.Clamp(initialValvePct, _cfg.ValveMinPct, _cfg.ValveMaxPct);
        Array.Fill(_warmPlan, clamped);
    }

    public SpNmpcControlAction ComputeAction(
        in CstrSeriesParallelParamsFFI baseParams,
        double caEst,
        double cbEst,
        double ccEst,
        double cdEst,
        double tempKEst,
        double jacketTempKEst,
        double prevValvePct)
    {
        int m = _cfg.ControlBlocks;
        var candidates = new List<double[]>(5);

        // Warm-started shifted plan
        var shifted = new double[m];
        for (int i = 0; i < m - 1; i++) shifted[i] = _warmPlan[i + 1];
        shifted[m - 1] = _warmPlan[m - 1];
        ProjectToConstraints(shifted, prevValvePct);
        candidates.Add(shifted);

        // Hold previous, medium cooling, max cooling, and minimum cooling seeds
        foreach (double level in new[] { prevValvePct, 55.0, _cfg.ValveMaxPct, _cfg.ValveMinPct })
        {
            var seed = new double[m];
            Array.Fill(seed, level);
            ProjectToConstraints(seed, prevValvePct);
            candidates.Add(seed);
        }

        double[] bestU = candidates[0];
        var (bestCost, bestPeakK, bestEndCb) = EvaluateRollout(
            bestU, in baseParams, caEst, cbEst, ccEst, cdEst, tempKEst, jacketTempKEst, prevValvePct);

        for (int c = 1; c < candidates.Count; c++)
        {
            var (cost, peakK, endCb) = EvaluateRollout(
                candidates[c], in baseParams, caEst, cbEst, ccEst, cdEst, tempKEst, jacketTempKEst, prevValvePct);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestPeakK = peakK;
                bestEndCb = endCb;
                bestU = candidates[c];
            }
        }

        // Projected gradient descent with Armijo backtracking line search
        var grad = new double[m];
        const double fdEps = 0.5; // 0.5% valve perturbation
        double stepSize = 20.0;

        for (int iter = 0; iter < _cfg.MaxIterations; iter++)
        {
            double gradNormSq = 0.0;
            for (int b = 0; b < m; b++)
            {
                var uPert = (double[])bestU.Clone();
                double delta = uPert[b] + fdEps <= _cfg.ValveMaxPct ? fdEps : -fdEps;
                uPert[b] += delta;
                var (costPert, _, _) = EvaluateRollout(
                    uPert, in baseParams, caEst, cbEst, ccEst, cdEst, tempKEst, jacketTempKEst, prevValvePct);
                grad[b] = (costPert - bestCost) / delta;
                gradNormSq += grad[b] * grad[b];
            }

            if (gradNormSq < 1e-8)
                break;

            bool accepted = false;
            double alpha = stepSize;
            for (int ls = 0; ls < 5; ls++)
            {
                var trial = new double[m];
                for (int b = 0; b < m; b++)
                    trial[b] = bestU[b] - alpha * grad[b];
                ProjectToConstraints(trial, prevValvePct);

                var (trialCost, trialPeakK, trialEndCb) = EvaluateRollout(
                    trial, in baseParams, caEst, cbEst, ccEst, cdEst, tempKEst, jacketTempKEst, prevValvePct);

                if (trialCost < bestCost - 1e-6)
                {
                    bestU = trial;
                    bestCost = trialCost;
                    bestPeakK = trialPeakK;
                    bestEndCb = trialEndCb;
                    accepted = true;
                    break;
                }
                alpha *= 0.45;
            }

            if (!accepted)
            {
                stepSize *= 0.5;
                if (stepSize < 0.25) break;
            }
        }

        _warmPlan = bestU;
        return new SpNmpcControlAction(
            ValveCommandPct: bestU[0],
            ObjectiveCost: bestCost,
            PredictedPeakTempK: bestPeakK,
            PredictedEndCbMolPerL: bestEndCb,
            PlannedValveBlocksPct: (double[])bestU.Clone()
        );
    }

    private void ProjectToConstraints(double[] uBlocks, double prevValvePct)
    {
        double anchor = prevValvePct;
        for (int i = 0; i < uBlocks.Length; i++)
        {
            double bounded = Math.Clamp(uBlocks[i], _cfg.ValveMinPct, _cfg.ValveMaxPct);
            double lo = Math.Max(_cfg.ValveMinPct, anchor - _cfg.MaxSlewPerStepPct);
            double hi = Math.Min(_cfg.ValveMaxPct, anchor + _cfg.MaxSlewPerStepPct);
            uBlocks[i] = Math.Clamp(bounded, lo, hi);
            anchor = uBlocks[i];
        }
    }

    private (double Cost, double PeakTempK, double EndCb) EvaluateRollout(
        double[] uBlocks,
        in CstrSeriesParallelParamsFFI baseParams,
        double ca,
        double cb,
        double cc,
        double cd,
        double tempK,
        double jacketTempK,
        double prevValvePct)
    {
        int n = _cfg.HorizonSteps;
        int m = _cfg.ControlBlocks;
        double totalCost = 0.0;
        double peakTempK = tempK;
        double uPrev = prevValvePct;
        double softLimitK = _cfg.MaxSafeTempK - _cfg.SafetyMarginK;

        for (int k = 0; k < n; k++)
        {
            int blockIdx = Math.Min((k * m) / n, m - 1);
            double uCurr = uBlocks[blockIdx];
            var pAct = SeriesParallelActuatorModel.ApplyValveCommand(in baseParams, uCurr);

            NativeReactorCoreInterop.StepSeriesParallelRk4(
                in pAct, ref ca, ref cb, ref cc, ref cd, ref tempK, ref jacketTempK, _cfg.SampleTimeMin);
            SeriesParallelActuatorModel.ClampState(ref ca, ref cb, ref cc, ref cd, ref tempK, ref jacketTempK);

            if (tempK > peakTempK)
                peakTempK = tempK;

            double duNorm = (uCurr - uPrev) / 100.0;
            uPrev = uCurr;

            double terminalWeight = k == n - 1 ? 2.5 : 1.0;

            double stageCbCost = _cfg.TargetCbMolPerL.HasValue
                ? _cfg.WeightCbEconomic * Math.Pow(cb - _cfg.TargetCbMolPerL.Value, 2)
                : -_cfg.WeightCbEconomic * cb;

            double byproductCost = _cfg.WeightByproduct * (cc + cd);
            double slewCost = _cfg.WeightDeltaU * duNorm * duNorm;

            double tempViolationSoft = Math.Max(0.0, tempK - softLimitK);
            double tempViolationHard = Math.Max(0.0, tempK - _cfg.MaxSafeTempK);
            double thermalCost = _cfg.WeightTempSoft * tempViolationSoft * tempViolationSoft
                               + _cfg.WeightTempHard * tempViolationHard * tempViolationHard;

            totalCost += terminalWeight * (stageCbCost + byproductCost + thermalCost) + slewCost;
        }

        return (totalCost, peakTempK, cb);
    }
}

/// <summary>
/// Closed-loop vs open-loop digital-twin runner comparing fixed-valve operation against
/// 6-state EKF state estimation + Economic NMPC regulation.
/// </summary>
public static class SeriesParallelClosedLoopSimulator
{
    public static SpClosedLoopComparisonResult RunComparison(SpClosedLoopScenario? scenario = null)
    {
        var sc = scenario ?? new SpClosedLoopScenario();
        int totalSteps = Math.Clamp(sc.TotalSteps, 5, 600);
        double dt = Math.Clamp(sc.SampleTimeMin, 0.005, 1.0);

        var ekfCfg = sc.EkfConfig ?? new SpEkfConfig();
        var nmpcCfg = (sc.NmpcConfig ?? new SpNmpcConfig()) with { SampleTimeMin = dt };
        double maxSafeTempK = nmpcCfg.MaxSafeTempK;
        double maxSafeTempC = maxSafeTempK - SeriesParallelActuatorModel.KelvinOffset;

        var baseParams = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        baseParams.CaFeedMolPerL = sc.CaFeedMolPerL;
        baseParams.JacketTInKelvin = sc.JacketTInC + SeriesParallelActuatorModel.KelvinOffset;

        // Open-loop plant state
        double olCa = sc.InitialCaMolPerL;
        double olCb = sc.InitialCbMolPerL;
        double olCc = sc.InitialCcMolPerL;
        double olCd = sc.InitialCdMolPerL;
        double olT = sc.InitialTempC + SeriesParallelActuatorModel.KelvinOffset;
        double olTj = sc.InitialJacketTempC + SeriesParallelActuatorModel.KelvinOffset;

        // Closed-loop plant state
        double clCa = sc.InitialCaMolPerL;
        double clCb = sc.InitialCbMolPerL;
        double clCc = sc.InitialCcMolPerL;
        double clCd = sc.InitialCdMolPerL;
        double clT = sc.InitialTempC + SeriesParallelActuatorModel.KelvinOffset;
        double clTj = sc.InitialJacketTempC + SeriesParallelActuatorModel.KelvinOffset;

        // Intentionally biased initial EKF estimates to verify observer convergence
        var ekf = new SeriesParallelEkf6(
            ca0: Math.Max(0.05, sc.InitialCaMolPerL + sc.InitialEkfCaBias),
            cb0: Math.Max(0.01, sc.InitialCbMolPerL + sc.InitialEkfCbBias),
            cc0: sc.InitialCcMolPerL,
            cd0: sc.InitialCdMolPerL,
            tempK0: clT,
            jacketTempK0: clTj,
            config: ekfCfg);

        var nmpc = new SeriesParallelEconomicNmpc(nmpcCfg, sc.InitialValvePct);
        var rng = new Random(sc.RngSeed);

        double prevValve = sc.InitialValvePct;
        var points = new List<SpClosedLoopStepPoint>(totalSteps);

        double olPeakT = double.NegativeInfinity;
        double clPeakT = double.NegativeInfinity;
        bool olRunawayTriggered = false;
        bool clRunawayTriggered = false;
        double sumCb = 0.0;
        double sumSqCbErr = 0.0;
        double maxNis = 0.0;
        int faultCount = 0;

        for (int step = 0; step < totalSteps; step++)
        {
            double feedTempC = step >= sc.DisturbanceStep ? sc.DisturbanceFeedTempC : sc.NominalFeedTempC;
            baseParams.TFeedKelvin = feedTempC + SeriesParallelActuatorModel.KelvinOffset;

            // Open-loop baseline holds initial valve command
            var olActParams = SeriesParallelActuatorModel.ApplyValveCommand(in baseParams, sc.InitialValvePct);
            NativeReactorCoreInterop.StepSeriesParallelDetailed(
                in olActParams,
                ref olCa,
                ref olCb,
                ref olCc,
                ref olCd,
                ref olT,
                ref olTj,
                dt,
                maxSafeTempK,
                out var olDetailed);

            // Closed-loop EKF predict -> noisy thermocouple update -> e-NMPC action -> plant step
            var est = ekf.State;
            var nmpcAction = nmpc.ComputeAction(
                in baseParams,
                est[0], est[1], est[2], est[3], est[4], est[5],
                prevValve);

            double clValve = nmpcAction.ValveCommandPct;
            var clActParams = SeriesParallelActuatorModel.ApplyValveCommand(in baseParams, clValve);
            NativeReactorCoreInterop.StepSeriesParallelDetailed(
                in clActParams,
                ref clCa,
                ref clCb,
                ref clCc,
                ref clCd,
                ref clT,
                ref clTj,
                dt,
                maxSafeTempK,
                out var clDetailed);

            ekf.Predict(in baseParams, clValve, dt);
            double measT = clT + NextGaussian(rng) * ekfCfg.SigmaTempMeasK;
            double measTj = clTj + NextGaussian(rng) * ekfCfg.SigmaJacketTempMeasK;
            var ekfUpdate = ekf.Update(measT, measTj);
            prevValve = clValve;

            double olTc = olT - SeriesParallelActuatorModel.KelvinOffset;
            double olTjc = olTj - SeriesParallelActuatorModel.KelvinOffset;
            double clTc = clT - SeriesParallelActuatorModel.KelvinOffset;
            double clTjc = clTj - SeriesParallelActuatorModel.KelvinOffset;
            double ekfTc = ekfUpdate.TempKelvin - SeriesParallelActuatorModel.KelvinOffset;
            double ekfTjc = ekfUpdate.JacketTempKelvin - SeriesParallelActuatorModel.KelvinOffset;

            if (olTc > olPeakT) olPeakT = olTc;
            if (clTc > clPeakT) clPeakT = clTc;
            olRunawayTriggered |= olDetailed.IsThermalRunaway;
            clRunawayTriggered |= clDetailed.IsThermalRunaway;

            sumCb += clCb;
            double cbErr = clCb - ekfUpdate.CbMolPerL;
            sumSqCbErr += cbErr * cbErr;
            if (ekfUpdate.Nis > maxNis) maxNis = ekfUpdate.Nis;
            if (ekfUpdate.SensorFaultDetected) faultCount++;

            points.Add(new SpClosedLoopStepPoint(
                Step: step,
                TimeMin: Math.Round((step + 1) * dt, 4),
                FeedTempC: Math.Round(feedTempC, 2),
                OpenLoopTempC: Math.Round(olTc, 2),
                OpenLoopJacketTempC: Math.Round(olTjc, 2),
                OpenLoopCaMolPerL: Math.Round(olCa, 4),
                OpenLoopCbMolPerL: Math.Round(olCb, 4),
                OpenLoopSelectivityBPct: Math.Round(olDetailed.SelectivityBPct, 2),
                OpenLoopValvePct: Math.Round(sc.InitialValvePct, 2),
                OpenLoopRunaway: olDetailed.IsThermalRunaway,
                ClosedLoopTempC: Math.Round(clTc, 2),
                ClosedLoopJacketTempC: Math.Round(clTjc, 2),
                ClosedLoopCaMolPerL: Math.Round(clCa, 4),
                ClosedLoopCbMolPerL: Math.Round(clCb, 4),
                ClosedLoopCcMolPerL: Math.Round(clCc, 4),
                ClosedLoopCdMolPerL: Math.Round(clCd, 4),
                ClosedLoopConversionAPct: Math.Round(clDetailed.ConversionAPct, 2),
                ClosedLoopYieldBPct: Math.Round(clDetailed.YieldBPct, 2),
                ClosedLoopSelectivityBPct: Math.Round(clDetailed.SelectivityBPct, 2),
                ClosedLoopValvePct: Math.Round(clValve, 2),
                ClosedLoopRunaway: clDetailed.IsThermalRunaway,
                EkfCaMolPerL: Math.Round(ekfUpdate.CaMolPerL, 4),
                EkfCbMolPerL: Math.Round(ekfUpdate.CbMolPerL, 4),
                EkfTempC: Math.Round(ekfTc, 2),
                EkfJacketTempC: Math.Round(ekfTjc, 2),
                Nis: Math.Round(ekfUpdate.Nis, 3),
                SensorFaultDetected: ekfUpdate.SensorFaultDetected,
                NmpcObjectiveCost: Math.Round(nmpcAction.ObjectiveCost, 3)
            ));
        }

        var last = points[^1];
        string engineMode = NativeReactorCoreInterop.IsNativeAvailable
            ? $"Native C11 SIMD ({NativeReactorCoreInterop.VersionString})"
            : "Managed C# Fallback";

        return new SpClosedLoopComparisonResult(
            EngineMode: engineMode,
            MaxSafeTempC: Math.Round(maxSafeTempC, 2),
            OpenLoopPeakTempC: Math.Round(olPeakT, 2),
            OpenLoopFinalCbMolPerL: last.OpenLoopCbMolPerL,
            OpenLoopFinalSelectivityBPct: last.OpenLoopSelectivityBPct,
            OpenLoopRunawayTriggered: olRunawayTriggered,
            ClosedLoopPeakTempC: Math.Round(clPeakT, 2),
            ClosedLoopFinalCbMolPerL: last.ClosedLoopCbMolPerL,
            ClosedLoopMeanCbMolPerL: Math.Round(sumCb / totalSteps, 4),
            ClosedLoopFinalSelectivityBPct: last.ClosedLoopSelectivityBPct,
            ClosedLoopRunawayTriggered: clRunawayTriggered,
            EkfFinalCbAbsErrorMolPerL: Math.Round(Math.Abs(last.ClosedLoopCbMolPerL - last.EkfCbMolPerL), 4),
            EkfMeanCbRmseMolPerL: Math.Round(Math.Sqrt(sumSqCbErr / totalSteps), 4),
            MaxNis: Math.Round(maxNis, 3),
            FaultCount: faultCount,
            Trajectory: points
        );
    }

    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
