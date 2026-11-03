using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Common contract for all chemical process equipment blocks (Mixer, Heater, FlashDrum, Reactor, etc.).
/// </summary>
public interface IUnitOperation
{
    string Id { get; }
    string Name { get; }
    string UnitType { get; }
    IReadOnlyList<MaterialStream> InletStreams { get; }
    IReadOnlyList<MaterialStream> OutletStreams { get; }

    /// <summary>Net thermal energy duty Q_dot added to the system in Watts (J/s).</summary>
    double EnergyDutyWatts { get; }

    /// <summary>
    /// Executes the mass, energy, and phase-equilibrium balances for this equipment block.
    /// </summary>
    void Solve(IThermodynamicPackage thermo);
}
