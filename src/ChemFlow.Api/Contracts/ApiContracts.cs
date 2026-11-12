using System.ComponentModel;

namespace ChemFlow.Api.Contracts;

public sealed record CompoundSummaryDto(
    string Id,
    string Name,
    string Formula,
    double MolarMass,
    double CriticalTemperatureKelvin,
    double CriticalPressureKpa,
    double NormalBoilingPointKelvin,
    double NormalBoilingPointCelsius
);

public sealed record ThermodynamicModelDto(
    string Id,
    string Name,
    string Description
);

public sealed record FlashCalculationRequest(
    [property: DefaultValue(new[] { "C6H6", "C7H8" })] IReadOnlyList<string> CompoundIds,
    [property: DefaultValue(new[] { 0.5, 0.5 })] IReadOnlyList<double> CompositionZ,
    [property: DefaultValue(368.15)] double TemperatureKelvin = 368.15,
    [property: DefaultValue(101.325)] double PressureKpa = 101.325,
    [property: DefaultValue("Peng-Robinson")] string? Model = "Peng-Robinson",
    string? InteractionPreset = null
);

public sealed record FlashCalculationResponse(
    string ThermodynamicModel,
    string Phase,
    double TemperatureKelvin,
    double TemperatureCelsius,
    double PressureKpa,
    double VaporFractionBeta,
    IReadOnlyList<string> CompoundIds,
    IReadOnlyList<double> OverallCompositionZ,
    IReadOnlyList<double> LiquidCompositionX,
    IReadOnlyList<double> VaporCompositionY,
    IReadOnlyList<double> KValues,
    double MolarEnthalpyJPerMol,
    int Iterations
);

public sealed record BinaryTxyDiagramRequest(
    [property: DefaultValue("C6H6")] string LightCompoundId = "C6H6",
    [property: DefaultValue("C7H8")] string HeavyCompoundId = "C7H8",
    [property: DefaultValue(101.325)] double PressureKpa = 101.325,
    [property: DefaultValue(11)] int Steps = 11,
    [property: DefaultValue("Peng-Robinson")] string? Model = "Peng-Robinson"
);

public sealed record TxyDiagramPointDto(
    double MoleFractionLight,
    double BubbleTemperatureKelvin,
    double DewTemperatureKelvin,
    double EquilibriumVaporFractionLightY
);

public sealed record BenchmarkSimulationRequest(
    [property: DefaultValue(50.0)] double BenzeneFeedFlowMolPerSec = 50.0,
    [property: DefaultValue(300.0)] double BenzeneFeedTemperatureKelvin = 300.0,
    [property: DefaultValue(50.0)] double TolueneFeedFlowMolPerSec = 50.0,
    [property: DefaultValue(310.0)] double TolueneFeedTemperatureKelvin = 310.0,
    [property: DefaultValue(368.15)] double PreheaterTargetTemperatureKelvin = 368.15,
    [property: DefaultValue(101.325)] double FlashDrumPressureKpa = 101.325
);

public sealed record RecycleSimulationRequest(
    [property: DefaultValue(50.0)] double FreshFeedFlowMolPerSec = 50.0,
    [property: DefaultValue(320.0)] double FreshFeedTemperatureKelvin = 320.0,
    [property: DefaultValue(0.60)] double BenzeneMoleFraction = 0.60,
    [property: DefaultValue(365.0)] double FlashDrumTemperatureKelvin = 365.0,
    [property: DefaultValue(101.325)] double FlashDrumPressureKpa = 101.325,
    [property: DefaultValue(0.65)] double RecycleFraction = 0.65,
    [property: DefaultValue("Wegstein")] string Method = "Wegstein",
    [property: DefaultValue(1e-5)] double Tolerance = 1e-5
);

public sealed record StreamStateDto(
    string Id,
    string Name,
    string Phase,
    double MolarFlowRateMolPerSec,
    double TemperatureKelvin,
    double TemperatureCelsius,
    double PressureKpa,
    double VaporFractionBeta,
    double MolarEnthalpyJPerMol,
    double TotalEnthalpyFlowKilowatts,
    IReadOnlyDictionary<string, double> MoleFractions
);

public sealed record UnitResultDto(
    string Id,
    string Name,
    string UnitType,
    double EnergyDutyKilowatts
);

public sealed record PlantSimulationResponse(
    long SavedSimulationRunId,
    string FlowsheetName,
    string ThermodynamicModel,
    IReadOnlyList<string> TopologicalExecutionOrder,
    IReadOnlyList<UnitResultDto> Units,
    IReadOnlyList<StreamStateDto> Streams,
    bool IsMassConserved = true,
    bool IsEnergyConserved = true,
    double MassBalanceRelativeError = 0.0,
    double EnergyBalanceRelativeError = 0.0,
    string? TopologyUrl = null,
    string? BalanceReportUrl = null,
    bool HasRecycle = false,
    int RecycleIterations = 1,
    IReadOnlyList<string>? TearStreams = null
);

public sealed record DistillationSimulationRequest(
    [property: DefaultValue(50.0)] double FeedFlowMolPerSec = 50.0,
    [property: DefaultValue(340.0)] double FeedTemperatureKelvin = 340.0,
    [property: DefaultValue(0.50)] double BenzeneMoleFraction = 0.50,
    [property: DefaultValue(10)] int NumberOfStages = 10,
    [property: DefaultValue(5)] int FeedStage = 5,
    [property: DefaultValue(2.0)] double RefluxRatio = 2.0,
    [property: DefaultValue(25.0)] double DistillateRateMolPerSec = 25.0,
    [property: DefaultValue(101.325)] double ColumnPressureKpa = 101.325,
    [property: DefaultValue("Peng-Robinson")] string? Model = "Peng-Robinson"
);

public sealed record DistillationStageProfileDto(
    int StageIndex,
    double TemperatureKelvin,
    double TemperatureCelsius,
    double PressureKpa,
    double LiquidFlowMolPerSec,
    double VaporFlowMolPerSec,
    IReadOnlyDictionary<string, double> LiquidCompositionX,
    IReadOnlyDictionary<string, double> VaporCompositionY
);

public sealed record DistillationSimulationResponse(
    long SavedSimulationRunId,
    string ColumnId,
    string ColumnName,
    string ThermodynamicModel,
    int NumberOfStages,
    int FeedStage,
    double RefluxRatio,
    int Iterations,
    bool IsConverged,
    double CondenserDutyKilowatts,
    double ReboilerDutyKilowatts,
    double NetEnergyDutyKilowatts,
    bool IsMassConserved,
    bool IsEnergyConserved,
    double MassBalanceRelativeError,
    double EnergyBalanceRelativeError,
    IReadOnlyList<UnitResultDto> Units,
    IReadOnlyList<StreamStateDto> Streams,
    StreamStateDto Distillate,
    StreamStateDto Bottoms,
    IReadOnlyList<DistillationStageProfileDto> StageProfiles,
    string? TopologyUrl = null,
    string? BalanceReportUrl = null
);

public sealed record DistillationIterationUpdateDto(
    int Iteration,
    double MaxDeltaT,
    double RelativeError,
    IReadOnlyList<double> StageTemperaturesCelsius
);

public sealed record CstrStreamRequest(
    [property: DefaultValue(100.0)] double VolumetricFlowLPerMin = 100.0,
    [property: DefaultValue(300.0)] double CoolantTemperatureKelvin = 300.0,
    [property: DefaultValue(1.0)] double FeedConcentrationMolPerL = 1.0,
    [property: DefaultValue(300.0)] double FeedTemperatureKelvin = 300.0,
    [property: DefaultValue(100.0)] double ReactorVolumeLiters = 100.0,
    [property: DefaultValue(120)] int MaxFrames = 120,
    [property: DefaultValue(150)] int IntervalMs = 150
);

public sealed record CstrTelemetryFrameDto(
    int FrameIndex,
    double TimeSeconds,
    double ReactantConcentrationMolPerL,
    double TemperatureKelvin,
    double TemperatureCelsius,
    double ConversionPct,
    double ReactionRateMolPerLMin,
    double CoolantTemperatureKelvin,
    double CoolingDutyKilowatts,
    bool IsThermalRunaway,
    string Status,
    string EngineMode
);

public sealed record TransportPropertiesRequest(
    [property: DefaultValue(new[] { "C6H6", "C7H8" })] IReadOnlyList<string> CompoundIds,
    [property: DefaultValue(new[] { 0.5, 0.5 })] IReadOnlyList<double> CompositionZ,
    [property: DefaultValue(298.15)] double TemperatureKelvin = 298.15,
    [property: DefaultValue(101.325)] double PressureKpa = 101.325,
    [property: DefaultValue("Chung")] string? Package = "Chung"
);

public sealed record TransportPropertiesResponse(
    string ModelName,
    string Phase,
    double TemperatureKelvin,
    double TemperatureCelsius,
    double PressureKpa,
    double DynamicViscosityPaS,
    double DynamicViscosityCp,
    double KinematicViscosityM2PerS,
    double KinematicViscosityCSt,
    double ThermalConductivityWPerMK,
    double DensityKgPerM3,
    double PrandtlNumber
);

public sealed record CstrBatterySimulationRequest(
    [property: DefaultValue(8)] int ReactorCount = 8,
    [property: DefaultValue(100.0)] double TotalFeedFlowMolPerSec = 100.0,
    [property: DefaultValue(350.0)] double FeedTemperatureKelvin = 350.0,
    [property: DefaultValue(150.0)] double FeedPressureKpa = 150.0,
    [property: DefaultValue("C6H6")] string ReactantCompoundId = "C6H6",
    [property: DefaultValue("C7H8")] string ProductCompoundId = "C7H8",
    [property: DefaultValue(new[] { 0.80, 0.20 })] IReadOnlyList<double>? CompositionZ = null,
    [property: DefaultValue(100.0)] double ReactorVolumeLiters = 100.0,
    [property: DefaultValue(300.0)] double CoolantTemperatureKelvin = 300.0,
    [property: DefaultValue(666.67)] double CoolingUaWattsPerKelvin = 666.67,
    [property: DefaultValue(400.0)] double MaxSafeTemperatureKelvin = 400.0,
    [property: DefaultValue("Chung")] string? TransportPackage = "Chung"
);

public sealed record CstrBatteryReactorStateDto(
    int ReactorIndex,
    double ReactantConcentrationMolPerL,
    double TemperatureKelvin,
    double TemperatureCelsius,
    double ConversionPct,
    double ReactionRateMolPerLMin,
    bool IsRunaway,
    double DynamicViscosityCp,
    double ThermalConductivityWPerMK,
    double PrandtlNumber
);

public sealed record CstrBatterySimulationResponse(
    string BatteryId,
    string Name,
    int ReactorCount,
    double MeanConversionPct,
    double MeanTemperatureKelvin,
    double MeanTemperatureCelsius,
    double MeanOutletConcentrationMolPerL,
    bool HasThermalRunaway,
    bool IsUsingNativeSimd,
    double EnergyDutyKilowatts,
    IReadOnlyList<CstrBatteryReactorStateDto> ReactorStates
);
