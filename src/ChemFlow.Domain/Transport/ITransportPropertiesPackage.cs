using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Streams;

namespace ChemFlow.Domain.Transport;

/// <summary>
/// Strategy interface for rigorous multicomponent transport properties estimation
/// (viscosity, thermal conductivity, and Prandtl number) across vapor and liquid phases.
/// </summary>
public interface ITransportPropertiesPackage
{
    /// <summary>Name of the predictive transport model.</summary>
    string Name { get; }

    /// <summary>
    /// Calculates vapor phase transport properties for a multicomponent mixture at (T, P).
    /// </summary>
    TransportPropertyResult CalculateVaporProperties(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> vaporMoleFractionsY,
        double temperatureKelvin,
        double pressureKpa);

    /// <summary>
    /// Calculates liquid phase transport properties for a multicomponent mixture at (T, P).
    /// </summary>
    TransportPropertyResult CalculateLiquidProperties(
        IReadOnlyList<ChemicalCompound> compounds,
        IReadOnlyList<double> liquidMoleFractionsX,
        double temperatureKelvin,
        double pressureKpa);

    /// <summary>
    /// Calculates bulk transport properties for an evaluated material stream.
    /// In two-phase regimes, computes flow-weighted averages across liquid and vapor phases.
    /// </summary>
    TransportPropertyResult CalculateStreamProperties(MaterialStream stream);
}
