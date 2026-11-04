using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Units;
using ChemFlow.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace ChemFlow.Infrastructure.Repositories;

/// <summary>
/// Persists and queries historical process simulation runs and their stream states in SQLite.
/// Uses atomic database transactions (ACID) across the 1-to-Many parent/child tables.
/// </summary>
public sealed class SqliteSimulationHistoryRepository : ISimulationHistoryRepository
{
    private readonly SqliteDatabaseInitializer _db;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public SqliteSimulationHistoryRepository(SqliteDatabaseInitializer db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public long SaveSimulationRun(ProcessFlowsheet flowsheet, IReadOnlyList<IUnitOperation> executedOrder)
    {
        ArgumentNullException.ThrowIfNull(flowsheet);
        ArgumentNullException.ThrowIfNull(executedOrder);

        using SqliteConnection connection = _db.CreateOpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        string executedAtIso = DateTime.UtcNow.ToString("O");
        string orderCsv = string.Join("->", executedOrder.Select(u => u.Id));

        FlowsheetTopology topology = flowsheet.GetTopology();
        FlowsheetBalanceReport balanceReport = flowsheet.GenerateBalanceReport();
        string topologyDot = topology.DotGraph;
        string topologyJson = System.Text.Json.JsonSerializer.Serialize(topology, JsonOptions);
        string balanceReportJson = System.Text.Json.JsonSerializer.Serialize(balanceReport, JsonOptions);

        long runId;
        using (SqliteCommand insertRunCmd = connection.CreateCommand())
        {
            insertRunCmd.Transaction = transaction;
            insertRunCmd.CommandText = """
                INSERT INTO SimulationRuns (
                    FlowsheetName, ThermodynamicModel, ExecutedAtUtc, UnitExecutionOrder,
                    TopologyDot, TopologyJson, BalanceReportJson
                ) VALUES (
                    $name, $model, $executedAt, $order,
                    $topologyDot, $topologyJson, $balanceReportJson
                );
                SELECT last_insert_rowid();
                """;
            insertRunCmd.Parameters.AddWithValue("$name", flowsheet.Name);
            insertRunCmd.Parameters.AddWithValue("$model", flowsheet.ThermodynamicPackage.Name);
            insertRunCmd.Parameters.AddWithValue("$executedAt", executedAtIso);
            insertRunCmd.Parameters.AddWithValue("$order", orderCsv);
            insertRunCmd.Parameters.AddWithValue("$topologyDot", topologyDot);
            insertRunCmd.Parameters.AddWithValue("$topologyJson", topologyJson);
            insertRunCmd.Parameters.AddWithValue("$balanceReportJson", balanceReportJson);

            runId = (long)(insertRunCmd.ExecuteScalar() ?? 0L);
        }

        foreach (MaterialStream stream in flowsheet.Streams)
        {
            using SqliteCommand insertStreamCmd = connection.CreateCommand();
            insertStreamCmd.Transaction = transaction;
            insertStreamCmd.CommandText = """
                INSERT INTO SimulationStreamResults (
                    SimulationRunId, StreamId, StreamName, Phase,
                    MolarFlowRateMolPerSec, TemperatureKelvin, PressureKpa,
                    VaporFractionBeta, MolarEnthalpyJPerMol
                ) VALUES (
                    $runId, $streamId, $streamName, $phase,
                    $flow, $temp, $press, $beta, $enthalpy
                );
                """;
            insertStreamCmd.Parameters.AddWithValue("$runId", runId);
            insertStreamCmd.Parameters.AddWithValue("$streamId", stream.Id);
            insertStreamCmd.Parameters.AddWithValue("$streamName", stream.Name);
            insertStreamCmd.Parameters.AddWithValue("$phase", stream.IsSolved ? stream.Phase.ToString() : "Unsolved");
            insertStreamCmd.Parameters.AddWithValue("$flow", stream.MolarFlowRateMolPerSec);
            insertStreamCmd.Parameters.AddWithValue("$temp", stream.TemperatureKelvin);
            insertStreamCmd.Parameters.AddWithValue("$press", stream.PressureKpa);
            insertStreamCmd.Parameters.AddWithValue("$beta", stream.IsSolved ? stream.VaporFractionBeta : 0.0);
            insertStreamCmd.Parameters.AddWithValue("$enthalpy", stream.IsSolved ? stream.MolarEnthalpyJPerMol : 0.0);
            insertStreamCmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return runId;
    }

    public SimulationRunRecord? GetById(long runId)
    {
        using SqliteConnection connection = _db.CreateOpenConnection();

        string? flowsheetName = null;
        string? thermoModel = null;
        DateTime executedAt = DateTime.MinValue;
        string[] unitOrder = [];
        string? topologyDot = null;
        string? topologyJson = null;
        string? balanceReportJson = null;

        using (SqliteCommand runCmd = connection.CreateCommand())
        {
            runCmd.CommandText = """
                SELECT FlowsheetName, ThermodynamicModel, ExecutedAtUtc, UnitExecutionOrder,
                       TopologyDot, TopologyJson, BalanceReportJson
                FROM SimulationRuns
                WHERE Id = $id;
                """;
            runCmd.Parameters.AddWithValue("$id", runId);

            using SqliteDataReader reader = runCmd.ExecuteReader();
            if (!reader.Read())
                return null;

            flowsheetName = reader.GetString(0);
            thermoModel = reader.GetString(1);
            executedAt = DateTime.Parse(reader.GetString(2)).ToUniversalTime();
            string rawOrder = reader.GetString(3);
            unitOrder = string.IsNullOrEmpty(rawOrder) ? [] : rawOrder.Split("->", StringSplitOptions.RemoveEmptyEntries);
            topologyDot = reader.IsDBNull(4) ? null : reader.GetString(4);
            topologyJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            balanceReportJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        var streams = new List<StreamResultRecord>();
        using (SqliteCommand streamsCmd = connection.CreateCommand())
        {
            streamsCmd.CommandText = """
                SELECT StreamId, StreamName, Phase,
                       MolarFlowRateMolPerSec, TemperatureKelvin, PressureKpa,
                       VaporFractionBeta, MolarEnthalpyJPerMol
                FROM SimulationStreamResults
                WHERE SimulationRunId = $runId
                ORDER BY Id ASC;
                """;
            streamsCmd.Parameters.AddWithValue("$runId", runId);

            using SqliteDataReader reader = streamsCmd.ExecuteReader();
            while (reader.Read())
            {
                streams.Add(new StreamResultRecord(
                    StreamId: reader.GetString(0),
                    StreamName: reader.GetString(1),
                    Phase: reader.GetString(2),
                    MolarFlowRateMolPerSec: reader.GetDouble(3),
                    TemperatureKelvin: reader.GetDouble(4),
                    PressureKpa: reader.GetDouble(5),
                    VaporFractionBeta: reader.GetDouble(6),
                    MolarEnthalpyJPerMol: reader.GetDouble(7)
                ));
            }
        }

        return new SimulationRunRecord(
            Id: runId,
            FlowsheetName: flowsheetName!,
            ThermodynamicModel: thermoModel!,
            ExecutedAtUtc: executedAt,
            UnitExecutionOrder: unitOrder,
            Streams: streams,
            TopologyDot: topologyDot,
            TopologyJson: topologyJson,
            BalanceReportJson: balanceReportJson
        );
    }

    public FlowsheetTopology? GetTopology(long runId)
    {
        using SqliteConnection connection = _db.CreateOpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TopologyJson FROM SimulationRuns WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", runId);

        object? result = cmd.ExecuteScalar();
        if (result is null || result is DBNull || string.IsNullOrWhiteSpace(result.ToString()))
            return null;

        return System.Text.Json.JsonSerializer.Deserialize<FlowsheetTopology>(result.ToString()!, JsonOptions);
    }

    public FlowsheetBalanceReport? GetBalanceReport(long runId)
    {
        using SqliteConnection connection = _db.CreateOpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT BalanceReportJson FROM SimulationRuns WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", runId);

        object? result = cmd.ExecuteScalar();
        if (result is null || result is DBNull || string.IsNullOrWhiteSpace(result.ToString()))
            return null;

        return System.Text.Json.JsonSerializer.Deserialize<FlowsheetBalanceReport>(result.ToString()!, JsonOptions);
    }

    public IReadOnlyList<SimulationRunRecord> GetRecentRuns(int limit = 20)
    {
        using SqliteConnection connection = _db.CreateOpenConnection();
        var ids = new List<long>();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id
                FROM SimulationRuns
                ORDER BY Id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        var results = new List<SimulationRunRecord>(ids.Count);
        foreach (long id in ids)
        {
            SimulationRunRecord? record = GetById(id);
            if (record is not null)
                results.Add(record);
        }

        return results;
    }
}
