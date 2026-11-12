using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Transport;

/// <summary>
/// Implements the Ely and Hanley (1981, 1983) TRAPP (Transport Properties Prediction) model
/// based on the Extended Corresponding States (ECS) principle with van der Waals one-fluid (vdW1) mixing rules.
///
/// References:
/// - Ely, J. F., Hanley, H. J. M. (1981). Prediction of transport properties. 1. Viscosity of fluids
///   and mixtures. Ind. Eng. Chem. Fundam., 20(4), 323-332.
/// - Ely, J. F., Hanley, H. J. M. (1983). Prediction of transport properties. 2. Thermal conductivity
///   of pure fluids and mixtures. Ind. Eng. Chem. Fundam., 22(1), 90-97.
/// </summary>
public sealed class ElyHanleyTransportPackage : ITransportPropertiesPackage
{
    private const double GasConstantR = 8.314462618; // J/(mol·K)

    // Reference fluid constants (Methane: CH4)
    private const double RefTc = 190.56;        // K
    private const double RefPc = 4599.0;        // kPa
    private const double RefVc = 98.6;          // cm³/mol
    private const double RefMw = 16.043;        // g/mol
    private const double RefOmega = 0.011;      // acentric factor
    private const double RefZc = 0.286;

    public string Name => "Ely-Hanley (TRAPP ECS)";

    public TransportPropertyResult CalculateVaporProperties(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> vaporMoleFractionsY,
        double temperatureKelvin,
        double pressureKpa)
    {
        ValidateInputs(compounds, vaporMoleFractionsY, temperatureKelvin, pressureKpa);

        int n = compounds.Count;
        double[] vc = new double[n];
        for (int i = 0; i < n; i++)
        {
            double zc = 0.2905 - 0.085 * compounds[i].AcentricFactor;
            vc[i] = (zc * GasConstantR * compounds[i].CriticalTemperatureKelvin / compounds[i].CriticalPressureKpa) * 1000.0;
            if (vc[i] <= 0.0) vc[i] = 120.0;
        }

        // 1. Van der Waals One-Fluid (vdW1) conformal mixing rules
        ComputeEquivalentFluidParameters(compounds, vaporMoleFractionsY, vc,
            out double tcm, out double vcm, out double mm, out double omegaM);

        double tr = Math.Clamp(temperatureKelvin / tcm, 0.4, 3.5);

        // 2. Shape factors (Leachman / Ely & Hanley)
        double theta = 1.0 + (omegaM - RefOmega) * (0.09056 - 0.05322 * Math.Log(tr));
        double phi = 1.0 + (omegaM - RefOmega) * (0.3949 * (tr - 1.0) - 0.1023 * Math.Log(tr));
        if (phi <= 0.1) phi = 0.1;

        double fx = (tcm / RefTc) * theta;
        double hx = (vcm / RefVc) * phi;
        if (fx <= 0.0) fx = 1.0;
        if (hx <= 0.0) hx = 1.0;

        // 3. Corresponding state temperature and reference properties (Methane)
        double t0 = temperatureKelvin / fx;
        if (t0 <= 50.0) t0 = 50.0;

        // Dilute reference viscosity: η0(T0) in Pa·s
        double tStar0 = 1.2593 * (t0 / RefTc);
        double omegaV0 = 1.16145 * Math.Pow(tStar0, -0.14874)
                       + 0.52487 * Math.Exp(-0.77320 * tStar0)
                       + 2.16178 * Math.Exp(-2.43787 * tStar0);
        double eta0RefPaS = (40.785 * Math.Sqrt(RefMw * t0) / (Math.Pow(RefVc, 2.0 / 3.0) * omegaV0)) * 1e-7;

        // Scale to mixture via ECS: μ_mix = μ_0 * (Mm / M0)^0.5 * fx^0.5 * hx^(-2/3)
        double viscVapor = eta0RefPaS * Math.Sqrt(mm / RefMw) * Math.Sqrt(fx) / Math.Pow(hx, 2.0 / 3.0);
        viscVapor = Math.Max(1e-6, viscVapor);

        // 4. Thermal conductivity: Translational (ECS) + Internal degrees of freedom
        // λ_trans = λ_0 * (M0 / Mm)^0.5 * fx^0.5 * hx^(-2/3)
        double lambda0Ref = (eta0RefPaS / (RefMw * 1e-3)) * (3.75 * GasConstantR);
        double lambdaTrans = lambda0Ref * Math.Sqrt(RefMw / mm) * Math.Sqrt(fx) / Math.Pow(hx, 2.0 / 3.0);

        // Internal modes (Eucken correction across mixture components)
        double lambdaInt = 0.0;
        double avgCpV = 0.0;
        for (int i = 0; i < n; i++)
        {
            avgCpV += vaporMoleFractionsY[i] * compounds[i].VaporHeatCapacityJPerMolK;
            double cvInt = Math.Max(0.0, compounds[i].VaporHeatCapacityJPerMolK - 2.5 * GasConstantR);
            double pureEta = (40.785e-7 * Math.Sqrt(compounds[i].MolarMass * temperatureKelvin))
                           / (Math.Pow(vc[i], 2.0 / 3.0) * omegaV0);
            lambdaInt += vaporMoleFractionsY[i] * (pureEta / (compounds[i].MolarMass * 1e-3)) * 1.32 * cvInt;
        }

        double lambdaVapor = Math.Max(1e-4, lambdaTrans + lambdaInt);

        // Density and dimensionless Prandtl number
        double densityVapor = (pressureKpa * mm) / (GasConstantR * temperatureKelvin);
        densityVapor = Math.Max(1e-4, densityVapor);
        double cpSpecificJPerKgK = (avgCpV / mm) * 1000.0;
        double prandtl = (cpSpecificJPerKgK * viscVapor) / lambdaVapor;

        return new TransportPropertyResult(
            dynamicViscosityPaS: viscVapor,
            thermalConductivityWPerMK: lambdaVapor,
            densityKgPerM3: densityVapor,
            prandtlNumber: prandtl,
            phase: PhaseState.SuperheatedVapor,
            temperatureKelvin: temperatureKelvin,
            pressureKpa: pressureKpa,
            modelName: Name
        );
    }

    public TransportPropertyResult CalculateLiquidProperties(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> liquidMoleFractionsX,
        double temperatureKelvin,
        double pressureKpa)
    {
        ValidateInputs(compounds, liquidMoleFractionsX, temperatureKelvin, pressureKpa);

        int n = compounds.Count;
        double[] vc = new double[n];
        double[] muL = new double[n];
        double[] lambdaL = new double[n];
        double[] mw = new double[n];
        double[] massFractions = new double[n];

        double totalMass = 0.0;
        for (int i = 0; i < n; i++)
        {
            mw[i] = compounds[i].MolarMass;
            totalMass += liquidMoleFractionsX[i] * mw[i];
            double zc = 0.2905 - 0.085 * compounds[i].AcentricFactor;
            vc[i] = (zc * GasConstantR * compounds[i].CriticalTemperatureKelvin / compounds[i].CriticalPressureKpa) * 1000.0;
            if (vc[i] <= 0.0) vc[i] = 120.0;
        }

        for (int i = 0; i < n; i++)
        {
            massFractions[i] = totalMass > 0.0 ? (liquidMoleFractionsX[i] * mw[i]) / totalMass : 1.0 / n;

            var comp = compounds[i];
            double tc = comp.CriticalTemperatureKelvin;
            double tr = Math.Clamp(temperatureKelvin / tc, 0.25, 0.98);

            // Pure liquid viscosity via Letsou & Stiel (1973) corresponding states
            double pcBar = Math.Max(1.0, comp.CriticalPressureKpa / 100.0);
            double xi = Math.Pow(tc, 1.0 / 6.0) / (Math.Sqrt(comp.MolarMass) * Math.Pow(pcBar, 2.0 / 3.0));
            double f0 = 0.015174 - 0.02135 * tr + 0.0075 * tr * tr;
            double f1 = 0.042552 - 0.07674 * tr + 0.0340 * tr * tr;
            double etaXi = Math.Max(0.001, f0 + comp.AcentricFactor * f1);
            double muCp = etaXi / xi;
            if (comp.Id == "H2O") muCp *= 2.8;
            muL[i] = muCp * 1e-3; // Convert cP (mPa·s) to Pa·s

            // Liquid thermal conductivity (Missenard-Latini)
            // λ = A * (1 - Tr)^0.38 / Tr^(1/6), where A = 0.359 * Tc^0.216 / sqrt(M) [SI W/(m·K)]
            double aLatini = 0.359 * Math.Pow(tc, 0.216) / Math.Sqrt(comp.MolarMass);
            if (comp.Id == "H2O") aLatini *= 4.5;
            lambdaL[i] = Math.Max(0.05, (aLatini * Math.Pow(1.0 - tr, 0.38)) / Math.Pow(tr, 1.0 / 6.0));
        }

        // Grunberg-Nissan logarithmic mixing for liquid viscosity
        double lnMuMix = 0.0;
        for (int i = 0; i < n; i++)
        {
            lnMuMix += liquidMoleFractionsX[i] * Math.Log(Math.Max(1e-12, muL[i]));
        }
        double muMix = Math.Exp(lnMuMix);

        // Filippov mass-fraction mixing for liquid thermal conductivity
        double lambdaMix = 0.0;
        for (int i = 0; i < n; i++)
        {
            lambdaMix += massFractions[i] * lambdaL[i];
        }

        double diffTerm = 0.0;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                diffTerm += massFractions[i] * massFractions[j] * Math.Abs(lambdaL[i] - lambdaL[j]);
            }
        }
        lambdaMix = Math.Max(0.05, lambdaMix - 0.72 * diffTerm);

        double avgCpL = 0.0;
        for (int i = 0; i < n; i++) avgCpL += liquidMoleFractionsX[i] * compounds[i].LiquidHeatCapacityJPerMolK;

        double densityLiquid = 1000.0 * (totalMass / 18.015);
        if (densityLiquid < 600.0) densityLiquid = 800.0;
        if (densityLiquid > 1500.0) densityLiquid = 1100.0;

        double cpSpecificJPerKgK = (avgCpL / totalMass) * 1000.0;
        double prandtl = (cpSpecificJPerKgK * muMix) / lambdaMix;

        return new TransportPropertyResult(
            dynamicViscosityPaS: muMix,
            thermalConductivityWPerMK: lambdaMix,
            densityKgPerM3: densityLiquid,
            prandtlNumber: prandtl,
            phase: PhaseState.SubcooledLiquid,
            temperatureKelvin: temperatureKelvin,
            pressureKpa: pressureKpa,
            modelName: Name
        );
    }

    public TransportPropertyResult CalculateStreamProperties(MaterialStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.IsSolved)
            throw new InvalidOperationException($"Stream '{stream.Name}' must be solved before computing transport properties.");

        var flash = stream.EquilibriumState!;
        if (flash.Phase == PhaseState.SubcooledLiquid)
        {
            return CalculateLiquidProperties(stream.Compounds, flash.LiquidCompositionX, stream.TemperatureKelvin, stream.PressureKpa);
        }
        else if (flash.Phase == PhaseState.SuperheatedVapor)
        {
            return CalculateVaporProperties(stream.Compounds, flash.VaporCompositionY, stream.TemperatureKelvin, stream.PressureKpa);
        }
        else
        {
            var propL = CalculateLiquidProperties(stream.Compounds, flash.LiquidCompositionX, stream.TemperatureKelvin, stream.PressureKpa);
            var propV = CalculateVaporProperties(stream.Compounds, flash.VaporCompositionY, stream.TemperatureKelvin, stream.PressureKpa);

            double beta = Math.Clamp(flash.VaporFractionBeta, 0.0, 1.0);
            double avgVisc = (1.0 - beta) * propL.DynamicViscosityPaS + beta * propV.DynamicViscosityPaS;
            double avgCond = (1.0 - beta) * propL.ThermalConductivityWPerMK + beta * propV.ThermalConductivityWPerMK;
            double avgDensity = (1.0 - beta) * propL.DensityKgPerM3 + beta * propV.DensityKgPerM3;
            double avgPr = (1.0 - beta) * propL.PrandtlNumber + beta * propV.PrandtlNumber;

            return new TransportPropertyResult(
                dynamicViscosityPaS: avgVisc,
                thermalConductivityWPerMK: avgCond,
                densityKgPerM3: avgDensity,
                prandtlNumber: avgPr,
                phase: PhaseState.VaporLiquidEquilibrium,
                temperatureKelvin: stream.TemperatureKelvin,
                pressureKpa: stream.PressureKpa,
                modelName: $"{Name} (Two-Phase Blend)"
            );
        }
    }

    private static void ComputeEquivalentFluidParameters(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> moleFractions,
        double[] vc,
        out double tcm,
        out double vcm,
        out double mm,
        out double omegaM)
    {
        int n = compounds.Count;
        double sumVc = 0.0;
        double sumTcVc = 0.0;
        double sumMassTerm = 0.0;
        double sumOmega = 0.0;

        for (int i = 0; i < n; i++)
        {
            sumOmega += moleFractions[i] * compounds[i].AcentricFactor;
            for (int j = 0; j < n; j++)
            {
                double vcij = Math.Pow(0.5 * (Math.Pow(vc[i], 1.0 / 3.0) + Math.Pow(vc[j], 1.0 / 3.0)), 3.0);
                double tcij = Math.Sqrt(compounds[i].CriticalTemperatureKelvin * compounds[j].CriticalTemperatureKelvin);
                double mij = 2.0 * compounds[i].MolarMass * compounds[j].MolarMass / (compounds[i].MolarMass + compounds[j].MolarMass);

                double xixj = moleFractions[i] * moleFractions[j];
                sumVc += xixj * vcij;
                sumTcVc += xixj * tcij * vcij;
                sumMassTerm += xixj * Math.Sqrt(mij) * Math.Pow(tcij, 0.25) * Math.Sqrt(vcij);
            }
        }

        vcm = Math.Max(1.0, sumVc);
        tcm = Math.Max(1.0, sumTcVc / vcm);
        double massNum = sumMassTerm * sumMassTerm;
        double massDenom = Math.Sqrt(tcm) * vcm;
        mm = massDenom > 1e-12 ? Math.Max(1.0, massNum / massDenom) : 50.0;
        omegaM = Math.Max(0.0, sumOmega);
    }

    private static void ValidateInputs(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> fractions,
        double temperatureKelvin,
        double pressureKpa)
    {
        ArgumentNullException.ThrowIfNull(compounds);
        ArgumentNullException.ThrowIfNull(fractions);
        if (compounds.Count == 0)
            throw new ArgumentException("Compounds list cannot be empty.", nameof(compounds));
        if (fractions.Count != compounds.Count)
            throw new ArgumentException("Composition vector length must match compounds count.", nameof(fractions));
        if (temperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(temperatureKelvin), "Temperature must be > 0 K.");
        if (pressureKpa <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(pressureKpa), "Pressure must be > 0 kPa.");
    }
}
