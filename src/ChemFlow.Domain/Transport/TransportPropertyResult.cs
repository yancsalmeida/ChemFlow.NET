using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Transport;

/// <summary>
/// Encapsulates rigorous fluid transport properties (dynamic viscosity, kinematic viscosity,
/// thermal conductivity, and dimensionless Prandtl number) for pure fluids and multicomponent mixtures.
/// </summary>
public sealed record TransportPropertyResult
{
    /// <summary>Dynamic viscosity in Pa·s (1 Pa·s = 1000 cP = 10 P = 10^7 μP).</summary>
    public double DynamicViscosityPaS { get; init; }

    /// <summary>Dynamic viscosity in centipoise (cP) (1 cP = 1 mPa·s).</summary>
    public double DynamicViscosityCp => DynamicViscosityPaS * 1000.0;

    /// <summary>Thermal conductivity in W/(m·K) (or J/(s·m·K)).</summary>
    public double ThermalConductivityWPerMK { get; init; }

    /// <summary>Kinematic viscosity ν = μ / ρ in m²/s.</summary>
    public double KinematicViscosityM2PerS { get; init; }

    /// <summary>Kinematic viscosity in centistokes (cSt) (1 cSt = 10^-6 m²/s = 1 mm²/s).</summary>
    public double KinematicViscosityCSt => KinematicViscosityM2PerS * 1e6;

    /// <summary>Dimensionless Prandtl number Pr = (Cp * μ) / λ.</summary>
    public double PrandtlNumber { get; init; }

    /// <summary>Fluid density ρ in kg/m³ used for kinematic viscosity calculation.</summary>
    public double DensityKgPerM3 { get; init; }

    /// <summary>Phase state of the fluid (Vapor, Liquid, or TwoPhase).</summary>
    public PhaseState Phase { get; init; }

    /// <summary>Evaluation temperature in Kelvin (K).</summary>
    public double TemperatureKelvin { get; init; }

    /// <summary>Evaluation temperature in Celsius (°C).</summary>
    public double TemperatureCelsius => TemperatureKelvin - 273.15;

    /// <summary>Evaluation pressure in kilopascals (kPa).</summary>
    public double PressureKpa { get; init; }

    /// <summary>Name of the predictive model used (e.g. "Chung et al. (1984/1988)" or "Ely-Hanley TRAPP").</summary>
    public string ModelName { get; init; } = string.Empty;

    public TransportPropertyResult(
        double dynamicViscosityPaS,
        double thermalConductivityWPerMK,
        double densityKgPerM3,
        double prandtlNumber,
        PhaseState phase,
        double temperatureKelvin,
        double pressureKpa,
        string modelName)
    {
        DynamicViscosityPaS = Math.Max(1e-12, dynamicViscosityPaS);
        ThermalConductivityWPerMK = Math.Max(1e-6, thermalConductivityWPerMK);
        DensityKgPerM3 = Math.Max(1e-6, densityKgPerM3);
        KinematicViscosityM2PerS = DynamicViscosityPaS / DensityKgPerM3;
        PrandtlNumber = Math.Max(1e-4, prandtlNumber);
        Phase = phase;
        TemperatureKelvin = temperatureKelvin;
        PressureKpa = pressureKpa;
        ModelName = modelName;
    }
}
