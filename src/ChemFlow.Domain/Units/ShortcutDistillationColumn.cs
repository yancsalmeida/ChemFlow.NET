using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Mathematics;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Shortcut Multicomponent Distillation Column (DSTWU) implementing the Fenske-Underwood-Gilliland-Kirkbride (FUG)
/// design method. Calculates minimum reflux R_min, minimum stages N_min, actual theoretical stages N,
/// optimal feed stage location N_feed, product splits, thermal duties, and column diameter.
/// 
/// Can automatically instantiate and parameterize a rigorous stage-by-stage MESH <see cref="DistillationColumn"/>.
/// </summary>
public sealed class ShortcutDistillationColumn : UnitOperationBase
{
    public override string UnitType => "ShortcutDistillationColumn";

    public MaterialStream FeedStream => InletStreams[0];
    public MaterialStream DistillateOutlet => OutletStreams[0];
    public MaterialStream BottomsOutlet => OutletStreams[1];

    public int LightKeyIndex { get; }
    public int HeavyKeyIndex { get; }
    public double LightKeyRecoveryDistillate { get; }
    public double HeavyKeyRecoveryBottoms { get; }
    public double? RefluxRatioMultiplier { get; }
    public double? FixedRefluxRatio { get; }
    public double? ColumnPressureKpa { get; }
    public GillilandCorrelationType Correlation { get; }

    public FugDesignResult? DesignResult { get; private set; }

    public double MinimumStagesNmin => DesignResult?.MinimumStagesNmin ?? 0.0;
    public double MinimumRefluxRatioRmin => DesignResult?.MinimumRefluxRatioRmin ?? 0.0;
    public double OperatingRefluxRatio => DesignResult?.OperatingRefluxRatio ?? 0.0;
    public double TheoreticalStagesN => DesignResult?.TheoreticalStagesN ?? 0.0;
    public int RecommendedNumberOfStages => DesignResult?.RecommendedNumberOfStages ?? 0;
    public int OptimalFeedStage => DesignResult?.OptimalFeedStage ?? 0;
    public double CondenserDutyWatts => DesignResult?.CondenserDutyWatts ?? 0.0;
    public double ReboilerDutyWatts => DesignResult?.ReboilerDutyWatts ?? 0.0;
    public double EstimatedDiameterMeters => DesignResult?.EstimatedDiameterMeters ?? 0.0;

    public ShortcutDistillationColumn(
        string id,
        string name,
        MaterialStream feedStream,
        MaterialStream distillateOutlet,
        MaterialStream bottomsOutlet,
        int lightKeyIndex,
        int heavyKeyIndex,
        double lightKeyRecoveryDistillate = 0.98,
        double heavyKeyRecoveryBottoms = 0.98,
        double? refluxRatioMultiplier = 1.25,
        double? fixedRefluxRatio = null,
        double? columnPressureKpa = null,
        GillilandCorrelationType correlation = GillilandCorrelationType.Molokanov)
        : base(id, name, [feedStream], [distillateOutlet, bottomsOutlet])
    {
        if (lightKeyRecoveryDistillate <= 0.0 || lightKeyRecoveryDistillate >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(lightKeyRecoveryDistillate), "Light key recovery in distillate must be in (0, 1).");
        if (heavyKeyRecoveryBottoms <= 0.0 || heavyKeyRecoveryBottoms >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(heavyKeyRecoveryBottoms), "Heavy key recovery in bottoms must be in (0, 1).");
        if (refluxRatioMultiplier.HasValue && refluxRatioMultiplier.Value <= 1.0)
            throw new ArgumentOutOfRangeException(nameof(refluxRatioMultiplier), "Reflux ratio multiplier must be strictly greater than 1.0.");

        LightKeyIndex = lightKeyIndex;
        HeavyKeyIndex = heavyKeyIndex;
        LightKeyRecoveryDistillate = lightKeyRecoveryDistillate;
        HeavyKeyRecoveryBottoms = heavyKeyRecoveryBottoms;
        RefluxRatioMultiplier = refluxRatioMultiplier;
        FixedRefluxRatio = fixedRefluxRatio;
        ColumnPressureKpa = columnPressureKpa;
        Correlation = correlation;
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        IReadOnlyList<ChemicalCompound> compounds = FeedStream.Compounds;
        double pCol = ColumnPressureKpa ?? FeedStream.PressureKpa;

        // Perform FUG calculation
        DesignResult = FugDistillationDesigner.Design(
            compounds: compounds,
            zFeed: FeedStream.CompositionZ,
            totalFeedFlow: FeedStream.MolarFlowRateMolPerSec,
            feedTemperatureK: FeedStream.TemperatureKelvin,
            columnPressureKpa: pCol,
            feedVaporFraction: FeedStream.VaporFractionBeta,
            lightKeyIndex: LightKeyIndex,
            heavyKeyIndex: HeavyKeyIndex,
            lightKeyRecoveryDistillate: LightKeyRecoveryDistillate,
            heavyKeyRecoveryBottoms: HeavyKeyRecoveryBottoms,
            refluxRatioMultiplier: RefluxRatioMultiplier,
            fixedRefluxRatio: FixedRefluxRatio,
            thermo: thermo,
            correlation: Correlation
        );

        // Update Outlet Streams
        DistillateOutlet.UpdateState(
            molarFlowRateMolPerSec: DesignResult.DistillateRateMolPerSec,
            temperatureKelvin: DesignResult.TopTemperatureKelvin,
            pressureKpa: pCol,
            compositionZ: DesignResult.DistillateCompositionX,
            thermo: thermo
        );

        BottomsOutlet.UpdateState(
            molarFlowRateMolPerSec: DesignResult.BottomsRateMolPerSec,
            temperatureKelvin: DesignResult.BottomTemperatureKelvin,
            pressureKpa: pCol,
            compositionZ: DesignResult.BottomsCompositionX,
            thermo: thermo
        );

        // Overall energy duty: Q_net = Q_reboiler + Q_condenser
        EnergyDutyWatts = DesignResult.ReboilerDutyWatts + DesignResult.CondenserDutyWatts;
    }

    /// <summary>
    /// Instantiates and parameterizes a rigorous stage-by-stage MESH <see cref="DistillationColumn"/>
    /// directly from this shortcut design.
    /// </summary>
    public DistillationColumn ToRigorousColumn(
        string? id = null,
        string? name = null,
        MaterialStream? distillateOutlet = null,
        MaterialStream? bottomsOutlet = null,
        DistillationColumnOptions? options = null)
    {
        if (DesignResult == null)
            throw new InvalidOperationException("Shortcut column must be solved before generating a rigorous column.");

        string colId = id ?? $"{Id}-RIGOROUS";
        string colName = name ?? $"{Name} (Rigorous MESH)";
        var distOut = distillateOutlet ?? DistillateOutlet;
        var btmOut = bottomsOutlet ?? BottomsOutlet;

        return new DistillationColumn(
            id: colId,
            name: colName,
            feedStream: FeedStream,
            distillateOutlet: distOut,
            bottomsOutlet: btmOut,
            numberOfStages: DesignResult.RecommendedNumberOfStages,
            feedStage: DesignResult.OptimalFeedStage,
            refluxRatio: DesignResult.OperatingRefluxRatio,
            distillateRateMolPerSec: DesignResult.DistillateRateMolPerSec,
            columnPressureKpa: ColumnPressureKpa,
            options: options
        );
    }
}
