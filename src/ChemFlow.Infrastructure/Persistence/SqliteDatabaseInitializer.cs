using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace ChemFlow.Infrastructure.Persistence;

/// <summary>
/// Manages the relational SQLite schema (DDL) and seeds default thermodynamic compounds.
/// </summary>
public sealed class SqliteDatabaseInitializer
{
    public string ConnectionString { get; }

    public SqliteDatabaseInitializer(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));

        ConnectionString = connectionString;
    }

    public SqliteConnection CreateOpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA foreign_keys = ON;";
        pragmaCmd.ExecuteNonQuery();

        return connection;
    }

    public void InitializeAndSeed()
    {
        using SqliteConnection connection = CreateOpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand ddlCmd = connection.CreateCommand())
        {
            ddlCmd.Transaction = transaction;
            ddlCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS ChemicalCompounds (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Formula TEXT NOT NULL,
                    MolarMass REAL NOT NULL CHECK (MolarMass > 0),
                    CriticalTemperatureKelvin REAL NOT NULL CHECK (CriticalTemperatureKelvin > 0),
                    CriticalPressureKpa REAL NOT NULL CHECK (CriticalPressureKpa > 0),
                    AcentricFactor REAL NOT NULL,
                    LiquidHeatCapacityJPerMolK REAL NOT NULL,
                    VaporHeatCapacityJPerMolK REAL NOT NULL,
                    HeatOfVaporizationJPerMol REAL NOT NULL,
                    AntoineA REAL NOT NULL,
                    AntoineB REAL NOT NULL CHECK (AntoineB > 0),
                    AntoineC REAL NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SimulationRuns (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FlowsheetName TEXT NOT NULL,
                    ThermodynamicModel TEXT NOT NULL,
                    ExecutedAtUtc TEXT NOT NULL,
                    UnitExecutionOrder TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SimulationStreamResults (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SimulationRunId INTEGER NOT NULL,
                    StreamId TEXT NOT NULL,
                    StreamName TEXT NOT NULL,
                    Phase TEXT NOT NULL,
                    MolarFlowRateMolPerSec REAL NOT NULL,
                    TemperatureKelvin REAL NOT NULL,
                    PressureKpa REAL NOT NULL,
                    VaporFractionBeta REAL NOT NULL,
                    MolarEnthalpyJPerMol REAL NOT NULL,
                    FOREIGN KEY (SimulationRunId) REFERENCES SimulationRuns(Id) ON DELETE CASCADE
                );
                """;
            ddlCmd.ExecuteNonQuery();
        }

        foreach (ChemicalCompound compound in DefaultCompounds.All)
        {
            using SqliteCommand seedCmd = connection.CreateCommand();
            seedCmd.Transaction = transaction;
            seedCmd.CommandText = """
                INSERT OR IGNORE INTO ChemicalCompounds (
                    Id, Name, Formula, MolarMass,
                    CriticalTemperatureKelvin, CriticalPressureKpa, AcentricFactor,
                    LiquidHeatCapacityJPerMolK, VaporHeatCapacityJPerMolK, HeatOfVaporizationJPerMol,
                    AntoineA, AntoineB, AntoineC
                ) VALUES (
                    $id, $name, $formula, $molarMass,
                    $tc, $pc, $omega,
                    $cpL, $cpV, $dhVap,
                    $antA, $antB, $antC
                );
                """;
            seedCmd.Parameters.AddWithValue("$id", compound.Id);
            seedCmd.Parameters.AddWithValue("$name", compound.Name);
            seedCmd.Parameters.AddWithValue("$formula", compound.Formula);
            seedCmd.Parameters.AddWithValue("$molarMass", compound.MolarMass);
            seedCmd.Parameters.AddWithValue("$tc", compound.CriticalTemperatureKelvin);
            seedCmd.Parameters.AddWithValue("$pc", compound.CriticalPressureKpa);
            seedCmd.Parameters.AddWithValue("$omega", compound.AcentricFactor);
            seedCmd.Parameters.AddWithValue("$cpL", compound.LiquidHeatCapacityJPerMolK);
            seedCmd.Parameters.AddWithValue("$cpV", compound.VaporHeatCapacityJPerMolK);
            seedCmd.Parameters.AddWithValue("$dhVap", compound.HeatOfVaporizationJPerMol);
            seedCmd.Parameters.AddWithValue("$antA", compound.Antoine.A);
            seedCmd.Parameters.AddWithValue("$antB", compound.Antoine.B);
            seedCmd.Parameters.AddWithValue("$antC", compound.Antoine.C);
            seedCmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
