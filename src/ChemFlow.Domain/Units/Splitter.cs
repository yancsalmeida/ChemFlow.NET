using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Stream Splitter (divider) partitioning a single inlet stream into multiple outlet streams
/// with identical composition, temperature, and pressure according to specified split fractions \alpha_k.
/// </summary>
public sealed class Splitter : UnitOperationBase
{
    public override string UnitType => "Splitter";
    public MaterialStream InletStream => InletStreams[0];
    public IReadOnlyList<double> SplitFractions { get; }

    public Splitter(
        string id,
        string name,
        MaterialStream inletStream,
        IReadOnlyList<MaterialStream> outletStreams,
        IReadOnlyList<double> splitFractions)
        : base(id, name, [inletStream], outletStreams)
    {
        ArgumentNullException.ThrowIfNull(splitFractions);
        if (splitFractions.Count != outletStreams.Count)
            throw new ArgumentException("Count of split fractions must match count of outlet streams.", nameof(splitFractions));

        double sum = 0.0;
        foreach (double fraction in splitFractions)
        {
            if (fraction < 0.0 || fraction > 1.0)
                throw new ArgumentOutOfRangeException(nameof(splitFractions), "Each split fraction must be between 0.0 and 1.0.");
            sum += fraction;
        }

        if (Math.Abs(sum - 1.0) > 1e-4)
            throw new ArgumentException($"Split fractions must sum to 1.0 (got {sum:F4}).", nameof(splitFractions));

        SplitFractions = splitFractions;
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        EnergyDutyWatts = 0.0;
        double totalInletFlow = InletStream.MolarFlowRateMolPerSec;
        double t = InletStream.TemperatureKelvin;
        double p = InletStream.PressureKpa;
        IReadOnlyList<double> z = InletStream.CompositionZ;

        for (int i = 0; i < OutletStreams.Count; i++)
        {
            double outletFlow = totalInletFlow * SplitFractions[i];
            OutletStreams[i].UpdateState(outletFlow, t, p, z, thermo);
        }
    }
}
