using ChemFlow.Domain.Entities;
using ChemFlow.Domain.ValueObjects;

namespace ChemFlow.Domain.Data;

/// <summary>
/// Provides standard chemical compounds with validated literature constants
/// (Smith, Van Ness &amp; Abbott - Introduction to Chemical Engineering Thermodynamics).
/// Antoine equation form: ln(P_sat [kPa]) = A - B / (T [K] + C)
/// </summary>
public static class DefaultCompounds
{
    public static ChemicalCompound Water { get; } = new(
        id: "H2O",
        name: "Water",
        formula: "H2O",
        molarMass: 18.015,
        criticalTemperatureKelvin: 647.1,
        criticalPressureKpa: 22055.0,
        acentricFactor: 0.345,
        liquidHeatCapacityJPerMolK: 75.3,
        vaporHeatCapacityJPerMolK: 33.6,
        heatOfVaporizationJPerMol: 40650.0,
        antoine: new AntoineCoefficients(16.3872, 3885.70, -42.98)
    );

    public static ChemicalCompound Ethanol { get; } = new(
        id: "C2H5OH",
        name: "Ethanol",
        formula: "C2H6O",
        molarMass: 46.069,
        criticalTemperatureKelvin: 513.9,
        criticalPressureKpa: 6148.0,
        acentricFactor: 0.645,
        liquidHeatCapacityJPerMolK: 112.3,
        vaporHeatCapacityJPerMolK: 65.4,
        heatOfVaporizationJPerMol: 38560.0,
        antoine: new AntoineCoefficients(16.8958, 3795.17, -42.23)
    );

    public static ChemicalCompound Benzene { get; } = new(
        id: "C6H6",
        name: "Benzene",
        formula: "C6H6",
        molarMass: 78.114,
        criticalTemperatureKelvin: 562.2,
        criticalPressureKpa: 4898.0,
        acentricFactor: 0.210,
        liquidHeatCapacityJPerMolK: 136.0,
        vaporHeatCapacityJPerMolK: 82.4,
        heatOfVaporizationJPerMol: 30720.0,
        antoine: new AntoineCoefficients(13.7819, 2726.81, -55.58)
    );

    public static ChemicalCompound Toluene { get; } = new(
        id: "C7H8",
        name: "Toluene",
        formula: "C7H8",
        molarMass: 92.141,
        criticalTemperatureKelvin: 591.8,
        criticalPressureKpa: 4106.0,
        acentricFactor: 0.262,
        liquidHeatCapacityJPerMolK: 157.3,
        vaporHeatCapacityJPerMolK: 103.7,
        heatOfVaporizationJPerMol: 33180.0,
        antoine: new AntoineCoefficients(13.9320, 3056.96, -55.52)
    );

    public static ChemicalCompound NHexane { get; } = new(
        id: "n-C6H14",
        name: "n-Hexane",
        formula: "C6H14",
        molarMass: 86.178,
        criticalTemperatureKelvin: 507.6,
        criticalPressureKpa: 3025.0,
        acentricFactor: 0.301,
        liquidHeatCapacityJPerMolK: 195.6,
        vaporHeatCapacityJPerMolK: 143.1,
        heatOfVaporizationJPerMol: 28850.0,
        antoine: new AntoineCoefficients(13.8193, 2696.04, -48.83)
    );

    public static ChemicalCompound NHeptane { get; } = new(
        id: "n-C7H16",
        name: "n-Heptane",
        formula: "C7H16",
        molarMass: 100.205,
        criticalTemperatureKelvin: 540.2,
        criticalPressureKpa: 2740.0,
        acentricFactor: 0.350,
        liquidHeatCapacityJPerMolK: 224.6,
        vaporHeatCapacityJPerMolK: 166.0,
        heatOfVaporizationJPerMol: 31770.0,
        antoine: new AntoineCoefficients(13.8622, 2910.26, -56.70)
    );

    public static IReadOnlyList<ChemicalCompound> All { get; } =
    [
        Water,
        Ethanol,
        Benzene,
        Toluene,
        NHexane,
        NHeptane
    ];
}
