namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Solves the isothermal Rachford-Rice flash equation for the molar vapor fraction β = V / F:
///   f(β) = Σ [ z_i * (K_i - 1) / (1 + β * (K_i - 1)) ] = 0
/// using a safeguarded Newton-Raphson + Bisection hybrid solver.
/// </summary>
public static class RachfordRiceSolver
{
    public readonly record struct Solution(
        PhaseState Phase,
        double Beta,
        double[] LiquidCompositionX,
        double[] VaporCompositionY,
        int Iterations
    );

    /// <summary>
    /// Evaluates the Rachford-Rice objective function f(β) at a given vapor fraction β.
    /// </summary>
    public static double EvaluateObjectiveFunction(
        IReadOnlyList<double> z,
        IReadOnlyList<double> kValues,
        double beta)
    {
        double sum = 0.0;
        for (int i = 0; i < z.Count; i++)
        {
            double deltaK = kValues[i] - 1.0;
            sum += (z[i] * deltaK) / (1.0 + beta * deltaK);
        }
        return sum;
    }

    /// <summary>
    /// Solves for the equilibrium phase state, vapor fraction β ∈ [0, 1], and phase compositions (x, y).
    /// </summary>
    public static Solution Solve(
        IReadOnlyList<double> z,
        IReadOnlyList<double> kValues,
        double tolerance = 1e-10,
        int maxIterations = 100)
    {
        if (z is null || z.Count == 0)
            throw new ArgumentException("Feed composition vector z cannot be empty.", nameof(z));
        if (kValues is null || kValues.Count != z.Count)
            throw new ArgumentException("K-values vector must have the same length as z.", nameof(kValues));

        int n = z.Count;
        double f0 = EvaluateObjectiveFunction(z, kValues, 0.0);

        // Case 1: Subcooled or saturated liquid (f(0) <= 0  <=>  Σ z_i * K_i <= 1)
        if (f0 <= 0.0)
        {
            double[] xLiquid = z.ToArray();
            double[] yIncipient = NormalizeProducts(z, kValues, multiply: true);
            return new Solution(PhaseState.SubcooledLiquid, 0.0, xLiquid, yIncipient, 0);
        }

        double f1 = EvaluateObjectiveFunction(z, kValues, 1.0);

        // Case 2: Superheated or saturated vapor (f(1) >= 0  <=>  Σ z_i / K_i <= 1)
        if (f1 >= 0.0)
        {
            double[] yVapor = z.ToArray();
            double[] xIncipient = NormalizeProducts(z, kValues, multiply: false);
            return new Solution(PhaseState.SuperheatedVapor, 1.0, xIncipient, yVapor, 0);
        }

        // Case 3: Two-phase Vapor-Liquid Equilibrium (f(0) > 0 and f(1) < 0)
        // Since f'(β) = -Σ [ z_i * (K_i - 1)^2 / (1 + β*(K_i - 1))^2 ] < 0 everywhere on [0, 1],
        // f(β) is strictly monotonically decreasing and has a unique root β* ∈ (0, 1).
        double betaLow = 0.0;
        double betaHigh = 1.0;
        double beta = 0.5;
        int iteration = 0;

        while (iteration < maxIterations)
        {
            iteration++;

            double f = 0.0;
            double df = 0.0;

            for (int i = 0; i < n; i++)
            {
                double deltaK = kValues[i] - 1.0;
                double denom = 1.0 + beta * deltaK;
                double term = deltaK / denom;

                f += z[i] * term;
                df -= z[i] * term * term;
            }

            if (Math.Abs(f) <= tolerance)
                break;

            // Update monotonic bracket [betaLow, betaHigh]
            if (f > 0.0)
                betaLow = beta;
            else
                betaHigh = beta;

            // Compute Newton-Raphson candidate step: β_next = β - f(β) / f'(β)
            double betaNewton = df != 0.0 ? beta - (f / df) : double.NaN;

            // Safeguard: accept Newton step only if it stays strictly inside (betaLow, betaHigh);
            // otherwise fall back to bisection midpoint.
            double betaNext = (betaNewton > betaLow && betaNewton < betaHigh)
                ? betaNewton
                : 0.5 * (betaLow + betaHigh);

            if (Math.Abs(betaNext - beta) <= tolerance)
            {
                beta = betaNext;
                break;
            }

            beta = betaNext;
        }

        // Compute equilibrium liquid (x_i) and vapor (y_i) mole fractions
        double[] x = new double[n];
        double[] y = new double[n];
        double sumX = 0.0;
        double sumY = 0.0;

        for (int i = 0; i < n; i++)
        {
            x[i] = z[i] / (1.0 + beta * (kValues[i] - 1.0));
            y[i] = kValues[i] * x[i];
            sumX += x[i];
            sumY += y[i];
        }

        // Normalize to eliminate any floating-point rounding drift
        for (int i = 0; i < n; i++)
        {
            x[i] /= sumX;
            y[i] /= sumY;
        }

        return new Solution(PhaseState.VaporLiquidEquilibrium, beta, x, y, iteration);
    }

    private static double[] NormalizeProducts(
        IReadOnlyList<double> z,
        IReadOnlyList<double> kValues,
        bool multiply)
    {
        int n = z.Count;
        double[] result = new double[n];
        double sum = 0.0;

        for (int i = 0; i < n; i++)
        {
            result[i] = multiply ? z[i] * kValues[i] : z[i] / kValues[i];
            sum += result[i];
        }

        if (sum > 0.0)
        {
            for (int i = 0; i < n; i++)
                result[i] /= sum;
        }

        return result;
    }
}
