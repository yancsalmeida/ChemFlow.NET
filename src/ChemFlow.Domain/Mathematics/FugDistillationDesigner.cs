using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Mathematics;

/// <summary>
/// Correlation formulation used to solve the Gilliland empirical curve:
/// relating actual stages N to minimum stages N_min and reflux ratio R / R_min.
/// </summary>
public enum GillilandCorrelationType
{
    /// <summary>
    /// Molokanov (1972) analytical approximation (smooth, highly accurate across 0.01 &lt;= X &lt;= 0.90).
    /// </summary>
    Molokanov,

    /// <summary>
    /// Eduljee (1975) power-law formulation: Y = 0.75 * (1 - X^0.5668).
    /// </summary>
    Eduljee
}

/// <summary>
/// Output results from the Fenske-Underwood-Gilliland-Kirkbride (FUG) shortcut distillation design method.
/// </summary>
public sealed record FugDesignResult(
    double MinimumStagesNmin,
    double MinimumRefluxRatioRmin,
    double OperatingRefluxRatio,
    double TheoreticalStagesN,
    int RecommendedNumberOfStages,
    int OptimalFeedStage,
    double RectifyingStagesNr,
    double StrippingStagesNs,
    double UnderwoodTheta,
    double DistillateRateMolPerSec,
    double BottomsRateMolPerSec,
    IReadOnlyList<double> DistillateCompositionX,
    IReadOnlyList<double> BottomsCompositionX,
    IReadOnlyList<double> RelativeVolatilities,
    double TopTemperatureKelvin,
    double BottomTemperatureKelvin,
    double CondenserDutyWatts,
    double ReboilerDutyWatts,
    double EstimatedDiameterMeters
);

/// <summary>
/// Mathematical engine for the Fenske-Underwood-Gilliland-Kirkbride (FUG) shortcut design
/// of multicomponent distillation columns.
/// </summary>
public static class FugDistillationDesigner
{
    private const double GasConstant = 8.314462618; // J/(mol*K)

    /// <summary>
    /// Computes the complete Fenske-Underwood-Gilliland-Kirkbride design for a multicomponent feed stream.
    /// </summary>
    /// <param name="compounds">List of chemical compounds in the mixture.</param>
    /// <param name="zFeed">Feed mole fractions (sums to 1.0).</param>
    /// <param name="totalFeedFlow">Total feed molar flow rate in mol/s.</param>
    /// <param name="feedTemperatureK">Feed temperature in Kelvin.</param>
    /// <param name="columnPressureKpa">Operating column pressure in kPa.</param>
    /// <param name="feedVaporFraction">Vapor fraction of feed beta in [0, 1].</param>
    /// <param name="lightKeyIndex">0-indexed position of Light Key (LK).</param>
    /// <param name="heavyKeyIndex">0-indexed position of Heavy Key (HK).</param>
    /// <param name="lightKeyRecoveryDistillate">Fractional recovery of LK in distillate (e.g. 0.98).</param>
    /// <param name="heavyKeyRecoveryBottoms">Fractional recovery of HK in bottoms (e.g. 0.98).</param>
    /// <param name="refluxRatioMultiplier">Multiplier over R_min (e.g. 1.25), or null if fixedReflux is provided.</param>
    /// <param name="fixedRefluxRatio">Optional explicit operating reflux ratio R (must exceed R_min).</param>
    /// <param name="thermo">Thermodynamic package to evaluate bubble points and K-values.</param>
    /// <param name="correlation">Gilliland curve correlation choice (default Molokanov).</param>
    public static FugDesignResult Design(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> zFeed,
        double totalFeedFlow,
        double feedTemperatureK,
        double columnPressureKpa,
        double feedVaporFraction,
        int lightKeyIndex,
        int heavyKeyIndex,
        double lightKeyRecoveryDistillate = 0.98,
        double heavyKeyRecoveryBottoms = 0.98,
        double? refluxRatioMultiplier = 1.25,
        double? fixedRefluxRatio = null,
        IThermodynamicPackage? thermo = null,
        GillilandCorrelationType correlation = GillilandCorrelationType.Molokanov)
    {
        int nComp = compounds.Count;
        if (nComp < 2)
            throw new ArgumentException("Distillation requires at least 2 chemical compounds.", nameof(compounds));
        if (zFeed.Count != nComp)
            throw new ArgumentException("Feed composition length must match compound count.", nameof(zFeed));
        if (lightKeyIndex < 0 || lightKeyIndex >= nComp)
            throw new ArgumentOutOfRangeException(nameof(lightKeyIndex), "Light key index out of range.");
        if (heavyKeyIndex < 0 || heavyKeyIndex >= nComp)
            throw new ArgumentOutOfRangeException(nameof(heavyKeyIndex), "Heavy key index out of range.");
        if (lightKeyIndex == heavyKeyIndex)
            throw new ArgumentException("Light key and Heavy key cannot be the same compound.");
        if (lightKeyRecoveryDistillate <= 0.0 || lightKeyRecoveryDistillate >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(lightKeyRecoveryDistillate), "LK recovery in distillate must be in (0, 1).");
        if (heavyKeyRecoveryBottoms <= 0.0 || heavyKeyRecoveryBottoms >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(heavyKeyRecoveryBottoms), "HK recovery in bottoms must be in (0, 1).");

        thermo ??= new RaoultThermodynamicPackage();

        // 1. Calculate relative volatilities alpha_i relative to Heavy Key (HK)
        // Evaluate K-values at feed condition (T_feed, P_col)
        double[] kVals = thermo.CalculateKValues(compounds, zFeed, feedTemperatureK, columnPressureKpa);
        double kHK = Math.Max(1e-12, kVals[heavyKeyIndex]);
        double[] alpha = new double[nComp];
        for (int i = 0; i < nComp; i++)
        {
            alpha[i] = Math.Max(1e-6, kVals[i] / kHK);
        }

        double alphaLK = alpha[lightKeyIndex];
        if (alphaLK <= 1.0001)
        {
            throw new InvalidOperationException(
                $"Light key '{compounds[lightKeyIndex].Name}' (alpha={alphaLK:F4}) must have strictly higher volatility than Heavy key '{compounds[heavyKeyIndex].Name}' (alpha=1.0).");
        }

        // 2. Fenske Equation: Minimum Theoretical Stages N_min at total reflux
        // d_LK / b_LK = r_LK / (1 - r_LK)
        // b_HK / d_HK = r_HK / (1 - r_HK)
        double ratioLK = lightKeyRecoveryDistillate / (1.0 - lightKeyRecoveryDistillate);
        double ratioHK = heavyKeyRecoveryBottoms / (1.0 - heavyKeyRecoveryBottoms);
        double nMin = Math.Log(ratioLK * ratioHK) / Math.Log(alphaLK);
        if (nMin <= 0.0 || double.IsNaN(nMin))
            nMin = 1.0;

        // 3. Fenske Component Distribution for all components
        // (d_i / b_i) = (d_HK / b_HK) * alpha_i^N_min
        double dHK_over_bHK = (1.0 - heavyKeyRecoveryBottoms) / heavyKeyRecoveryBottoms;
        double[] dFlows = new double[nComp];
        double[] bFlows = new double[nComp];
        double totalDistillate = 0.0;
        double totalBottoms = 0.0;

        for (int i = 0; i < nComp; i++)
        {
            double f_i = totalFeedFlow * zFeed[i];
            double r_i;
            if (i == lightKeyIndex)
            {
                r_i = ratioLK;
            }
            else if (i == heavyKeyIndex)
            {
                r_i = dHK_over_bHK;
            }
            else
            {
                double logTerm = Math.Log(dHK_over_bHK) + nMin * Math.Log(alpha[i]);
                r_i = logTerm > 50.0 ? 1e20 : (logTerm < -50.0 ? 1e-20 : Math.Exp(logTerm));
            }

            double di = (r_i >= 1e15) ? f_i : (r_i <= 1e-15 ? 0.0 : f_i * (r_i / (1.0 + r_i)));
            di = Math.Clamp(di, 0.0, f_i);
            double bi = f_i - di;

            dFlows[i] = di;
            bFlows[i] = bi;
            totalDistillate += di;
            totalBottoms += bi;
        }

        totalDistillate = Math.Max(1e-9, totalDistillate);
        totalBottoms = Math.Max(1e-9, totalBottoms);

        double[] xD = new double[nComp];
        double[] xB = new double[nComp];
        for (int i = 0; i < nComp; i++)
        {
            xD[i] = dFlows[i] / totalDistillate;
            xB[i] = bFlows[i] / totalBottoms;
        }

        // 4. Underwood Equations: Find root theta in (1.0, alphaLK)
        double qFeed = 1.0 - Math.Clamp(feedVaporFraction, 0.0, 1.0);
        double theta = SolveUnderwoodTheta(alpha, zFeed, qFeed, lightKeyIndex, heavyKeyIndex);

        // 2nd Underwood equation: V_min = sum( alpha_i * d_i / (alpha_i - theta) )
        double vMin = 0.0;
        for (int i = 0; i < nComp; i++)
        {
            vMin += (alpha[i] * dFlows[i]) / (alpha[i] - theta);
        }

        double rMin = Math.Max(0.01, (vMin / totalDistillate) - 1.0);

        // 5. Operating Reflux Ratio R
        double rOperating;
        if (fixedRefluxRatio.HasValue)
        {
            if (fixedRefluxRatio.Value <= rMin)
                throw new InvalidOperationException($"Fixed reflux ratio {fixedRefluxRatio.Value:F3} must be greater than minimum reflux R_min = {rMin:F3}.");
            rOperating = fixedRefluxRatio.Value;
        }
        else
        {
            double mult = refluxRatioMultiplier ?? 1.25;
            if (mult <= 1.0)
                throw new ArgumentOutOfRangeException(nameof(refluxRatioMultiplier), "Reflux ratio multiplier must be > 1.0.");
            rOperating = mult * rMin;
        }

        // 6. Gilliland Correlation: Actual Theoretical Stages N
        double nActual = CalculateGillilandStages(nMin, rMin, rOperating, correlation);
        int recommendedStages = Math.Max(3, (int)Math.Ceiling(nActual));

        // 7. Kirkbride Correlation: Optimal Feed Stage Location
        (double nr, double ns, int feedStage) = CalculateKirkbrideFeedStage(
            zFeed[lightKeyIndex],
            zFeed[heavyKeyIndex],
            xB[lightKeyIndex],
            xD[heavyKeyIndex],
            totalBottoms,
            totalDistillate,
            recommendedStages
        );

        // 8. Thermodynamic bubble points and Condenser / Reboiler Enthalpy Duties
        double tTop = thermo.CalculateBubblePointTemperatureKelvin(compounds, xD, columnPressureKpa);
        double tBtm = thermo.CalculateBubblePointTemperatureKelvin(compounds, xB, columnPressureKpa);

        // Flash distillate and bottoms to determine enthalpies
        FlashResult flashD = thermo.CalculateIsothermalFlash(compounds, xD, tTop, columnPressureKpa);
        FlashResult flashB = thermo.CalculateIsothermalFlash(compounds, xB, tBtm, columnPressureKpa);
        FlashResult flashF = thermo.CalculateIsothermalFlash(compounds, zFeed, feedTemperatureK, columnPressureKpa);

        // Condenser cooling duty Q_C: V_top = (R + 1) * D
        // Delta H_vap at top estimated by average heat of vaporization
        double avgHvapTop = 0.0;
        for (int i = 0; i < nComp; i++) avgHvapTop += xD[i] * compounds[i].HeatOfVaporizationJPerMol;
        double vTopFlow = (rOperating + 1.0) * totalDistillate;
        double qCondenser = -vTopFlow * avgHvapTop; // strictly negative (cooling)

        // First-Law column overall energy balance closure:
        // Q_reboiler + Q_condenser + F * h_F = D * h_D + B * h_B
        double hD_total = totalDistillate * flashD.MolarEnthalpyJPerMol;
        double hB_total = totalBottoms * flashB.MolarEnthalpyJPerMol;
        double hF_total = totalFeedFlow * flashF.MolarEnthalpyJPerMol;
        double qReboiler = (hD_total + hB_total) - hF_total - qCondenser;
        if (qReboiler <= 0.0)
        {
            // Fallback estimation using bottoms vaporization
            double avgHvapBtm = 0.0;
            for (int i = 0; i < nComp; i++) avgHvapBtm += xB[i] * compounds[i].HeatOfVaporizationJPerMol;
            double vBtm = (rOperating + qFeed) * totalDistillate;
            qReboiler = Math.Max(1000.0, vBtm * avgHvapBtm);
        }

        // 9. Column Diameter Estimation via Souders-Brown / Fair Flooding
        double avgMolarMassTop = 0.0;
        for (int i = 0; i < nComp; i++) avgMolarMassTop += xD[i] * compounds[i].MolarMass; // g/mol
        double pPa = columnPressureKpa * 1000.0;
        double rhoVaporTop = (pPa * (avgMolarMassTop * 1e-3)) / (GasConstant * tTop); // kg/m^3
        double rhoLiquidTop = 750.0; // standard hydrocarbon liquid density estimate kg/m^3

        double cSoudersBrown = 0.075; // m/s for 24-in tray spacing
        double vFlood = cSoudersBrown * Math.Sqrt(Math.Max(1.0, (rhoLiquidTop - rhoVaporTop) / Math.Max(0.1, rhoVaporTop)));
        double vOp = 0.75 * vFlood; // 75% of flood velocity

        double vaporMassFlowKgPerSec = (vTopFlow * (avgMolarMassTop * 1e-3));
        double volumetricFlowM3PerSec = vaporMassFlowKgPerSec / Math.Max(0.1, rhoVaporTop);
        double activeAreaM2 = volumetricFlowM3PerSec / Math.Max(0.1, vOp);
        double totalAreaM2 = activeAreaM2 / 0.85; // 15% downcomer area
        double diameter = Math.Sqrt(Math.Max(0.01, (4.0 * totalAreaM2) / Math.PI));

        return new FugDesignResult(
            MinimumStagesNmin: nMin,
            MinimumRefluxRatioRmin: rMin,
            OperatingRefluxRatio: rOperating,
            TheoreticalStagesN: nActual,
            RecommendedNumberOfStages: recommendedStages,
            OptimalFeedStage: feedStage,
            RectifyingStagesNr: nr,
            StrippingStagesNs: ns,
            UnderwoodTheta: theta,
            DistillateRateMolPerSec: totalDistillate,
            BottomsRateMolPerSec: totalBottoms,
            DistillateCompositionX: xD,
            BottomsCompositionX: xB,
            RelativeVolatilities: alpha,
            TopTemperatureKelvin: tTop,
            BottomTemperatureKelvin: tBtm,
            CondenserDutyWatts: qCondenser,
            ReboilerDutyWatts: qReboiler,
            EstimatedDiameterMeters: diameter
        );
    }

    /// <summary>
    /// Solves the 1st Underwood equation for the unique root theta in (1.0, alpha_LK):
    ///   f(theta) = sum( alpha_i * z_i / (alpha_i - theta) ) - (1 - q) = 0.
    /// Uses robust hybrid Bisection + Newton-Raphson ensuring monotonic convergence.
    /// </summary>
    public static double SolveUnderwoodTheta(
        IReadOnlyList<double> alpha,
        IReadOnlyList<double> zFeed,
        double q,
        int lightKeyIndex,
        int heavyKeyIndex)
    {
        int nComp = alpha.Count;
        double alphaLK = alpha[lightKeyIndex];
        double alphaHK = 1.0;

        // Bracket strictly inside (alphaHK, alphaLK)
        double a = alphaHK + 1e-6;
        double b = alphaLK - 1e-6;

        double fUnderwood(double th)
        {
            double sum = 0.0;
            for (int i = 0; i < nComp; i++)
            {
                sum += (alpha[i] * zFeed[i]) / (alpha[i] - th);
            }
            return sum - (1.0 - q);
        }

        double fPrimeUnderwood(double th)
        {
            double sum = 0.0;
            for (int i = 0; i < nComp; i++)
            {
                double diff = alpha[i] - th;
                sum += (alpha[i] * zFeed[i]) / (diff * diff);
            }
            return sum; // Strictly > 0 everywhere
        }

        double fa = fUnderwood(a);
        double fb = fUnderwood(b);

        // Expand bounds slightly if needed
        int attempts = 0;
        while (fa > 0 && attempts < 20)
        {
            a = alphaHK + (a - alphaHK) * 0.1;
            fa = fUnderwood(a);
            attempts++;
        }
        attempts = 0;
        while (fb < 0 && attempts < 20)
        {
            b = alphaLK - (alphaLK - b) * 0.1;
            fb = fUnderwood(b);
            attempts++;
        }

        // Bisection / Newton hybrid
        double theta = 0.5 * (a + b);
        for (int iter = 0; iter < 60; iter++)
        {
            double fVal = fUnderwood(theta);
            if (Math.Abs(fVal) < 1e-10)
                break;

            if (fVal > 0.0)
                b = theta;
            else
                a = theta;

            double df = fPrimeUnderwood(theta);
            if (df > 1e-12)
            {
                double step = fVal / df;
                double nextTheta = theta - step;
                if (nextTheta > a && nextTheta < b)
                {
                    theta = nextTheta;
                    continue;
                }
            }

            // Fallback to bisection
            theta = 0.5 * (a + b);
        }

        return theta;
    }

    /// <summary>
    /// Calculates the actual theoretical stages N from Gilliland correlation given N_min, R_min, and R.
    /// </summary>
    public static double CalculateGillilandStages(
        double nMin,
        double rMin,
        double rOperating,
        GillilandCorrelationType correlation = GillilandCorrelationType.Molokanov)
    {
        if (rOperating <= rMin)
            throw new ArgumentOutOfRangeException(nameof(rOperating), $"Reflux ratio R ({rOperating:F3}) must exceed R_min ({rMin:F3}).");

        double x = (rOperating - rMin) / (rOperating + 1.0);
        x = Math.Clamp(x, 1e-5, 0.9999);

        double y;
        if (correlation == GillilandCorrelationType.Eduljee)
        {
            // Eduljee (1975): Y = 0.75 * (1 - X^0.5668)
            y = 0.75 * (1.0 - Math.Pow(x, 0.5668));
        }
        else
        {
            // Molokanov (1972):
            // Y = 1 - exp( [(1 + 54.4 X) / (11 + 117.2 X)] * [(X - 1) / sqrt(X)] )
            double expArg = ((1.0 + 54.4 * x) / (11.0 + 117.2 * x)) * ((x - 1.0) / Math.Sqrt(x));
            y = 1.0 - Math.Exp(expArg);
        }

        y = Math.Clamp(y, 1e-5, 0.999);

        // Y = (N - N_min) / (N + 1)  ==>  N = (N_min + Y) / (1 - Y)
        double nActual = (nMin + y) / (1.0 - y);
        return Math.Max(nMin, nActual);
    }

    /// <summary>
    /// Calculates optimal feed stage location via Kirkbride correlation.
    /// Returns (N_rectifying, N_stripping, FeedStage_1Indexed).
    /// </summary>
    public static (double Nr, double Ns, int FeedStage) CalculateKirkbrideFeedStage(
        double zLK,
        double zHK,
        double xB_LK,
        double xD_HK,
        double totalBottoms,
        double totalDistillate,
        int totalStages)
    {
        double term1 = Math.Max(1e-6, zHK) / Math.Max(1e-6, zLK);
        double term2 = Math.Max(1e-8, xB_LK) / Math.Max(1e-8, xD_HK);
        double term3 = Math.Max(1e-6, totalBottoms) / Math.Max(1e-6, totalDistillate);

        double ratioNrOverNs = Math.Pow(term1 * (term2 * term2) * term3, 0.206);
        ratioNrOverNs = Math.Clamp(ratioNrOverNs, 0.05, 20.0);

        double n = Math.Max(3, totalStages);
        double ns = n / (1.0 + ratioNrOverNs);
        double nr = n - ns;

        int feedStage = (int)Math.Round(nr) + 1;
        feedStage = Math.Clamp(feedStage, 2, totalStages - 1);

        return (nr, ns, feedStage);
    }
}
