using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Adiabatic Mixer combining multiple inlet streams into a single outlet stream.
/// Solves overall mass balance, component mole balances, pressure rule (P_out = min P_in),
/// and enthalpy balance (H_dot_out = Σ H_dot_in) to find the outlet equilibrium temperature.
/// </summary>
public sealed class Mixer : UnitOperationBase
{
    public override string UnitType => "Mixer";
    public MaterialStream OutletStream => OutletStreams[0];

    public Mixer(string id, string name, IReadOnlyList<MaterialStream> inletStreams, MaterialStream outletStream)
        : base(id, name, inletStreams, [outletStream])
    {
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        IReadOnlyList<ChemicalCompound> compounds = OutletStream.Compounds;
        int n = compounds.Count;

        double totalMolarFlow = 0.0;
        double totalEnthalpyWatts = 0.0;
        double minPressureKpa = double.MaxValue;
        double[] componentFlows = new double[n];

        foreach (MaterialStream inlet in InletStreams)
        {
            totalMolarFlow += inlet.MolarFlowRateMolPerSec;
            totalEnthalpyWatts += inlet.TotalEnthalpyFlowWatts;

            if (inlet.PressureKpa < minPressureKpa)
                minPressureKpa = inlet.PressureKpa;

            for (int i = 0; i < n; i++)
            {
                componentFlows[i] += inlet.MolarFlowRateMolPerSec * inlet.CompositionZ[i];
            }
        }

        double[] zOut = new double[n];
        if (totalMolarFlow > 0.0)
        {
            for (int i = 0; i < n; i++)
                zOut[i] = componentFlows[i] / totalMolarFlow;
        }
        else
        {
            Array.Fill(zOut, 1.0 / n);
        }

        // Adiabatic mixer: Q_dot = 0 => target molar enthalpy H_target = H_dot_total / F_total
        EnergyDutyWatts = 0.0;
        double targetMolarEnthalpy = totalMolarFlow > 0.0
            ? totalEnthalpyWatts / totalMolarFlow
            : 0.0;

        // Find T_out via bracketed root-finding on H(T, P_out, z_out) - H_target = 0
        double minT = InletStreams.Min(s => s.TemperatureKelvin) - 30.0;
        double maxT = InletStreams.Max(s => s.TemperatureKelvin) + 30.0;
        minT = Math.Max(200.0, minT);

        double tLow = minT;
        double tHigh = maxT;
        double tMid = 0.5 * (tLow + tHigh);

        for (int iter = 0; iter < 50; iter++)
        {
            tMid = 0.5 * (tLow + tHigh);
            FlashResult flash = thermo.CalculateIsothermalFlash(compounds, zOut, tMid, minPressureKpa);
            double error = flash.MolarEnthalpyJPerMol - targetMolarEnthalpy;

            if (Math.Abs(error) <= 1e-3 || (tHigh - tLow) <= 1e-6)
                break;

            if (error < 0.0)
                tLow = tMid;
            else
                tHigh = tMid;
        }

        OutletStream.UpdateState(totalMolarFlow, tMid, minPressureKpa, zOut, thermo);
    }
}
