using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.ValueObjects;
using ChemFlow.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace ChemFlow.Infrastructure.Repositories;

/// <summary>
/// Relational SQLite implementation of <see cref="ICompoundRepository"/> using parameterized SQL queries.
/// </summary>
public sealed class SqliteCompoundRepository : ICompoundRepository
{
    private readonly SqliteDatabaseInitializer _db;

    public SqliteCompoundRepository(SqliteDatabaseInitializer db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public IReadOnlyList<ChemicalCompound> GetAll()
    {
        using SqliteConnection connection = _db.CreateOpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Formula, MolarMass,
                   CriticalTemperatureKelvin, CriticalPressureKpa, AcentricFactor,
                   LiquidHeatCapacityJPerMolK, VaporHeatCapacityJPerMolK, HeatOfVaporizationJPerMol,
                   AntoineA, AntoineB, AntoineC
            FROM ChemicalCompounds
            ORDER BY Name ASC;
            """;

        var results = new List<ChemicalCompound>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapCompound(reader));
        }

        return results;
    }

    public ChemicalCompound? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        using SqliteConnection connection = _db.CreateOpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Formula, MolarMass,
                   CriticalTemperatureKelvin, CriticalPressureKpa, AcentricFactor,
                   LiquidHeatCapacityJPerMolK, VaporHeatCapacityJPerMolK, HeatOfVaporizationJPerMol,
                   AntoineA, AntoineB, AntoineC
            FROM ChemicalCompounds
            WHERE Id = $id COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$id", id.Trim());

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapCompound(reader) : null;
    }

    public IReadOnlyList<ChemicalCompound> GetByIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var results = new List<ChemicalCompound>();
        foreach (string id in ids)
        {
            ChemicalCompound? compound = GetById(id)
                ?? throw new KeyNotFoundException($"Chemical compound '{id}' was not found in the database.");
            results.Add(compound);
        }
        return results;
    }

    public void Upsert(ChemicalCompound compound)
    {
        ArgumentNullException.ThrowIfNull(compound);

        using SqliteConnection connection = _db.CreateOpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ChemicalCompounds (
                Id, Name, Formula, MolarMass,
                CriticalTemperatureKelvin, CriticalPressureKpa, AcentricFactor,
                LiquidHeatCapacityJPerMolK, VaporHeatCapacityJPerMolK, HeatOfVaporizationJPerMol,
                AntoineA, AntoineB, AntoineC
            ) VALUES (
                $id, $name, $formula, $molarMass,
                $tc, $pc, $omega,
                $cpL, $cpV, $dhVap,
                $antA, $antB, $antC
            )
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Formula = excluded.Formula,
                MolarMass = excluded.MolarMass,
                CriticalTemperatureKelvin = excluded.CriticalTemperatureKelvin,
                CriticalPressureKpa = excluded.CriticalPressureKpa,
                AcentricFactor = excluded.AcentricFactor,
                LiquidHeatCapacityJPerMolK = excluded.LiquidHeatCapacityJPerMolK,
                VaporHeatCapacityJPerMolK = excluded.VaporHeatCapacityJPerMolK,
                HeatOfVaporizationJPerMol = excluded.HeatOfVaporizationJPerMol,
                AntoineA = excluded.AntoineA,
                AntoineB = excluded.AntoineB,
                AntoineC = excluded.AntoineC;
            """;

        cmd.Parameters.AddWithValue("$id", compound.Id);
        cmd.Parameters.AddWithValue("$name", compound.Name);
        cmd.Parameters.AddWithValue("$formula", compound.Formula);
        cmd.Parameters.AddWithValue("$molarMass", compound.MolarMass);
        cmd.Parameters.AddWithValue("$tc", compound.CriticalTemperatureKelvin);
        cmd.Parameters.AddWithValue("$pc", compound.CriticalPressureKpa);
        cmd.Parameters.AddWithValue("$omega", compound.AcentricFactor);
        cmd.Parameters.AddWithValue("$cpL", compound.LiquidHeatCapacityJPerMolK);
        cmd.Parameters.AddWithValue("$cpV", compound.VaporHeatCapacityJPerMolK);
        cmd.Parameters.AddWithValue("$dhVap", compound.HeatOfVaporizationJPerMol);
        cmd.Parameters.AddWithValue("$antA", compound.Antoine.A);
        cmd.Parameters.AddWithValue("$antB", compound.Antoine.B);
        cmd.Parameters.AddWithValue("$antC", compound.Antoine.C);

        cmd.ExecuteNonQuery();
    }

    private static ChemicalCompound MapCompound(SqliteDataReader reader) =>
        new(
            id: reader.GetString(0),
            name: reader.GetString(1),
            formula: reader.GetString(2),
            molarMass: reader.GetDouble(3),
            criticalTemperatureKelvin: reader.GetDouble(4),
            criticalPressureKpa: reader.GetDouble(5),
            acentricFactor: reader.GetDouble(6),
            liquidHeatCapacityJPerMolK: reader.GetDouble(7),
            vaporHeatCapacityJPerMolK: reader.GetDouble(8),
            heatOfVaporizationJPerMol: reader.GetDouble(9),
            antoine: new AntoineCoefficients(
                reader.GetDouble(10),
                reader.GetDouble(11),
                reader.GetDouble(12))
        );
}
