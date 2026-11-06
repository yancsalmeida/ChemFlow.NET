namespace ChemFlow.Domain.Flowsheet;

/// <summary>
/// Represents a unit operation node in the flowsheet graph topology.
/// </summary>
public sealed record UnitTopologyNode(
    string Id,
    string Name,
    string UnitType,
    IReadOnlyList<string> InletStreamIds,
    IReadOnlyList<string> OutletStreamIds,
    double EnergyDutyWatts
);

/// <summary>
/// Represents a directed material stream connecting equipment nodes or plant boundary ports.
/// </summary>
public sealed record StreamTopologyEdge(
    string StreamId,
    string StreamName,
    string? SourceUnitId,
    string? TargetUnitId,
    bool IsExternalFeed,
    bool IsFinalProduct,
    double MolarFlowMolPerSec,
    double MassFlowKgPerSec,
    double TemperatureKelvin,
    double PressureKpa,
    string Phase,
    double VaporFractionBeta,
    double TotalEnthalpyFlowWatts,
    IReadOnlyDictionary<string, double> Composition,
    bool IsTearStream = false
);

/// <summary>
/// Complete structural topology of a chemical process flowsheet, including Graphviz DOT representation
/// and machine-readable node/edge connectivity metadata.
/// </summary>
public sealed record FlowsheetTopology(
    string FlowsheetName,
    IReadOnlyList<UnitTopologyNode> Units,
    IReadOnlyList<StreamTopologyEdge> Streams,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AdjacencyList,
    IReadOnlyList<string> ExecutionOrder,
    bool IsAcyclic,
    string DotGraph,
    IReadOnlyList<string>? TearStreamIds = null
);
