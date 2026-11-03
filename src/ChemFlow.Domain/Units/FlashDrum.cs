using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Vapor-Liquid Equilibrium separator vessel (Flash Drum).
/// Flashes the feed stream at (OperatingTemperatureKelvin, OperatingPressureKpa)
/// and splits the phases into a saturated overhead VaporOutlet and bottom LiquidOutlet.
/// </summary>
public sealed class FlashDrum : UnitOperationBase
{
    public override string UnitType => "FlashDrum";
    public MaterialStream FeedStream => InletStreams[0];
    public MaterialStream VaporOutlet => OutletStreams[0];
    public MaterialStream LiquidOutlet => OutletStreams[1];

    public double? OperatingTemperatureKelvin { get; }
    public double? OperatingPressureKpa { get; }

    public FlashDrum(
        string id,
        string name,
        MaterialStream feedStream,
        MaterialStream vaporOutlet,
        MaterialStream liquidOutlet,
        double? operatingTemperatureKelvin = null,
        double? operatingPressureKpa = null)
        : base(id, name, [feedStream], [vaporOutlet, liquidOutlet])
    {
        OperatingTemperatureKelvin = operatingTemperatureKelvin;
        OperatingPressureKpa = operatingPressureKpa;
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        double drumT = OperatingTemperatureKelvin ?? FeedStream.TemperatureKelvin;
        double drumP = OperatingPressureKpa ?? FeedStream.PressureKpa;

        FlashResult flash = thermo.CalculateIsothermalFlash(
            FeedStream.Compounds,
            FeedStream.CompositionZ,
            drumT,
            drumP);

        double vaporFlow = FeedStream.MolarFlowRateMolPerSec * flash.VaporFractionBeta;
        double liquidFlow = FeedStream.MolarFlowRateMolPerSec * (1.0 - flash.VaporFractionBeta);

        VaporOutlet.UpdateState(vaporFlow, drumT, drumP, flash.VaporCompositionY, thermo);
        LiquidOutlet.UpdateState(liquidFlow, drumT, drumP, flash.LiquidCompositionX, thermo);

        EnergyDutyWatts =
            (VaporOutlet.TotalEnthalpyFlowWatts + LiquidOutlet.TotalEnthalpyFlowWatts) -
            FeedStream.TotalEnthalpyFlowWatts;
    }
}
