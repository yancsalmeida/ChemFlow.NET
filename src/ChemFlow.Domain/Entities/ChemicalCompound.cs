using ChemFlow.Domain.ValueObjects;

namespace ChemFlow.Domain.Entities;

/// <summary>
/// Represents a pure chemical species with its fundamental thermodynamic properties.
/// </summary>
public sealed class ChemicalCompound
{
    /// <summary>Reference temperature for enthalpy calculations (298.15 K = 25 °C).</summary>
    public const double ReferenceTemperatureKelvin = 298.15;

    /// <summary>Unique identifier / formula or slug (e.g., "H2O", "C6H6").</summary>
    public string Id { get; }

    /// <summary>Display name (e.g., "Water", "Benzene").</summary>
    public string Name { get; }

    /// <summary>Chemical formula (e.g., "H2O", "C6H6").</summary>
    public string Formula { get; }

    /// <summary>Molar mass in g/mol (or kg/kmol).</summary>
    public double MolarMass { get; }

    /// <summary>Critical temperature Tc in Kelvin (K).</summary>
    public double CriticalTemperatureKelvin { get; }

    /// <summary>Critical pressure Pc in kilopascals (kPa).</summary>
    public double CriticalPressureKpa { get; }

    /// <summary>Pitzer acentric factor (dimensionless).</summary>
    public double AcentricFactor { get; }

    /// <summary>Liquid molar heat capacity Cp_L in J/(mol·K).</summary>
    public double LiquidHeatCapacityJPerMolK { get; }

    /// <summary>Ideal-gas vapor molar heat capacity Cp_V in J/(mol·K).</summary>
    public double VaporHeatCapacityJPerMolK { get; }

    /// <summary>Latent heat of vaporization ΔH_vap at the reference temperature in J/mol.</summary>
    public double HeatOfVaporizationJPerMol { get; }

    /// <summary>Antoine correlation parameters for vapor pressure calculation.</summary>
    public AntoineCoefficients Antoine { get; }

    public ChemicalCompound(
        string id,
        string name,
        string formula,
        double molarMass,
        double criticalTemperatureKelvin,
        double criticalPressureKpa,
        double acentricFactor,
        double liquidHeatCapacityJPerMolK,
        double vaporHeatCapacityJPerMolK,
        double heatOfVaporizationJPerMol,
        AntoineCoefficients antoine)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Compound ID cannot be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Compound name cannot be empty.", nameof(name));
        if (molarMass <= 0)
            throw new ArgumentOutOfRangeException(nameof(molarMass), "Molar mass must be positive.");
        if (criticalTemperatureKelvin <= 0)
            throw new ArgumentOutOfRangeException(nameof(criticalTemperatureKelvin), "Critical temperature must be positive.");
        if (criticalPressureKpa <= 0)
            throw new ArgumentOutOfRangeException(nameof(criticalPressureKpa), "Critical pressure must be positive.");

        Id = id.Trim();
        Name = name.Trim();
        Formula = string.IsNullOrWhiteSpace(formula) ? Id : formula.Trim();
        MolarMass = molarMass;
        CriticalTemperatureKelvin = criticalTemperatureKelvin;
        CriticalPressureKpa = criticalPressureKpa;
        AcentricFactor = acentricFactor;
        LiquidHeatCapacityJPerMolK = liquidHeatCapacityJPerMolK;
        VaporHeatCapacityJPerMolK = vaporHeatCapacityJPerMolK;
        HeatOfVaporizationJPerMol = heatOfVaporizationJPerMol;
        Antoine = antoine;
    }

    /// <summary>
    /// Computes the saturation pressure P_sat (in kPa) at temperature T (in K).
    /// </summary>
    public double GetSaturationPressureKpa(double temperatureKelvin) =>
        Antoine.CalculateSaturationPressureKpa(temperatureKelvin);

    /// <summary>
    /// Computes the normal boiling point (at 1 atm = 101.325 kPa) in Kelvin.
    /// </summary>
    public double GetNormalBoilingPointKelvin() =>
        Antoine.CalculateSaturationTemperatureKelvin(101.325);

    /// <summary>
    /// Computes the liquid molar enthalpy H_L(T) in J/mol relative to liquid at 298.15 K.
    /// H_L(T) = Cp_L * (T - T_ref)
    /// </summary>
    public double CalculateLiquidEnthalpyJPerMol(double temperatureKelvin)
    {
        if (temperatureKelvin <= 0)
            throw new ArgumentOutOfRangeException(nameof(temperatureKelvin), "Temperature must be > 0 K.");

        return LiquidHeatCapacityJPerMolK * (temperatureKelvin - ReferenceTemperatureKelvin);
    }

    /// <summary>
    /// Computes the vapor molar enthalpy H_V(T) in J/mol relative to liquid at 298.15 K.
    /// H_V(T) = ΔH_vap(T_ref) + Cp_V * (T - T_ref)
    /// </summary>
    public double CalculateVaporEnthalpyJPerMol(double temperatureKelvin)
    {
        if (temperatureKelvin <= 0)
            throw new ArgumentOutOfRangeException(nameof(temperatureKelvin), "Temperature must be > 0 K.");

        return HeatOfVaporizationJPerMol + VaporHeatCapacityJPerMolK * (temperatureKelvin - ReferenceTemperatureKelvin);
    }
}
