using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Utility heat exchanger (Heater / Cooler) that brings a stream to a specified target temperature
/// with an optional pressure drop, computing the required thermal duty Q_dot = H_dot_out - H_dot_in.
/// </summary>
public sealed class HeaterCooler : UnitOperationBase
{
    public override string UnitType => "HeaterCooler";
    public MaterialStream InletStream => InletStreams[0];
    public MaterialStream OutletStream => OutletStreams[0];

    public double TargetTemperatureKelvin { get; }
    public double PressureDropKpa { get; }

    public HeaterCooler(
        string id,
        string name,
        MaterialStream inletStream,
        MaterialStream outletStream,
        double targetTemperatureKelvin,
        double pressureDropKpa = 0.0)
        : base(id, name, [inletStream], [outletStream])
    {
        if (targetTemperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(targetTemperatureKelvin), "Target temperature must be > 0 K.");
        if (pressureDropKpa < 0.0)
            throw new ArgumentOutOfRangeException(nameof(pressureDropKpa), "Pressure drop cannot be negative.");

        TargetTemperatureKelvin = targetTemperatureKelvin;
        PressureDropKpa = pressureDropKpa;
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        double outletPressureKpa = InletStream.PressureKpa - PressureDropKpa;
        if (outletPressureKpa <= 0.0)
            throw new InvalidOperationException($"Pressure drop ({PressureDropKpa} kPa) exceeds inlet pressure ({InletStream.PressureKpa} kPa).");

        OutletStream.UpdateState(
            molarFlowRateMolPerSec: InletStream.MolarFlowRateMolPerSec,
            temperatureKelvin: TargetTemperatureKelvin,
            pressureKpa: outletPressureKpa,
            compositionZ: InletStream.CompositionZ,
            thermo: thermo);

        EnergyDutyWatts = OutletStream.TotalEnthalpyFlowWatts - InletStream.TotalEnthalpyFlowWatts;
    }
}
