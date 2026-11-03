using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Abstract base class for unit operations implementing the Template Method design pattern:
/// validates preconditions (all inlet streams solved) before invoking the subclass-specific physics.
/// </summary>
public abstract class UnitOperationBase : IUnitOperation
{
    public string Id { get; }
    public string Name { get; }
    public abstract string UnitType { get; }
    public IReadOnlyList<MaterialStream> InletStreams { get; }
    public IReadOnlyList<MaterialStream> OutletStreams { get; }
    public double EnergyDutyWatts { get; protected set; }

    protected UnitOperationBase(
        string id,
        string name,
        IReadOnlyList<MaterialStream> inletStreams,
        IReadOnlyList<MaterialStream> outletStreams)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Unit ID cannot be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Unit name cannot be empty.", nameof(name));
        if (inletStreams is null || inletStreams.Count == 0)
            throw new ArgumentException("At least one inlet stream is required.", nameof(inletStreams));
        if (outletStreams is null || outletStreams.Count == 0)
            throw new ArgumentException("At least one outlet stream is required.", nameof(outletStreams));

        Id = id.Trim();
        Name = name.Trim();
        InletStreams = inletStreams;
        OutletStreams = outletStreams;
    }

    public void Solve(IThermodynamicPackage thermo)
    {
        ArgumentNullException.ThrowIfNull(thermo);

        foreach (MaterialStream inlet in InletStreams)
        {
            if (!inlet.IsSolved)
            {
                throw new InvalidOperationException(
                    $"Cannot solve unit '{Name}' ({UnitType}): inlet stream '{inlet.Name}' has not been solved yet.");
            }
        }

        SolveInternal(thermo);
    }

    protected abstract void SolveInternal(IThermodynamicPackage thermo);
}
