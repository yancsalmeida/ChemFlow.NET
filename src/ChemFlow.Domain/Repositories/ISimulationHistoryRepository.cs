using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Units;

namespace ChemFlow.Domain.Repositories;

public sealed record StreamResultRecord(
    string StreamId,
    string StreamName,
    string Phase,
    double MolarFlowRateMolPerSec,
    double TemperatureKelvin,
    double PressureKpa,
    double VaporFractionBeta,
    double MolarEnthalpyJPerMol
);

public sealed record SimulationRunRecord(
    long Id,
    string FlowsheetName,
    string ThermodynamicModel,
    DateTime ExecutedAtUtc,
    IReadOnlyList<string> UnitExecutionOrder,
    IReadOnlyList<StreamResultRecord> Streams,
    string? TopologyDot = null,
    string? TopologyJson = null,
    string? BalanceReportJson = null
);

/// <summary>
/// Repository contract for persisting and retrieving historical process simulation runs.
/// </summary>
public interface ISimulationHistoryRepository
{
    long SaveSimulationRun(ProcessFlowsheet flowsheet, IReadOnlyList<IUnitOperation> executedOrder);
    SimulationRunRecord? GetById(long runId);
    IReadOnlyList<SimulationRunRecord> GetRecentRuns(int limit = 20);
    FlowsheetTopology? GetTopology(long runId);
    FlowsheetBalanceReport? GetBalanceReport(long runId);
}
