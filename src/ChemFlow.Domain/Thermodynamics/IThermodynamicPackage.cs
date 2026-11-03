using ChemFlow.Domain.Entities;

namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Strategy interface for thermodynamic phase-equilibrium and enthalpy calculations.
/// Follows the Open/Closed Principle (OCP) and Dependency Inversion Principle (DIP):
/// unit operations depend on this abstraction rather than a concrete equation of state.
/// </summary>
public interface IThermodynamicPackage
{
    /// <summary>Name of the thermodynamic model (e.g., "Ideal Raoult's Law").</summary>
    string Name { get; }

    /// <summary>
    /// Computes equilibrium ratios K_i = y_i / x_i for each component at (T, P).
    /// </summary>
    double[] CalculateKValues(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> composition,
        double temperatureKelvin,
        double pressureKpa);

    /// <summary>
    /// Computes the bubble-point pressure P_bubble (in kPa) at a given temperature T (in K) and liquid composition x.
    /// </summary>
    double CalculateBubblePointPressureKpa(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> liquidCompositionX,
        double temperatureKelvin);

    /// <summary>
    /// Computes the dew-point pressure P_dew (in kPa) at a given temperature T (in K) and vapor composition y.
    /// </summary>
    double CalculateDewPointPressureKpa(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> vaporCompositionY,
        double temperatureKelvin);

    /// <summary>
    /// Computes the bubble-point temperature T_bubble (in K) at a given pressure P (in kPa) and liquid composition x.
    /// </summary>
    double CalculateBubblePointTemperatureKelvin(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> liquidCompositionX,
        double pressureKpa);

    /// <summary>
    /// Computes the dew-point temperature T_dew (in K) at a given pressure P (in kPa) and vapor composition y.
    /// </summary>
    double CalculateDewPointTemperatureKelvin(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> vaporCompositionY,
        double pressureKpa);

    /// <summary>
    /// Performs an isothermal (T, P) flash calculation for feed composition z, returning phase state,
    /// vapor fraction β, equilibrium phase compositions (x, y), and mixture molar enthalpy H.
    /// </summary>
    FlashResult CalculateIsothermalFlash(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> overallCompositionZ,
        double temperatureKelvin,
        double pressureKpa);
}
