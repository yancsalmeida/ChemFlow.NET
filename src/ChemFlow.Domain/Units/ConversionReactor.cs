using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Stoichiometric Conversion Reactor solving mole balances via the extent of reaction ξ:
///   n_dot_out,i = n_dot_in,i + ν_i * ξ
/// where ξ = (n_dot_in,key * X) / |ν_key| and Q_dot = H_dot_out - H_dot_in + ξ * ΔH_rxn°.
/// </summary>
public sealed class ConversionReactor : UnitOperationBase
{
    public override string UnitType => "ConversionReactor";
    public MaterialStream InletStream => InletStreams[0];
    public MaterialStream OutletStream => OutletStreams[0];

    public string KeyReactantId { get; }
    public double FractionalConversion { get; }
    public IReadOnlyDictionary<string, double> Stoichiometry { get; }
    public double StandardReactionEnthalpyJPerMol { get; }
    public double OutletTemperatureKelvin { get; }

    public ConversionReactor(
        string id,
        string name,
        MaterialStream inletStream,
        MaterialStream outletStream,
        string keyReactantId,
        double fractionalConversion,
        IReadOnlyDictionary<string, double> stoichiometry,
        double outletTemperatureKelvin,
        double standardReactionEnthalpyJPerMol = 0.0)
        : base(id, name, [inletStream], [outletStream])
    {
        if (string.IsNullOrWhiteSpace(keyReactantId))
            throw new ArgumentException("Key reactant ID cannot be empty.", nameof(keyReactantId));
        if (fractionalConversion is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(fractionalConversion), "Fractional conversion X must be in [0, 1].");
        if (stoichiometry is null || !stoichiometry.TryGetValue(keyReactantId, out double nuKey) || nuKey >= 0.0)
            throw new ArgumentException("Stoichiometry must contain a strictly negative coefficient for the key reactant.", nameof(stoichiometry));
        if (outletTemperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(outletTemperatureKelvin), "Outlet temperature must be > 0 K.");

        KeyReactantId = keyReactantId;
        FractionalConversion = fractionalConversion;
        Stoichiometry = stoichiometry;
        OutletTemperatureKelvin = outletTemperatureKelvin;
        StandardReactionEnthalpyJPerMol = standardReactionEnthalpyJPerMol;
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        IReadOnlyList<ChemicalCompound> compounds = InletStream.Compounds;
        int n = compounds.Count;

        int keyIndex = -1;
        for (int i = 0; i < n; i++)
        {
            if (compounds[i].Id == KeyReactantId)
            {
                keyIndex = i;
                break;
            }
        }

        if (keyIndex < 0)
            throw new InvalidOperationException($"Key reactant '{KeyReactantId}' is not present in stream compounds.");

        double keyMolarFlowIn = InletStream.MolarFlowRateMolPerSec * InletStream.CompositionZ[keyIndex];
        double nuKeyAbs = Math.Abs(Stoichiometry[KeyReactantId]);

        // Extent of reaction ξ (in mol/s)
        double extentMolPerSec = (keyMolarFlowIn * FractionalConversion) / nuKeyAbs;

        double[] outletComponentFlows = new double[n];
        double totalOutletFlow = 0.0;

        for (int i = 0; i < n; i++)
        {
            double flowIn = InletStream.MolarFlowRateMolPerSec * InletStream.CompositionZ[i];
            double nu = Stoichiometry.TryGetValue(compounds[i].Id, out double coeff) ? coeff : 0.0;
            double flowOut = flowIn + nu * extentMolPerSec;

            if (flowOut < -1e-9)
                throw new InvalidOperationException($"Limiting reactant '{compounds[i].Name}' was completely depleted (negative molar flow).");

            outletComponentFlows[i] = Math.Max(0.0, flowOut);
            totalOutletFlow += outletComponentFlows[i];
        }

        double[] zOut = new double[n];
        for (int i = 0; i < n; i++)
        {
            zOut[i] = totalOutletFlow > 0.0 ? outletComponentFlows[i] / totalOutletFlow : 1.0 / n;
        }

        OutletStream.UpdateState(
            molarFlowRateMolPerSec: totalOutletFlow,
            temperatureKelvin: OutletTemperatureKelvin,
            pressureKpa: InletStream.PressureKpa,
            compositionZ: zOut,
            thermo: thermo);

        EnergyDutyWatts =
            (OutletStream.TotalEnthalpyFlowWatts - InletStream.TotalEnthalpyFlowWatts) +
            extentMolPerSec * StandardReactionEnthalpyJPerMol;
    }
}
