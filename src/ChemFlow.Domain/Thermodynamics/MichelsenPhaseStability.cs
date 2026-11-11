using ChemFlow.Domain.Entities;

namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Implements Michelsen's (1982) Tangent Plane Distance (TPD) phase stability analysis
/// for multicomponent mixtures modeled with cubic equations of state (PR-EOS).
///
/// Determines whether a single-phase mixture at (T, P, z) is thermodynamically stable,
/// or whether it will spontaneously split into two phases (liquid + vapor).
/// When unstable, provides optimal stationary compositions to initialize two-phase flash iterations.
/// </summary>
public static class MichelsenPhaseStability
{
    public readonly record struct StabilityResult(
        bool IsTwoPhase,
        PhaseState InferredSinglePhase,
        double[] RefinedKValues,
        double VaporTrialSum,
        double LiquidTrialSum,
        int Iterations
    );

    /// <summary>
    /// Evaluates thermodynamic stability of feed composition z at (T, P).
    /// </summary>
    public static StabilityResult Evaluate(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> z,
        double temperatureKelvin,
        double pressureKpa,
        double[] initialWilsonK,
        Func<IReadOnlyList<double>, bool, double[]> fugacityEvaluator,
        int maxIterations = 50,
        double tolerance = 1e-8)
    {
        int n = compounds.Count;

        // 1. Compute feed fugacity coefficients in both reference states
        double[] phiZLiquid = fugacityEvaluator(z, false);
        double[] phiZVapor = fugacityEvaluator(z, true);

        // Compute Gibbs energy comparison for single-phase tie-breaker
        double gLiquid = 0.0;
        double gVapor = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (z[i] > 1e-12)
            {
                gLiquid += z[i] * (Math.Log(z[i]) + Math.Log(Math.Max(1e-12, phiZLiquid[i])));
                gVapor += z[i] * (Math.Log(z[i]) + Math.Log(Math.Max(1e-12, phiZVapor[i])));
            }
        }
        PhaseState singlePhaseFallback = gLiquid <= gVapor ? PhaseState.SubcooledLiquid : PhaseState.SuperheatedVapor;

        // 2. Vapor-like trial (incipient vapor bubble from liquid feed):
        // In(Y_i) = ln(z_i) + ln(phi_i^L(z)) - ln(phi_i^V(y))
        double[] lnDiVapor = new double[n];
        for (int i = 0; i < n; i++)
        {
            lnDiVapor[i] = Math.Log(Math.Max(1e-15, z[i])) + Math.Log(Math.Max(1e-15, phiZLiquid[i]));
        }

        double[] Y = new double[n];
        for (int i = 0; i < n; i++)
        {
            Y[i] = z[i] * initialWilsonK[i];
        }

        (bool vaporUnstable, double sumY, double[] yStationary, int vIters) = RunTrial(
            Y, lnDiVapor, fugacityEvaluator, isVaporTrial: true, z, maxIterations, tolerance);

        // 3. Liquid-like trial (incipient liquid droplet from vapor feed):
        // ln(X_i) = ln(z_i) + ln(phi_i^V(z)) - ln(phi_i^L(x))
        double[] lnDiLiquid = new double[n];
        for (int i = 0; i < n; i++)
        {
            lnDiLiquid[i] = Math.Log(Math.Max(1e-15, z[i])) + Math.Log(Math.Max(1e-15, phiZVapor[i]));
        }

        double[] X = new double[n];
        for (int i = 0; i < n; i++)
        {
            X[i] = z[i] / Math.Max(1e-6, initialWilsonK[i]);
        }

        (bool liquidUnstable, double sumX, double[] xStationary, int lIters) = RunTrial(
            X, lnDiLiquid, fugacityEvaluator, isVaporTrial: false, z, maxIterations, tolerance);

        bool isTwoPhase = vaporUnstable || liquidUnstable;
        double[] refinedK = (double[])initialWilsonK.Clone();

        if (isTwoPhase)
        {
            for (int i = 0; i < n; i++)
            {
                if (vaporUnstable && !liquidUnstable)
                {
                    double Y_i = yStationary[i] * sumY;
                    refinedK[i] = Math.Clamp(Y_i / Math.Max(1e-12, z[i]), 1e-5, 1e5);
                }
                else if (liquidUnstable && !vaporUnstable)
                {
                    double X_i = xStationary[i] * sumX;
                    refinedK[i] = Math.Clamp(z[i] / Math.Max(1e-12, X_i), 1e-5, 1e5);
                }
                else
                {
                    double Y_i = yStationary[i] * sumY;
                    double X_i = xStationary[i] * sumX;
                    refinedK[i] = Math.Clamp(Y_i / Math.Max(1e-12, X_i), 1e-5, 1e5);
                }
            }
        }

        return new StabilityResult(
            IsTwoPhase: isTwoPhase,
            InferredSinglePhase: singlePhaseFallback,
            RefinedKValues: refinedK,
            VaporTrialSum: sumY,
            LiquidTrialSum: sumX,
            Iterations: vIters + lIters
        );
    }

    private static (bool IsUnstable, double SumMol, double[] StationaryComposition, int Iterations) RunTrial(
        double[] trialMoles,
        double[] lnDi,
        Func<IReadOnlyList<double>, bool, double[]> fugacityEvaluator,
        bool isVaporTrial,
        IReadOnlyList<double> z,
        int maxIterations,
        double tolerance)
    {
        int n = trialMoles.Length;
        double[] current = (double[])trialMoles.Clone();
        double[] normalized = new double[n];
        int iterations = 0;

        for (int iter = 1; iter <= maxIterations; iter++)
        {
            iterations = iter;

            // Normalize mole fractions
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                current[i] = Math.Max(1e-16, current[i]);
                sum += current[i];
            }
            for (int i = 0; i < n; i++)
            {
                normalized[i] = current[i] / sum;
            }

            // Check trivial solution convergence: sum of (normalized[i] - z[i])^2 < 1e-5
            double trivialDist = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = normalized[i] - z[i];
                trivialDist += d * d;
            }
            if (trivialDist < 1e-5 && Math.Abs(sum - 1.0) < 1e-3)
            {
                // Converged to trivial stationary point (feed itself)
                return (false, sum, normalized, iter);
            }

            // Evaluate fugacity of trial phase
            double[] phiTrial = fugacityEvaluator(normalized, isVaporTrial);

            // Update trial moles: ln(Y_i) = ln(d_i) - ln(phi_i(y))
            double maxStep = 0.0;
            double nextSum = 0.0;

            for (int i = 0; i < n; i++)
            {
                double lnPhi = Math.Log(Math.Max(1e-15, phiTrial[i]));
                double nextLn = lnDi[i] - lnPhi;
                double nextVal = Math.Exp(Math.Clamp(nextLn, -40.0, 40.0));

                double diff = Math.Abs(nextVal - current[i]) / Math.Max(1e-6, current[i]);
                if (diff > maxStep) maxStep = diff;

                current[i] = nextVal;
                nextSum += nextVal;
            }

            if (maxStep < tolerance)
            {
                bool unstable = nextSum > (1.0 + 1e-4);
                return (unstable, nextSum, normalized, iter);
            }
        }

        double finalSum = 0.0;
        for (int i = 0; i < n; i++) finalSum += current[i];
        bool isUnstable = finalSum > (1.0 + 1e-4);
        return (isUnstable, finalSum, normalized, iterations);
    }
}
