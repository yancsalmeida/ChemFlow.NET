using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Units;

namespace ChemFlow.Domain.Flowsheet;

/// <summary>
/// Represents a chemical process flowsheet as a Directed Acyclic Graph (DAG) where:
/// - Vertices (V) are equipment blocks (<see cref="IUnitOperation"/>)
/// - Directed Edges (E) are internal material streams (<see cref="MaterialStream"/>) connecting
///   an upstream unit's outlet to a downstream unit's inlet.
/// Uses Kahn's Topological Sort algorithm O(V + E) to determine the valid sequential calculation order.
/// </summary>
public sealed class ProcessFlowsheet
{
    private readonly Dictionary<string, MaterialStream> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IUnitOperation> _units = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; }
    public IThermodynamicPackage ThermodynamicPackage { get; }

    public IReadOnlyCollection<MaterialStream> Streams => _streams.Values;
    public IReadOnlyCollection<IUnitOperation> Units => _units.Values;

    public ProcessFlowsheet(string name, IThermodynamicPackage thermodynamicPackage)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Flowsheet name cannot be empty.", nameof(name));
        ArgumentNullException.ThrowIfNull(thermodynamicPackage);

        Name = name.Trim();
        ThermodynamicPackage = thermodynamicPackage;
    }

    public void AddStream(MaterialStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!_streams.TryAdd(stream.Id, stream))
            throw new InvalidOperationException($"A stream with ID '{stream.Id}' already exists in the flowsheet.");
    }

    public void AddUnit(IUnitOperation unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_units.TryAdd(unit.Id, unit))
            throw new InvalidOperationException($"A unit operation with ID '{unit.Id}' already exists in the flowsheet.");

        // Automatically register any streams attached to the unit if not already registered
        foreach (MaterialStream inlet in unit.InletStreams)
            _streams.TryAdd(inlet.Id, inlet);

        foreach (MaterialStream outlet in unit.OutletStreams)
            _streams.TryAdd(outlet.Id, outlet);
    }

    /// <summary>
    /// Computes the topological execution order of unit operations using Kahn's Algorithm O(V + E).
    /// Throws <see cref="InvalidOperationException"/> if a recycle loop (cycle) is detected.
    /// </summary>
    public IReadOnlyList<IUnitOperation> GetTopologicalExecutionOrder()
    {
        // Map each stream ID to the upstream unit that produces it (if any)
        var producerByStreamId = new Dictionary<string, IUnitOperation>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream outlet in unit.OutletStreams)
            {
                producerByStreamId[outlet.Id] = unit;
            }
        }

        // Build adjacency list (u -> list of downstream units v) and in-degree table
        var adjacencyList = new Dictionary<string, List<IUnitOperation>>(StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (IUnitOperation unit in _units.Values)
        {
            adjacencyList[unit.Id] = [];
            inDegree[unit.Id] = 0;
        }

        foreach (IUnitOperation targetUnit in _units.Values)
        {
            // Track unique upstream dependencies per unit so parallel pipes between the same two units count once
            var uniqueUpstreamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MaterialStream inlet in targetUnit.InletStreams)
            {
                if (producerByStreamId.TryGetValue(inlet.Id, out IUnitOperation? sourceUnit) &&
                    uniqueUpstreamIds.Add(sourceUnit.Id))
                {
                    adjacencyList[sourceUnit.Id].Add(targetUnit);
                    inDegree[targetUnit.Id]++;
                }
            }
        }

        // Kahn's Algorithm: enqueue all units with in-degree == 0 (fed only by external feed streams)
        var queue = new Queue<IUnitOperation>();
        foreach (IUnitOperation unit in _units.Values)
        {
            if (inDegree[unit.Id] == 0)
            {
                queue.Enqueue(unit);
            }
        }

        var executionOrder = new List<IUnitOperation>(_units.Count);

        while (queue.Count > 0)
        {
            IUnitOperation current = queue.Dequeue();
            executionOrder.Add(current);

            foreach (IUnitOperation downstream in adjacencyList[current.Id])
            {
                inDegree[downstream.Id]--;
                if (inDegree[downstream.Id] == 0)
                {
                    queue.Enqueue(downstream);
                }
            }
        }

        if (executionOrder.Count != _units.Count)
        {
            var cyclicUnits = _units.Values
                .Where(u => inDegree[u.Id] > 0)
                .Select(u => u.Name);

            throw new InvalidOperationException(
                $"Cycle detected in process flowsheet '{Name}'. Units involved in recycle loop: {string.Join(", ", cyclicUnits)}.");
        }

        return executionOrder;
    }

    /// <summary>
    /// Solves the entire process flowsheet:
    /// 1. Flashes all external feed streams
    /// 2. Determines topological order via Kahn's DAG algorithm
    /// 3. Solves each unit operation sequentially
    /// </summary>
    public IReadOnlyList<IUnitOperation> Solve()
    {
        // Identify streams produced by units so we know which ones are external feeds
        var producedStreamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream outlet in unit.OutletStreams)
            {
                producedStreamIds.Add(outlet.Id);
            }
        }

        // Step 1: Equilibrate all external feed streams
        foreach (MaterialStream stream in _streams.Values)
        {
            if (!producedStreamIds.Contains(stream.Id))
            {
                stream.SolveEquilibrium(ThermodynamicPackage);
            }
        }

        // Step 2: Obtain topological execution sequence
        IReadOnlyList<IUnitOperation> executionOrder = GetTopologicalExecutionOrder();

        // Step 3: Solve each equipment block in order
        foreach (IUnitOperation unit in executionOrder)
        {
            unit.Solve(ThermodynamicPackage);
        }

        return executionOrder;
    }
}
