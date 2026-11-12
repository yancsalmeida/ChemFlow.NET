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

    public static ChemicalCompound Propane { get; } = new(
        id: "C3H8",
        name: "Propane",
        formula: "C3H8",
        molarMass: 44.097,
        criticalTemperatureKelvin: 369.8,
        criticalPressureKpa: 4248.0,
        acentricFactor: 0.152,
        liquidHeatCapacityJPerMolK: 98.0,
        vaporHeatCapacityJPerMolK: 73.5,
        heatOfVaporizationJPerMol: 19040.0,
        antoine: new AntoineCoefficients(13.7118, 1872.46, -25.16)
    );

    public static ChemicalCompound Isobutane { get; } = new(
        id: "i-C4H10",
        name: "Isobutane",
        formula: "C4H10",
        molarMass: 58.123,
        criticalTemperatureKelvin: 407.8,
        criticalPressureKpa: 3640.0,
        acentricFactor: 0.181,
        liquidHeatCapacityJPerMolK: 130.0,
        vaporHeatCapacityJPerMolK: 96.0,
        heatOfVaporizationJPerMol: 21290.0,
        antoine: new AntoineCoefficients(13.5658, 2040.85, -33.15)
    );

    public static ChemicalCompound NButane { get; } = new(
        id: "n-C4H10",
        name: "n-Butane",
        formula: "C4H10",
        molarMass: 58.123,
        criticalTemperatureKelvin: 425.1,
        criticalPressureKpa: 3796.0,
        acentricFactor: 0.200,
        liquidHeatCapacityJPerMolK: 132.4,
        vaporHeatCapacityJPerMolK: 97.4,
        heatOfVaporizationJPerMol: 22440.0,
        antoine: new AntoineCoefficients(13.6608, 2154.70, -34.42)
    );

    public static ChemicalCompound Isopentane { get; } = new(
        id: "i-C5H12",
        name: "Isopentane",
        formula: "C5H12",
        molarMass: 72.150,
        criticalTemperatureKelvin: 460.4,
        criticalPressureKpa: 3380.0,
        acentricFactor: 0.229,
        liquidHeatCapacityJPerMolK: 164.0,
        vaporHeatCapacityJPerMolK: 118.0,
        heatOfVaporizationJPerMol: 24600.0,
        antoine: new AntoineCoefficients(13.6946, 2369.34, -40.15)
    );

    public static ChemicalCompound NPentane { get; } = new(
        id: "n-C5H12",
        name: "n-Pentane",
        formula: "C5H12",
        molarMass: 72.150,
        criticalTemperatureKelvin: 469.7,
        criticalPressureKpa: 3370.0,
        acentricFactor: 0.252,
        liquidHeatCapacityJPerMolK: 167.2,
        vaporHeatCapacityJPerMolK: 120.2,
        heatOfVaporizationJPerMol: 25790.0,
        antoine: new AntoineCoefficients(13.7983, 2477.07, -39.94)
    );

    public static ChemicalCompound PXylene { get; } = new(
        id: "p-C8H10",
        name: "p-Xylene",
        formula: "C8H10",
        molarMass: 106.167,
        criticalTemperatureKelvin: 616.2,
        criticalPressureKpa: 3511.0,
        acentricFactor: 0.322,
        liquidHeatCapacityJPerMolK: 182.0,
        vaporHeatCapacityJPerMolK: 126.8,
        heatOfVaporizationJPerMol: 35670.0,
        antoine: new AntoineCoefficients(14.0579, 3331.45, -57.97)
    );

    public static IReadOnlyList<ChemicalCompound> All { get; } =
    [
        Water,
        Ethanol,
        Benzene,
        Toluene,
        NHexane,
        NHeptane,
        Propane,
        Isobutane,
        NButane,
        Isopentane,
        NPentane,
        PXylene
    ];
}
