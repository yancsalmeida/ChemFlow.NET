using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Streams;

/// <summary>
/// Represents a material stream (pipe) carrying chemical species between unit operations.
/// </summary>
public sealed class MaterialStream
{
    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<ChemicalCompound> Compounds { get; }

    /// <summary>Total molar flow rate F in mol/s.</summary>
    public double MolarFlowRateMolPerSec { get; private set; }

    /// <summary>Stream temperature T in Kelvin (K).</summary>
    public double TemperatureKelvin { get; private set; }

    /// <summary>Stream pressure P in kilopascals (kPa).</summary>
    public double PressureKpa { get; private set; }

    /// <summary>Overall mole fraction vector z_i (sums to 1.0).</summary>
    public IReadOnlyList<double> CompositionZ { get; private set; }

    /// <summary>Converged phase equilibrium state after flashing at (T, P).</summary>
    public FlashResult? EquilibriumState { get; private set; }

    public bool IsSolved => EquilibriumState is not null;

    public PhaseState Phase =>
        EquilibriumState?.Phase ?? throw new InvalidOperationException($"Stream '{Name}' has not been solved yet.");

    public double VaporFractionBeta =>
        EquilibriumState?.VaporFractionBeta ?? throw new InvalidOperationException($"Stream '{Name}' has not been solved yet.");

    public double MolarEnthalpyJPerMol =>
        EquilibriumState?.MolarEnthalpyJPerMol ?? throw new InvalidOperationException($"Stream '{Name}' has not been solved yet.");

    /// <summary>Total enthalpy flow rate H_dot = F * H in Watts (J/s).</summary>
    public double TotalEnthalpyFlowWatts => MolarFlowRateMolPerSec * MolarEnthalpyJPerMol;

    public MaterialStream(
        string id,
        string name,
        IReadOnlyList<ChemicalCompound> compounds,
        double molarFlowRateMolPerSec = 0.0,
        double temperatureKelvin = 298.15,
        double pressureKpa = 101.325,
        IReadOnlyList<double>? compositionZ = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Stream ID cannot be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Stream name cannot be empty.", nameof(name));
        if (compounds is null || compounds.Count == 0)
            throw new ArgumentException("Compounds list cannot be empty.", nameof(compounds));
        if (molarFlowRateMolPerSec < 0.0)
            throw new ArgumentOutOfRangeException(nameof(molarFlowRateMolPerSec), "Molar flow rate cannot be negative.");

        Id = id.Trim();
        Name = name.Trim();
        Compounds = compounds;
        MolarFlowRateMolPerSec = molarFlowRateMolPerSec;
        TemperatureKelvin = temperatureKelvin;
        PressureKpa = pressureKpa;

        if (compositionZ is not null)
        {
            if (compositionZ.Count != compounds.Count)
                throw new ArgumentException("Composition length must match compounds count.", nameof(compositionZ));
            CompositionZ = compositionZ.ToArray();
        }
        else
        {
            double[] uniform = new double[compounds.Count];
            Array.Fill(uniform, 1.0 / compounds.Count);
            CompositionZ = uniform;
        }
    }

    /// <summary>
    /// Updates the stream thermodynamic state (F, T, P, z) and flashes it using the provided thermodynamic package.
    /// </summary>
    public void UpdateState(
        double molarFlowRateMolPerSec,
        double temperatureKelvin,
        double pressureKpa,
        IReadOnlyList<double> compositionZ,
        IThermodynamicPackage thermo)
    {
        ArgumentNullException.ThrowIfNull(thermo);
        if (molarFlowRateMolPerSec < 0.0)
            throw new ArgumentOutOfRangeException(nameof(molarFlowRateMolPerSec), "Molar flow rate cannot be negative.");
        if (compositionZ is null || compositionZ.Count != Compounds.Count)
            throw new ArgumentException("Composition length must match compounds count.", nameof(compositionZ));

        MolarFlowRateMolPerSec = molarFlowRateMolPerSec;
        TemperatureKelvin = temperatureKelvin;
        PressureKpa = pressureKpa;
        CompositionZ = compositionZ.ToArray();

        EquilibriumState = thermo.CalculateIsothermalFlash(Compounds, CompositionZ, TemperatureKelvin, PressureKpa);
    }

    /// <summary>
    /// Solves the phase equilibrium of the stream using its current (T, P, z) specifications.
    /// </summary>
    public void SolveEquilibrium(IThermodynamicPackage thermo)
    {
        ArgumentNullException.ThrowIfNull(thermo);
        EquilibriumState = thermo.CalculateIsothermalFlash(Compounds, CompositionZ, TemperatureKelvin, PressureKpa);
    }
}
