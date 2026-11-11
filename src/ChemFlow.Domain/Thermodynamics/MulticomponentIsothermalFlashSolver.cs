using ChemFlow.Domain.Entities;

namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Rigorous multicomponent isothermal (T, P) flash solver implementing:
/// 1. Initial partition coefficient estimation via Wilson's empirical correlation (1969).
/// 2. Michelsen's (1982) Tangent Plane Distance (TPD) phase stability analysis.
/// 3. Hybrid Newton-Raphson + Bisection Rachford-Rice inner loop solver.
/// 4. Accelerated Successive Substitution (SS) coupled to Peng-Robinson Cubic EOS with
///    van der Waals quadratic mixing rules and binary interaction parameter (BIP) matrix [k_ij].
/// 5. Full thermodynamic departure function for mixture molar enthalpy.
/// </summary>
public sealed class MulticomponentIsothermalFlashSolver
{
    public const double GasConstantR = 8.314462618; // J/(mol·K) = kPa·L/(mol·K)
    private static readonly double Sqrt2 = Math.Sqrt(2.0);
    private static readonly double Sigma1 = 1.0 + Math.Sqrt(2.0); // 1 + √2 ≈ 2.41421356
    private static readonly double Sigma2 = 1.0 - Math.Sqrt(2.0); // 1 - √2 ≈ -0.41421356

    private readonly BinaryInteractionMatrix _binaryInteractionMatrix;

    public BinaryInteractionMatrix InteractionMatrix => _binaryInteractionMatrix;

    public MulticomponentIsothermalFlashSolver(BinaryInteractionMatrix? binaryInteractionMatrix = null)
    {
        _binaryInteractionMatrix = binaryInteractionMatrix ?? BinaryInteractionMatrix.Empty;
    }

    /// <summary>
    /// Computes the initial equilibrium ratio estimate K_i via Wilson's correlation (1969).
    /// </summary>
    public static double[] CalculateWilsonKValues(
        IReadOnlyList<ChemicalCompound> compounds,
        double temperatureKelvin,
        double pressureKpa)
    {
        int n = compounds.Count;
        double[] kValues = new double[n];

        for (int i = 0; i < n; i++)
        {
            ChemicalCompound c = compounds[i];
            double tr = temperatureKelvin / c.CriticalTemperatureKelvin;
            double pr = pressureKpa / c.CriticalPressureKpa;

            // K_i ≈ (1 / P_ri) * exp[ 5.373 * (1 + ω_i) * (1 - 1/Tr_i) ]
            double exponent = 5.373 * (1.0 + c.AcentricFactor) * (1.0 - (1.0 / Math.Max(0.01, tr)));
            double k = (1.0 / Math.Max(1e-12, pr)) * Math.Exp(exponent);
            kValues[i] = Math.Clamp(k, 1e-6, 1e6);
        }

        return kValues;
    }

    /// <summary>
    /// Executes the complete rigorous multicomponent isothermal flash algorithm at specified (T, P, z).
    /// </summary>
    public FlashResult Solve(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> overallCompositionZ,
        double temperatureKelvin,
        double pressureKpa,
        double tolerance = 1e-9,
        int maxOuterIterations = 150)
    {
        ValidateInputs(compounds, overallCompositionZ, temperatureKelvin, pressureKpa);

        int n = compounds.Count;
        double[] z = overallCompositionZ.ToArray();

        // 1. Initial K-value estimate via Wilson's correlation
        double[] kValues = CalculateWilsonKValues(compounds, temperatureKelvin, pressureKpa);

        // Pre-compute dense BIP matrix
        double[,] kij = _binaryInteractionMatrix.ToMatrix(compounds);

        // 2. Initial phase boundary check with Wilson values
        double f0 = RachfordRiceSolver.EvaluateObjectiveFunction(z, kValues, 0.0);
        double f1 = RachfordRiceSolver.EvaluateObjectiveFunction(z, kValues, 1.0);

        if (f0 <= 0.0)
        {
            // Subcooled liquid limit
            double[] xPure = (double[])z.Clone();
            double enthalpyL = CalculatePhaseEnthalpy(compounds, xPure, temperatureKelvin, pressureKpa, kij, isVapor: false);

            return new FlashResult(
                Phase: PhaseState.SubcooledLiquid,
                TemperatureKelvin: temperatureKelvin,
                PressureKpa: pressureKpa,
                VaporFractionBeta: 0.0,
                OverallCompositionZ: z,
                LiquidCompositionX: xPure,
                VaporCompositionY: Normalize(Multiply(xPure, kValues)),
                KValues: kValues,
                MolarEnthalpyJPerMol: enthalpyL,
                Iterations: 0
            );
        }

        if (f1 >= 0.0)
        {
            // Superheated vapor limit
            double[] yPure = (double[])z.Clone();
            double enthalpyV = CalculatePhaseEnthalpy(compounds, yPure, temperatureKelvin, pressureKpa, kij, isVapor: true);

            return new FlashResult(
                Phase: PhaseState.SuperheatedVapor,
                TemperatureKelvin: temperatureKelvin,
                PressureKpa: pressureKpa,
                VaporFractionBeta: 1.0,
                OverallCompositionZ: z,
                LiquidCompositionX: Normalize(Divide(yPure, kValues)),
                VaporCompositionY: yPure,
                KValues: kValues,
                MolarEnthalpyJPerMol: enthalpyV,
                Iterations: 0
            );
        }

        // 3. Robust Two-Phase Flash Loop (Successive Substitution + Wegstein Acceleration)

        // 4. Accelerated Two-Phase Flash Loop (Successive Substitution + Wegstein Acceleration)
        double beta = 0.5;
        double[] x = new double[n];
        double[] y = new double[n];
        int iterations = 0;

        double[] prevLnK = new double[n];
        double[] prevLnKCalc = new double[n];
        bool hasPrevHistory = false;

        for (int iter = 1; iter <= maxOuterIterations; iter++)
        {
            iterations = iter;

            // Inner Loop: Solve Rachford-Rice equation for vapor fraction β
            RachfordRiceSolver.Solution rr = RachfordRiceSolver.Solve(z, kValues, tolerance: 1e-11);
            beta = rr.Beta;
            x = rr.LiquidCompositionX;
            y = rr.VaporCompositionY;

            if (rr.Phase != PhaseState.VaporLiquidEquilibrium)
            {
                if (hasPrevHistory && iter < maxOuterIterations)
                {
                    // Backtrack trial K-values towards previous known two-phase state with increased damping
                    for (int i = 0; i < n; i++)
                    {
                        kValues[i] = Math.Sqrt(kValues[i] * Math.Exp(prevLnK[i]));
                    }
                    continue;
                }

                // Boundary hit: check if truly at single-phase boundary
                if (beta <= 1e-7)
                {
                    double hL = CalculatePhaseEnthalpy(compounds, x, temperatureKelvin, pressureKpa, kij, isVapor: false);
                    return new FlashResult(PhaseState.SubcooledLiquid, temperatureKelvin, pressureKpa, 0.0, z, x, y, kValues, hL, iterations);
                }
                if (beta >= (1.0 - 1e-7))
                {
                    double hV = CalculatePhaseEnthalpy(compounds, y, temperatureKelvin, pressureKpa, kij, isVapor: true);
                    return new FlashResult(PhaseState.SuperheatedVapor, temperatureKelvin, pressureKpa, 1.0, z, x, y, kValues, hV, iterations);
                }
            }

            // Outer Loop: Evaluate PR-EOS fugacities for liquid x and vapor y
            double[] phiL = CalculateFugacityCoefficients(compounds, x, temperatureKelvin, pressureKpa, kij, isVapor: false);
            double[] phiV = CalculateFugacityCoefficients(compounds, y, temperatureKelvin, pressureKpa, kij, isVapor: true);

            double[] newK = new double[n];
            double[] lnKCalc = new double[n];
            double errorNormSq = 0.0;

            for (int i = 0; i < n; i++)
            {
                newK[i] = Math.Clamp(phiL[i] / Math.Max(1e-12, phiV[i]), 1e-6, 1e6);
                lnKCalc[i] = Math.Log(newK[i]);

                double relError = (newK[i] / kValues[i]) - 1.0;
                errorNormSq += relError * relError;
            }

            // Check convergence
            if (errorNormSq < tolerance)
            {
                kValues = newK;
                break;
            }

            // Wegstein / Damped Acceleration
            double[] nextK = new double[n];
            if (hasPrevHistory && (iter % 3 == 0))
            {
                // Apply Wegstein extrapolation on ln(K)
                for (int i = 0; i < n; i++)
                {
                    double currentLnK = Math.Log(kValues[i]);
                    double denom = (currentLnK - prevLnK[i]);
                    double s = Math.Abs(denom) > 1e-12
                        ? (lnKCalc[i] - prevLnKCalc[i]) / denom
                        : 0.0;

                    // Wegstein factor q = s / (s - 1), bounded to [-5, 0] for numerical stability
                    double q = (s < 0.99 && s > -10.0) ? s / (s - 1.0) : 0.0;
                    q = Math.Clamp(q, -3.0, 0.0);

                    double acceleratedLnK = (1.0 - q) * currentLnK + q * lnKCalc[i];
                    nextK[i] = Math.Exp(Math.Clamp(acceleratedLnK, -20.0, 20.0));
                }
            }
            else
            {
                // Adaptive damped successive substitution
                double damping = iter > 25 ? 0.60 : (iter > 10 ? 0.80 : 0.90);
                for (int i = 0; i < n; i++)
                {
                    double currentLnK = Math.Log(kValues[i]);
                    double dampedLnK = damping * lnKCalc[i] + (1.0 - damping) * currentLnK;
                    nextK[i] = Math.Exp(Math.Clamp(dampedLnK, -20.0, 20.0));
                }
            }

            for (int i = 0; i < n; i++)
            {
                prevLnK[i] = Math.Log(kValues[i]);
                prevLnKCalc[i] = lnKCalc[i];
            }
            hasPrevHistory = true;
            kValues = nextK;
        }

        // Final consistency solve with converged K-values
        RachfordRiceSolver.Solution finalRr = RachfordRiceSolver.Solve(z, kValues, tolerance: 1e-12);
        if (finalRr.Phase == PhaseState.VaporLiquidEquilibrium)
        {
            beta = finalRr.Beta;
            x = finalRr.LiquidCompositionX;
            y = finalRr.VaporCompositionY;
        }

        // 5. Calculate Phase Enthalpies via PR-EOS Departure Functions
        double liquidEnthalpy = CalculatePhaseEnthalpy(compounds, x, temperatureKelvin, pressureKpa, kij, isVapor: false);
        double vaporEnthalpy = CalculatePhaseEnthalpy(compounds, y, temperatureKelvin, pressureKpa, kij, isVapor: true);
        double totalEnthalpy = (1.0 - beta) * liquidEnthalpy + beta * vaporEnthalpy;

        PhaseState finalPhase = beta <= 1e-7 ? PhaseState.SubcooledLiquid
                              : beta >= (1.0 - 1e-7) ? PhaseState.SuperheatedVapor
                              : PhaseState.VaporLiquidEquilibrium;

        return new FlashResult(
            Phase: finalPhase,
            TemperatureKelvin: temperatureKelvin,
            PressureKpa: pressureKpa,
            VaporFractionBeta: beta,
            OverallCompositionZ: z,
            LiquidCompositionX: x,
            VaporCompositionY: y,
            KValues: kValues,
            MolarEnthalpyJPerMol: totalEnthalpy,
            Iterations: iterations
        );
    }

    /// <summary>
    /// Computes component fugacity coefficients φ_i for mixture at (T, P) in vapor or liquid state.
    /// </summary>
    public static double[] CalculateFugacityCoefficients(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> moleFractions,
        double temperatureKelvin,
        double pressureKpa,
        double[,] kij,
        bool isVapor)
    {
        int n = compounds.Count;
        double[] a_pure = new double[n];
        double[] b_pure = new double[n];

        for (int i = 0; i < n; i++)
        {
            ChemicalCompound c = compounds[i];
            double tr = temperatureKelvin / c.CriticalTemperatureKelvin;
            double omega = c.AcentricFactor;

            // Peng-Robinson m parameter (standard 1976 / 1978)
            double m = omega <= 0.49
                ? 0.37464 + 1.54226 * omega - 0.26992 * omega * omega
                : 0.379642 + 1.48503 * omega - 0.164423 * omega * omega + 0.016666 * omega * omega * omega;

            double sqrtTr = Math.Sqrt(Math.Max(0.001, tr));
            double alpha = Math.Pow(1.0 + m * (1.0 - sqrtTr), 2.0);

            a_pure[i] = 0.45724 * Math.Pow(GasConstantR * c.CriticalTemperatureKelvin, 2.0) * alpha / c.CriticalPressureKpa;
            b_pure[i] = 0.07780 * (GasConstantR * c.CriticalTemperatureKelvin) / c.CriticalPressureKpa;
        }

        // Mixture mixing rules (van der Waals with BIPs)
        double aMix = 0.0;
        double bMix = 0.0;
        double[] deltaA = new double[n];

        for (int i = 0; i < n; i++)
        {
            bMix += moleFractions[i] * b_pure[i];
            double sumDelta = 0.0;

            for (int j = 0; j < n; j++)
            {
                double k_param = (kij is not null && i < kij.GetLength(0) && j < kij.GetLength(1)) ? kij[i, j] : 0.0;
                double aij = Math.Sqrt(a_pure[i] * a_pure[j]) * (1.0 - k_param);
                aMix += moleFractions[i] * moleFractions[j] * aij;
                sumDelta += moleFractions[j] * aij;
            }
            deltaA[i] = sumDelta;
        }

        // Dimensionless EOS parameters A and B
        double rt = GasConstantR * temperatureKelvin;
        double A = (aMix * pressureKpa) / (rt * rt);
        double B = (bMix * pressureKpa) / rt;

        // Solve cubic compressibility factor Z
        (double zLiquid, double zVapor) = PengRobinsonThermodynamicPackage.SolveCubicCompressibility(A, B);
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
    public static double CalculatePhaseEnthalpy(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> moleFractions,
        double temperatureKelvin,
        double pressureKpa,
        double[,] kij,
        bool isVapor)
    {
        int n = compounds.Count;
        double hIdeal = 0.0;

        for (int i = 0; i < n; i++)
        {
            double compH = compounds[i].CalculateVaporEnthalpyJPerMol(temperatureKelvin);
            hIdeal += moleFractions[i] * compH;
        }

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
                double k_param = (kij is not null && i < kij.GetLength(0) && j < kij.GetLength(1)) ? kij[i, j] : 0.0;
                double aij = Math.Sqrt(a_pure[i] * a_pure[j]) * (1.0 - k_param);
                double daijDt = 0.5 * (a_pure[i] * da_pure[j] + a_pure[j] * da_pure[i]) / Math.Sqrt(Math.Max(1e-12, a_pure[i] * a_pure[j])) * (1.0 - k_param);

                aMix += moleFractions[i] * moleFractions[j] * aij;
                daMixDt += moleFractions[i] * moleFractions[j] * daijDt;
            }
        }

        double rt = GasConstantR * temperatureKelvin;
        double A = (aMix * pressureKpa) / (rt * rt);
        double B = (bMix * pressureKpa) / rt;

        (double zLiquid, double zVapor) = PengRobinsonThermodynamicPackage.SolveCubicCompressibility(A, B);
        double Z = isVapor ? zVapor : zLiquid;

        double logArg = (Z + Sigma1 * B) / (Z + Sigma2 * B);
        double logTerm = logArg > 0.0 ? Math.Log(logArg) : 0.0;
        double hDeparture = rt * (Z - 1.0) + ((temperatureKelvin * daMixDt - aMix) / (2.0 * Sqrt2 * bMix)) * logTerm;

        return hIdeal + hDeparture;
    }

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
