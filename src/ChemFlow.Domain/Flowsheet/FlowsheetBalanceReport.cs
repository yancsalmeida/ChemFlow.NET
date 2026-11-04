namespace ChemFlow.Domain.Flowsheet;

/// <summary>
/// Overall plant mass and energy balance summary evaluating conservation across plant boundary feeds and products.
/// </summary>
public sealed record GlobalBalanceSummary(
    double TotalFeedMolarFlowMolPerSec,
    double TotalProductMolarFlowMolPerSec,
    double MolarFlowDeltaMolPerSec,
    double RelativeMolarError,
    double TotalFeedMassFlowKgPerSec,
    double TotalProductMassFlowKgPerSec,
    double MassFlowDeltaKgPerSec,
    double RelativeMassError,
    double TotalFeedEnthalpyWatts,
    double TotalProductEnthalpyWatts,
    double TotalHeatDutyAddedWatts,
    double EnthalpyDeltaWatts,
    double RelativeEnthalpyError,
    bool IsMassBalanced,
    bool IsEnergyBalanced
);

/// <summary>
/// Component-level species conservation balance comparing overall feed vs product flow rates.
/// </summary>
public sealed record ComponentBalanceSummary(
    string CompoundId,
    string CompoundName,
    double MolarMassGPerMol,
    double FeedMolarFlowMolPerSec,
    double ProductMolarFlowMolPerSec,
    double MolarDeltaMolPerSec,
    double FeedMassFlowKgPerSec,
    double ProductMassFlowKgPerSec,
    double MassDeltaKgPerSec,
    double RelativeMassError,
    bool IsConserved
);

/// <summary>
/// Local mass and energy balance closure around an individual equipment block.
/// </summary>
public sealed record UnitBalanceSummary(
    string UnitId,
    string UnitName,
    string UnitType,
    double InletMassFlowKgPerSec,
    double OutletMassFlowKgPerSec,
    double MassDeltaKgPerSec,
    double RelativeMassError,
    double InletEnthalpyWatts,
    double OutletEnthalpyWatts,
    double HeatDutyWatts,
    double EnthalpyDeltaWatts,
    double RelativeEnthalpyError,
    bool IsMassBalanced,
    bool IsEnergyBalanced
);

/// <summary>
/// Stream summary contributing to overall boundary balances.
/// </summary>
public sealed record StreamBalanceSummary(
    string StreamId,
    string StreamName,
    string Direction,
    double MolarFlowMolPerSec,
    double MassFlowKgPerSec,
    double TemperatureKelvin,
    double PressureKpa,
    string Phase,
    double EnthalpyWatts
);

/// <summary>
/// Comprehensive mass and energy balance closure report for a solved process flowsheet.
/// Validates overall plant conservation, component-level balances, and individual equipment closure.
/// </summary>
public sealed record FlowsheetBalanceReport(
    string FlowsheetName,
    string ThermodynamicModel,
    DateTime GeneratedAtUtc,
    GlobalBalanceSummary GlobalBalance,
    IReadOnlyList<ComponentBalanceSummary> ComponentBalances,
    IReadOnlyList<UnitBalanceSummary> UnitBalances,
    IReadOnlyList<StreamBalanceSummary> ExternalFeeds,
    IReadOnlyList<StreamBalanceSummary> FinalProducts,
    bool IsMassConserved,
    bool IsEnergyConserved
);
