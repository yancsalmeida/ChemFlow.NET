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

public sealed record FlashCalculationRequest(
    [property: DefaultValue(new[] { "C6H6", "C7H8" })] IReadOnlyList<string> CompoundIds,
    [property: DefaultValue(new[] { 0.5, 0.5 })] IReadOnlyList<double> CompositionZ,
    [property: DefaultValue(368.15)] double TemperatureKelvin = 368.15,
    [property: DefaultValue(101.325)] double PressureKpa = 101.325
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
    [property: DefaultValue(11)] int Steps = 11
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
