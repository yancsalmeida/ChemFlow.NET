using ChemFlow.Domain.Entities;

namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Implements ideal Vapor-Liquid Equilibrium (VLE) via Raoult's Law:
///   y_i * P = x_i * P_i^sat(T)   =&gt;   K_i = P_i^sat(T) / P
/// </summary>
public sealed class RaoultThermodynamicPackage : IThermodynamicPackage
{
    public string Name => "Ideal Raoult's Law";

    public double[] CalculateKValues(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> composition,
        double temperatureKelvin,
        double pressureKpa)
    {
        ValidateInputs(compounds, composition);
        if (temperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(temperatureKelvin), "Temperature must be > 0 K.");
        if (pressureKpa <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(pressureKpa), "Pressure must be > 0 kPa.");

        double[] kValues = new double[compounds.Count];
        for (int i = 0; i < compounds.Count; i++)
        {
            kValues[i] = compounds[i].GetSaturationPressureKpa(temperatureKelvin) / pressureKpa;
        }

        return kValues;
    }

    public double CalculateBubblePointPressureKpa(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> liquidCompositionX,
        double temperatureKelvin)
    {
        ValidateInputs(compounds, liquidCompositionX);
        if (temperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(temperatureKelvin), "Temperature must be > 0 K.");

        double bubblePressure = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            bubblePressure += liquidCompositionX[i] * compounds[i].GetSaturationPressureKpa(temperatureKelvin);
        }

        return bubblePressure;
    }

    public double CalculateDewPointPressureKpa(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> vaporCompositionY,
        double temperatureKelvin)
    {
        ValidateInputs(compounds, vaporCompositionY);
        if (temperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(temperatureKelvin), "Temperature must be > 0 K.");

        double inverseSum = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            inverseSum += vaporCompositionY[i] / compounds[i].GetSaturationPressureKpa(temperatureKelvin);
        }

        return 1.0 / inverseSum;
    }

    public double CalculateBubblePointTemperatureKelvin(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> liquidCompositionX,
        double pressureKpa)
    {
        ValidateInputs(compounds, liquidCompositionX);
        if (pressureKpa <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(pressureKpa), "Pressure must be > 0 kPa.");

        // Initial guess: mole-fraction weighted average of pure-component saturation temperatures
        double temperature = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            temperature += liquidCompositionX[i] * compounds[i].Antoine.CalculateSaturationTemperatureKelvin(pressureKpa);
        }

        // Newton-Raphson on g(T) = Σ x_i * P_i^sat(T) - P = 0
        for (int iter = 0; iter < 100; iter++)
        {
            double g = -pressureKpa;
            double dg = 0.0;

            for (int i = 0; i < compounds.Count; i++)
            {
                double pSat = compounds[i].GetSaturationPressureKpa(temperature);
                double denom = temperature + compounds[i].Antoine.C;
                double dPsatdT = pSat * (compounds[i].Antoine.B / (denom * denom));

                g += liquidCompositionX[i] * pSat;
                dg += liquidCompositionX[i] * dPsatdT;
            }

            if (Math.Abs(g) <= 1e-8)
                return temperature;

            double deltaT = g / dg;
            temperature -= deltaT;

            if (Math.Abs(deltaT) <= 1e-9)
                return temperature;
        }

        return temperature;
    }

    public double CalculateDewPointTemperatureKelvin(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> vaporCompositionY,
        double pressureKpa)
    {
        ValidateInputs(compounds, vaporCompositionY);
        if (pressureKpa <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(pressureKpa), "Pressure must be > 0 kPa.");

        // Initial guess: mole-fraction weighted average of pure-component saturation temperatures
        double temperature = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            temperature += vaporCompositionY[i] * compounds[i].Antoine.CalculateSaturationTemperatureKelvin(pressureKpa);
        }

        // Newton-Raphson on h(T) = Σ (y_i / P_i^sat(T)) - (1 / P) = 0
        double targetInverseP = 1.0 / pressureKpa;
        for (int iter = 0; iter < 100; iter++)
        {
            double h = -targetInverseP;
            double dh = 0.0;

            for (int i = 0; i < compounds.Count; i++)
            {
                double pSat = compounds[i].GetSaturationPressureKpa(temperature);
                double denom = temperature + compounds[i].Antoine.C;
                double dInvPsatdT = -(1.0 / pSat) * (compounds[i].Antoine.B / (denom * denom));

                h += vaporCompositionY[i] / pSat;
                dh += vaporCompositionY[i] * dInvPsatdT;
            }

            if (Math.Abs(h) <= 1e-10)
                return temperature;

            double deltaT = h / dh;
            temperature -= deltaT;

            if (Math.Abs(deltaT) <= 1e-9)
                return temperature;
        }

        return temperature;
    }

    public FlashResult CalculateIsothermalFlash(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> overallCompositionZ,
        double temperatureKelvin,
        double pressureKpa)
    {
        double[] kValues = CalculateKValues(compounds, overallCompositionZ, temperatureKelvin, pressureKpa);
        RachfordRiceSolver.Solution rr = RachfordRiceSolver.Solve(overallCompositionZ, kValues);

        double liquidEnthalpy = 0.0;
        double vaporEnthalpy = 0.0;

        for (int i = 0; i < compounds.Count; i++)
        {
            liquidEnthalpy += rr.LiquidCompositionX[i] * compounds[i].CalculateLiquidEnthalpyJPerMol(temperatureKelvin);
            vaporEnthalpy += rr.VaporCompositionY[i] * compounds[i].CalculateVaporEnthalpyJPerMol(temperatureKelvin);
        }

        double totalMolarEnthalpy = (1.0 - rr.Beta) * liquidEnthalpy + rr.Beta * vaporEnthalpy;

        return new FlashResult(
            Phase: rr.Phase,
            TemperatureKelvin: temperatureKelvin,
            PressureKpa: pressureKpa,
            VaporFractionBeta: rr.Beta,
            OverallCompositionZ: overallCompositionZ.ToArray(),
            LiquidCompositionX: rr.LiquidCompositionX,
            VaporCompositionY: rr.VaporCompositionY,
            KValues: kValues,
            MolarEnthalpyJPerMol: totalMolarEnthalpy,
            Iterations: rr.Iterations
        );
    }

    private static void ValidateInputs(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> composition)
    {
        if (compounds is null || compounds.Count == 0)
            throw new ArgumentException("Compounds list cannot be empty.", nameof(compounds));
        if (composition is null || composition.Count != compounds.Count)
            throw new ArgumentException("Composition length must match compounds count.", nameof(composition));

        double sum = 0.0;
        for (int i = 0; i < composition.Count; i++)
        {
            if (composition[i] < 0.0)
                throw new ArgumentOutOfRangeException(nameof(composition), "Mole fractions cannot be negative.");
            sum += composition[i];
        }

        if (Math.Abs(sum - 1.0) > 1e-4)
            throw new ArgumentException($"Mole fractions must sum to 1.0 (actual sum = {sum:F6}).", nameof(composition));
    }
}
