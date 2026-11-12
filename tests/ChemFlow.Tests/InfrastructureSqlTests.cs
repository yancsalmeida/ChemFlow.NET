using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Flowsheet;
using ChemFlow.Domain.Repositories;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;
using ChemFlow.Domain.ValueObjects;
using ChemFlow.Infrastructure.Persistence;
using ChemFlow.Infrastructure.Repositories;

namespace ChemFlow.Tests;

public sealed class InfrastructureSqlTests : IDisposable
{
    private readonly string _dbFilePath;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly ICompoundRepository _compoundRepo;
    private readonly ISimulationHistoryRepository _historyRepo;

    public InfrastructureSqlTests()
    {
        _dbFilePath = Path.Combine(Path.GetTempPath(), $"chemflow_test_{Guid.NewGuid():N}.db");
        _initializer = new SqliteDatabaseInitializer($"Data Source={_dbFilePath}");
        _initializer.InitializeAndSeed();

        _compoundRepo = new SqliteCompoundRepository(_initializer);
        _historyRepo = new SqliteSimulationHistoryRepository(_initializer);
    }

    [Fact]
    public void DatabaseInitializer_SeedsDefaultCompoundsAndSupportsCaseInsensitiveLookup()
    {
        // Act
        IReadOnlyList<ChemicalCompound> all = _compoundRepo.GetAll();
        ChemicalCompound? benzene = _compoundRepo.GetById("c6h6");

        // Assert
        Assert.Equal(DefaultCompounds.All.Count, all.Count);
        Assert.NotNull(benzene);
        Assert.Equal("Benzene", benzene.Name);
        Assert.Equal(78.114, benzene.MolarMass, precision: 3);
    }

    [Fact]
    public void CompoundRepository_Upsert_InsertsNewCompoundAndUpdatesExisting()
    {
        // Arrange: Add Acetone to the SQL database
        var acetone = new ChemicalCompound(
            id: "C3H6O",
            name: "Acetone",
            formula: "C3H6O",
            molarMass: 58.080,
            criticalTemperatureKelvin: 508.2,
            criticalPressureKpa: 4701.0,
            acentricFactor: 0.307,
            liquidHeatCapacityJPerMolK: 126.3,
            vaporHeatCapacityJPerMolK: 75.0,
            heatOfVaporizationJPerMol: 29100.0,
            antoine: new AntoineCoefficients(14.3145, 2756.22, -45.09)
        );

        // Act
        _compoundRepo.Upsert(acetone);
        ChemicalCompound? fetched = _compoundRepo.GetById("C3H6O");

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal("Acetone", fetched.Name);
        Assert.Equal(DefaultCompounds.All.Count + 1, _compoundRepo.GetAll().Count);
    }

    [Fact]
    public void SimulationHistoryRepository_SavesAndRetrievesRelationalRunWithStreams()
    {
        // Arrange: Fetch compounds from SQL, run a Flash Drum simulation, and save to SQL
        IReadOnlyList<ChemicalCompound> compounds = _compoundRepo.GetByIds(["C6H6", "C7H8"]);
        IThermodynamicPackage thermo = new RaoultThermodynamicPackage();

        var feed = new MaterialStream("FEED-1", "Reactor Feed", compounds, 100.0, 368.15, 101.325, [0.50, 0.50]);
        var vapor = new MaterialStream("VAP-1", "Vapor Top", compounds);
        var liquid = new MaterialStream("LIQ-1", "Liquid Bottom", compounds);

        var drum = new FlashDrum("V-100", "Separator", feed, vapor, liquid);
        var flowsheet = new ProcessFlowsheet("SQL Persistence Test Plant", thermo);
        flowsheet.AddUnit(drum);

        IReadOnlyList<IUnitOperation> executedOrder = flowsheet.Solve();

        // Act: Save to relational tables (SimulationRuns + SimulationStreamResults)
        long runId = _historyRepo.SaveSimulationRun(flowsheet, executedOrder);
        SimulationRunRecord? savedRun = _historyRepo.GetById(runId);

        // Assert
        Assert.True(runId > 0);
        Assert.NotNull(savedRun);
        Assert.Equal("SQL Persistence Test Plant", savedRun.FlowsheetName);
        Assert.Equal(["V-100"], savedRun.UnitExecutionOrder);
        Assert.Equal(3, savedRun.Streams.Count);
        Assert.Contains(savedRun.Streams, s => s.StreamId == "VAP-1" && s.MolarFlowRateMolPerSec > 0);
    }

    public void Dispose()
    {
        if (File.Exists(_dbFilePath))
        {
            try { File.Delete(_dbFilePath); } catch { /* ignore cleanup errors */ }
        }
    }
}
