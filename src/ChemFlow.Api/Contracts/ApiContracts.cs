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

public sealed record FlashCalculationRequest(
    IReadOnlyList<string> CompoundIds,
    IReadOnlyList<double> CompositionZ,
    double TemperatureKelvin,
    double PressureKpa
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
    string LightCompoundId,
    string HeavyCompoundId,
    double PressureKpa = 101.325,
    int Steps = 11
);

public sealed record TxyDiagramPointDto(
    double MoleFractionLight,
    double BubbleTemperatureKelvin,
    double DewTemperatureKelvin,
    double EquilibriumVaporFractionLightY
);

public sealed record BenchmarkSimulationRequest(
    double BenzeneFeedFlowMolPerSec = 50.0,
    double BenzeneFeedTemperatureKelvin = 300.0,
    double TolueneFeedFlowMolPerSec = 50.0,
    double TolueneFeedTemperatureKelvin = 310.0,
    double PreheaterTargetTemperatureKelvin = 368.15,
    double FlashDrumPressureKpa = 101.325
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
    IReadOnlyList<StreamStateDto> Streams
);
