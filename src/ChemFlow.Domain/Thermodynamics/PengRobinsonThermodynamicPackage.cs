using ChemFlow.Domain.Entities;

namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Implements the industry-standard Peng-Robinson (1976 / 1978) Cubic Equation of State (PR-EOS)
/// for non-ideal Vapor-Liquid Equilibrium (VLE), fugacity coefficients, and enthalpy departure.
///
/// Formulation:
///   P = R*T / (v - b) - a(T) / (v*(v + b) + b*(v - b))
///
/// Cubic form in compressibility factor Z:
///   Z^3 - (1 - B)*Z^2 + (A - 3*B^2 - 2*B)*Z - (A*B - B^2 - B^3) = 0
/// </summary>
public sealed class PengRobinsonThermodynamicPackage : IThermodynamicPackage
{
    public const double GasConstantR = 8.314462618; // J/(mol·K) = kPa·L/(mol·K)
    private static readonly double Sqrt2 = Math.Sqrt(2.0);
    private static readonly double Sigma1 = 1.0 + Math.Sqrt(2.0); // 1 + √2 ≈ 2.41421356
    private static readonly double Sigma2 = 1.0 - Math.Sqrt(2.0); // 1 - √2 ≈ -0.41421356

    private readonly double[,]? _binaryInteractionParameters;
    private readonly BinaryInteractionMatrix _binaryInteractionMatrix;
    private readonly MulticomponentIsothermalFlashSolver _flashSolver;

    public string Name => "Peng-Robinson EOS (1976)";
    public BinaryInteractionMatrix InteractionMatrix => _binaryInteractionMatrix;
    public MulticomponentIsothermalFlashSolver FlashSolver => _flashSolver;

    public PengRobinsonThermodynamicPackage(BinaryInteractionMatrix? binaryInteractionMatrix = null)
    {
        _binaryInteractionMatrix = binaryInteractionMatrix ?? BinaryInteractionMatrix.Empty;
        _binaryInteractionParameters = null;
        _flashSolver = new MulticomponentIsothermalFlashSolver(_binaryInteractionMatrix);
    }

    public PengRobinsonThermodynamicPackage(double[,]? binaryInteractionParameters)
    {
        _binaryInteractionParameters = binaryInteractionParameters;
        _binaryInteractionMatrix = BinaryInteractionMatrix.Empty;
        _flashSolver = new MulticomponentIsothermalFlashSolver(_binaryInteractionMatrix);
    }

    /// <summary>
    /// Computes equilibrium K-values K_i = y_i / x_i = φ_i^L / φ_i^V using PR-EOS fugacities.
    /// </summary>
    public double[] CalculateKValues(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> composition,
        double temperatureKelvin,
        double pressureKpa)
    {
        ValidateInputs(compounds, composition, temperatureKelvin, pressureKpa);

        // Flash calculation determines the equilibrium K-values
        FlashResult flash = CalculateIsothermalFlash(compounds, composition, temperatureKelvin, pressureKpa);
        return flash.KValues.ToArray();
    }

    /// <summary>
    /// Performs a rigorous multicomponent isothermal (T, P) flash calculation for feed composition z,
    /// returning phase state, vapor fraction β, phase compositions (x, y), and mixture molar enthalpy H.
    /// Coupled with Wilson K-values initialization, Michelsen stability analysis, and Wegstein acceleration.
    /// </summary>
    public FlashResult CalculateIsothermalFlash(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> overallCompositionZ,
        double temperatureKelvin,
        double pressureKpa)
    {
        var solver = _binaryInteractionParameters is not null
            ? new MulticomponentIsothermalFlashSolver(new BinaryInteractionMatrix(compounds, _binaryInteractionParameters))
            : _flashSolver;

        return solver.Solve(compounds, overallCompositionZ, temperatureKelvin, pressureKpa);
    }

    /// <summary>
    /// Computes the bubble-point pressure P_bubble in kPa at temperature T and liquid composition x.
    /// </summary>
    public double CalculateBubblePointPressureKpa(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> liquidCompositionX,
        double temperatureKelvin)
    {
        ValidateInputs(compounds, liquidCompositionX, temperatureKelvin, 101.325);

        // Initial estimate via Raoult/Wilson
        double pressure = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            pressure += liquidCompositionX[i] * compounds[i].GetSaturationPressureKpa(temperatureKelvin);
        }

        // Iterative refinement: P_new = P * Σ (x_i * φ_i^L / φ_i^V)
        double[] x = liquidCompositionX.ToArray();
        double[] y = new double[compounds.Count];

        for (int iter = 0; iter < 40; iter++)
        {
            double[] phiL = CalculateFugacityCoefficients(compounds, x, temperatureKelvin, pressure, isVapor: false);

            // Estimate incipient vapor composition y_i = x_i * (phiL_i / phiV_i)
            double[] phiVEstimate = CalculateFugacityCoefficients(compounds, x, temperatureKelvin, pressure, isVapor: true);
            double sumY = 0.0;
            for (int i = 0; i < compounds.Count; i++)
            {
                y[i] = x[i] * (phiL[i] / phiVEstimate[i]);
                sumY += y[i];
            }
            for (int i = 0; i < compounds.Count; i++) y[i] /= sumY;

            // Refine vapor fugacities with actual y
            double[] phiV = CalculateFugacityCoefficients(compounds, y, temperatureKelvin, pressure, isVapor: true);

            double sumKx = 0.0;
            for (int i = 0; i < compounds.Count; i++)
            {
                sumKx += x[i] * (phiL[i] / phiV[i]);
            }

            double nextPressure = pressure * sumKx;
            if (Math.Abs(nextPressure - pressure) / pressure < 1e-6)
                return nextPressure;

            pressure = 0.5 * pressure + 0.5 * nextPressure;
        }

        return pressure;
    }

    /// <summary>
    /// Computes the dew-point pressure P_dew in kPa at temperature T and vapor composition y.
    /// </summary>
    public double CalculateDewPointPressureKpa(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> vaporCompositionY,
        double temperatureKelvin)
    {
        ValidateInputs(compounds, vaporCompositionY, temperatureKelvin, 101.325);

        double invSum = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            invSum += vaporCompositionY[i] / compounds[i].GetSaturationPressureKpa(temperatureKelvin);
        }
        double pressure = 1.0 / invSum;

        double[] y = vaporCompositionY.ToArray();
        double[] x = new double[compounds.Count];

        for (int iter = 0; iter < 40; iter++)
        {
            double[] phiV = CalculateFugacityCoefficients(compounds, y, temperatureKelvin, pressure, isVapor: true);
            double[] phiLEstimate = CalculateFugacityCoefficients(compounds, y, temperatureKelvin, pressure, isVapor: false);

            double sumX = 0.0;
            for (int i = 0; i < compounds.Count; i++)
            {
                x[i] = y[i] / (phiLEstimate[i] / phiV[i]);
                sumX += x[i];
            }
            for (int i = 0; i < compounds.Count; i++) x[i] /= sumX;

            double[] phiL = CalculateFugacityCoefficients(compounds, x, temperatureKelvin, pressure, isVapor: false);

            double sumYOverK = 0.0;
            for (int i = 0; i < compounds.Count; i++)
            {
                double k = phiL[i] / phiV[i];
                sumYOverK += y[i] / k;
            }

            double nextPressure = pressure / sumYOverK;
            if (Math.Abs(nextPressure - pressure) / pressure < 1e-6)
                return nextPressure;

            pressure = 0.5 * pressure + 0.5 * nextPressure;
        }

        return pressure;
    }

    public double CalculateBubblePointTemperatureKelvin(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> liquidCompositionX,
        double pressureKpa)
    {
        // Initial guess: mole-fraction weighted saturation temperature
        double t = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            t += liquidCompositionX[i] * compounds[i].Antoine.CalculateSaturationTemperatureKelvin(pressureKpa);
        }

        // Secant method on g(T) = ln(P_bubble(T) / P) = 0
        double tPrev = t - 1.0;
        double fPrev = Math.Log(CalculateBubblePointPressureKpa(compounds, liquidCompositionX, tPrev) / pressureKpa);

        for (int iter = 0; iter < 30; iter++)
        {
            double f = Math.Log(CalculateBubblePointPressureKpa(compounds, liquidCompositionX, t) / pressureKpa);
            if (Math.Abs(f) < 1e-6)
                return t;

            double df = (f - fPrev) / (t - tPrev);
            if (Math.Abs(df) < 1e-12) break;

            tPrev = t;
            fPrev = f;
            t -= f / df;
        }

        return t;
    }

    public double CalculateDewPointTemperatureKelvin(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> vaporCompositionY,
        double pressureKpa)
    {
        double t = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            t += vaporCompositionY[i] * compounds[i].Antoine.CalculateSaturationTemperatureKelvin(pressureKpa);
        }

        double tPrev = t + 1.0;
        double fPrev = Math.Log(CalculateDewPointPressureKpa(compounds, vaporCompositionY, tPrev) / pressureKpa);

        for (int iter = 0; iter < 30; iter++)
        {
            double f = Math.Log(CalculateDewPointPressureKpa(compounds, vaporCompositionY, t) / pressureKpa);
            if (Math.Abs(f) < 1e-6)
                return t;

            double df = (f - fPrev) / (t - tPrev);
            if (Math.Abs(df) < 1e-12) break;

            tPrev = t;
            fPrev = f;
            t -= f / df;
        }

        return t;
    }

    // -------------------------------------------------------------------------
    // Core Thermodynamic Calculations (PR-EOS Parameters & Fugacity)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes component fugacity coefficients φ_i for mixture at (T, P) in vapor or liquid state.
    /// </summary>
    public double[] CalculateFugacityCoefficients(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> moleFractions,
        double temperatureKelvin,
        double pressureKpa,
        bool isVapor)
    {
        int n = compounds.Count;
        double[] a_pure = new double[n];
        double[] b_pure = new double[n];
        double[] m_pure = new double[n];
        double[] alpha_pure = new double[n];

        for (int i = 0; i < n; i++)
        {
            ChemicalCompound c = compounds[i];
            double tr = temperatureKelvin / c.CriticalTemperatureKelvin;
            double omega = c.AcentricFactor;

            // Peng-Robinson m parameter (standard 1976 or 1978 for heavy components)
            double m = omega <= 0.49
                ? 0.37464 + 1.54226 * omega - 0.26992 * omega * omega
                : 0.379642 + 1.48503 * omega - 0.164423 * omega * omega + 0.016666 * omega * omega * omega;

            double sqrtTr = Math.Sqrt(Math.Max(0.001, tr));
            double alpha = Math.Pow(1.0 + m * (1.0 - sqrtTr), 2.0);

            m_pure[i] = m;
            alpha_pure[i] = alpha;
            a_pure[i] = 0.45724 * Math.Pow(GasConstantR * c.CriticalTemperatureKelvin, 2.0) * alpha / c.CriticalPressureKpa;
            b_pure[i] = 0.07780 * (GasConstantR * c.CriticalTemperatureKelvin) / c.CriticalPressureKpa;
        }

        // Mixture mixing rules (van der Waals with optional BIPs)
        double aMix = 0.0;
        double bMix = 0.0;
        double[] deltaA = new double[n];

        for (int i = 0; i < n; i++)
        {
            bMix += moleFractions[i] * b_pure[i];
            double sumDelta = 0.0;

            for (int j = 0; j < n; j++)
            {
                double kij = _binaryInteractionParameters is not null
                    ? _binaryInteractionParameters[i, j]
                    : _binaryInteractionMatrix.Get(compounds[i].Id, compounds[j].Id);
                double aij = Math.Sqrt(a_pure[i] * a_pure[j]) * (1.0 - kij);
                aMix += moleFractions[i] * moleFractions[j] * aij;
                sumDelta += moleFractions[j] * aij;
            }
            deltaA[i] = sumDelta;
        }

        // Dimensionless EOS parameters A and B
        double rt = GasConstantR * temperatureKelvin;
        double A = (aMix * pressureKpa) / (rt * rt);
        double B = (bMix * pressureKpa) / rt;

        // Solve cubic for compressibility factor Z
        (double zLiquid, double zVapor) = SolveCubicCompressibility(A, B);
        double Z = isVapor ? zVapor : zLiquid;

        // Physical clamp: Z must strictly exceed B
        if (Z <= B)
        {
            Z = B + 1e-5;
        }

        double[] phi = new double[n];
        double logArg = (Z + Sigma1 * B) / (Z + Sigma2 * B);
        double logTerm = logArg > 0.0 ? Math.Log(logArg) : 0.0;

        for (int i = 0; i < n; i++)
        {
            double bi_over_b = b_pure[i] / bMix;
            double term1 = bi_over_b * (Z - 1.0);
            double term2 = Math.Log(Math.Max(1e-12, Z - B));
            double term3 = (A / (2.0 * Sqrt2 * B)) * ((2.0 * deltaA[i] / aMix) - bi_over_b) * logTerm;

            double lnPhi_i = term1 - term2 - term3;
            phi[i] = Math.Exp(Math.Clamp(lnPhi_i, -50.0, 50.0));
        }

        return phi;
    }

    /// <summary>
    /// Computes the molar enthalpy of a phase in J/mol: H = H_ideal(T) + H_departure(T, P).
    /// </summary>
    public double CalculatePhaseEnthalpy(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> moleFractions,
        double temperatureKelvin,
        double pressureKpa,
        bool isVapor)
    {
        int n = compounds.Count;
        double hIdeal = 0.0;

        for (int i = 0; i < n; i++)
        {
            // In EOS thermodynamics, the ideal gas state at T is the shared reference for both phases
            double compH = compounds[i].CalculateVaporEnthalpyJPerMol(temperatureKelvin);
            hIdeal += moleFractions[i] * compH;
        }

        // Compute Peng-Robinson Enthalpy Departure
        double[] a_pure = new double[n];
        double[] b_pure = new double[n];
        double[] da_pure = new double[n];

        for (int i = 0; i < n; i++)
        {
            ChemicalCompound c = compounds[i];
            double tr = temperatureKelvin / c.CriticalTemperatureKelvin;
            double omega = c.AcentricFactor;
            double m = omega <= 0.49
                ? 0.37464 + 1.54226 * omega - 0.26992 * omega * omega
                : 0.379642 + 1.48503 * omega - 0.164423 * omega * omega + 0.016666 * omega * omega * omega;

            double sqrtTr = Math.Sqrt(Math.Max(0.001, tr));
            double alpha = Math.Pow(1.0 + m * (1.0 - sqrtTr), 2.0);
            double dAlphaDt = -m * Math.Sqrt(alpha) / Math.Sqrt(temperatureKelvin * c.CriticalTemperatureKelvin);

            double a0 = 0.45724 * Math.Pow(GasConstantR * c.CriticalTemperatureKelvin, 2.0) / c.CriticalPressureKpa;
            a_pure[i] = a0 * alpha;
            da_pure[i] = a0 * dAlphaDt;
            b_pure[i] = 0.07780 * (GasConstantR * c.CriticalTemperatureKelvin) / c.CriticalPressureKpa;
        }

        double aMix = 0.0;
        double bMix = 0.0;
        double daMixDt = 0.0;

        for (int i = 0; i < n; i++)
        {
            bMix += moleFractions[i] * b_pure[i];
            for (int j = 0; j < n; j++)
            {
                double kij = _binaryInteractionParameters is not null
                    ? _binaryInteractionParameters[i, j]
                    : _binaryInteractionMatrix.Get(compounds[i].Id, compounds[j].Id);
                double aij = Math.Sqrt(a_pure[i] * a_pure[j]) * (1.0 - kij);
                double daijDt = 0.5 * (a_pure[i] * da_pure[j] + a_pure[j] * da_pure[i]) / Math.Sqrt(Math.Max(1e-12, a_pure[i] * a_pure[j])) * (1.0 - kij);

                aMix += moleFractions[i] * moleFractions[j] * aij;
                daMixDt += moleFractions[i] * moleFractions[j] * daijDt;
            }
        }

        double rt = GasConstantR * temperatureKelvin;
        double A = (aMix * pressureKpa) / (rt * rt);
        double B = (bMix * pressureKpa) / rt;

        (double zLiquid, double zVapor) = SolveCubicCompressibility(A, B);
        double Z = isVapor ? zVapor : zLiquid;

        // Departure function: H_dep = R*T*(Z - 1) + [ (T*(da/dT) - a) / (2*√2*b) ] * ln( [Z + σ1*B] / [Z + σ2*B] )
        double logArg = (Z + Sigma1 * B) / (Z + Sigma2 * B);
        double logTerm = logArg > 0.0 ? Math.Log(logArg) : 0.0;
        double hDeparture = rt * (Z - 1.0) + ((temperatureKelvin * daMixDt - aMix) / (2.0 * Sqrt2 * bMix)) * logTerm;

        return hIdeal + hDeparture;
    }

    /// <summary>
    /// Solves the Peng-Robinson cubic equation for compressibility roots (Z_liquid, Z_vapor) via Cardano's formula:
    ///   Z^3 + c2*Z^2 + c1*Z + c0 = 0
    /// </summary>
    public static (double ZLiquid, double ZVapor) SolveCubicCompressibility(double A, double B)
    {
        double c2 = -(1.0 - B);
        double c1 = A - 3.0 * B * B - 2.0 * B;
        double c0 = -(A * B - B * B - B * B * B);

        // Depress to y^3 + p*y + q = 0 via Z = y - c2/3
        double c2Over3 = c2 / 3.0;
        double p = c1 - (c2 * c2) / 3.0;
        double q = c0 - (c1 * c2) / 3.0 + (2.0 * c2 * c2 * c2) / 27.0;

        double discriminant = (q * q) / 4.0 + (p * p * p) / 27.0;

        if (discriminant > 1e-14)
        {
            // One real root
            double sqrtD = Math.Sqrt(discriminant);
            double u = Math.Cbrt(-q / 2.0 + sqrtD);
            double v = Math.Cbrt(-q / 2.0 - sqrtD);
            double z = (u + v) - c2Over3;
            return (z, z);
        }
        else
        {
            // Three real roots (two-phase region)
            double negPOver3 = Math.Max(0.0, -p / 3.0);
            double sqrtNegPOver3 = Math.Sqrt(negPOver3);

            double arg = 0.0;
            double denom = negPOver3 * sqrtNegPOver3;
            if (denom > 1e-15)
            {
                arg = Math.Clamp(-q / (2.0 * denom), -1.0, 1.0);
            }

            double theta = Math.Acos(arg) / 3.0;
            double factor = 2.0 * sqrtNegPOver3;

            double r1 = factor * Math.Cos(theta) - c2Over3;
            double r2 = factor * Math.Cos(theta - 2.0 * Math.PI / 3.0) - c2Over3;
            double r3 = factor * Math.Cos(theta + 2.0 * Math.PI / 3.0) - c2Over3;

            double maxZ = Math.Max(r1, Math.Max(r2, r3));
            double minZ = Math.Min(r1, Math.Min(r2, r3));

            double liquidZ = minZ > B ? minZ : maxZ;
            double vaporZ = maxZ;

            return (liquidZ, vaporZ);
        }
    }

    private static double[] CalculateWilsonKValues(
        IReadOnlyList<ChemicalCompound> compounds,
        double temperatureKelvin,
        double pressureKpa)
    {
        double[] kValues = new double[compounds.Count];
        for (int i = 0; i < compounds.Count; i++)
        {
            ChemicalCompound c = compounds[i];
            double tr = temperatureKelvin / c.CriticalTemperatureKelvin;
            double pr = pressureKpa / c.CriticalPressureKpa;

            // K_i ≈ (P_ci / P) * exp[ 5.373 * (1 + ω_i) * (1 - 1/Tr_i) ]
            double exponent = 5.373 * (1.0 + c.AcentricFactor) * (1.0 - (1.0 / Math.Max(0.1, tr)));
            double k = (1.0 / pr) * Math.Exp(exponent);
            kValues[i] = Math.Clamp(k, 1e-6, 1e6);
        }
        return kValues;
    }

    private double[] CalculatePureFugacityCoefficients(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> moleFractions,
        double temperatureKelvin,
        double pressureKpa,
        bool isVapor) =>
        CalculateFugacityCoefficients(compounds, moleFractions, temperatureKelvin, pressureKpa, isVapor);

    private static double[] Normalize(double[] vector)
    {
        double sum = 0.0;
        for (int i = 0; i < vector.Length; i++) sum += vector[i];
        if (sum <= 0.0) return vector;
        double[] res = new double[vector.Length];
        for (int i = 0; i < vector.Length; i++) res[i] = vector[i] / sum;
        return res;
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        double[] res = new double[a.Length];
        for (int i = 0; i < a.Length; i++) res[i] = a[i] * b[i];
        return res;
    }

    private static double[] Divide(double[] a, double[] b)
    {
        double[] res = new double[a.Length];
        for (int i = 0; i < a.Length; i++) res[i] = a[i] / Math.Max(1e-12, b[i]);
        return res;
    }

    private static void ValidateInputs(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> composition,
        double temperatureKelvin,
        double pressureKpa)
    {
        if (compounds is null || compounds.Count == 0)
            throw new ArgumentException("Compounds list cannot be empty.", nameof(compounds));
        if (composition is null || composition.Count != compounds.Count)
            throw new ArgumentException("Composition length must match compounds count.", nameof(composition));
        if (temperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(temperatureKelvin), "Temperature must be > 0 K.");
        if (pressureKpa <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(pressureKpa), "Pressure must be > 0 kPa.");

        double sum = 0.0;
        for (int i = 0; i < composition.Count; i++)
        {
            if (composition[i] < 0.0)
                throw new ArgumentOutOfRangeException(nameof(composition), "Mole fractions cannot be negative.");
            sum += composition[i];
        }

        if (Math.Abs(sum - 1.0) > 1e-3)
            throw new ArgumentException($"Mole fractions must sum to 1.0 (actual sum = {sum:F6}).", nameof(composition));
    }
}
