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
    private readonly HashSet<string> _tearStreamIds = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; }
    public IThermodynamicPackage ThermodynamicPackage { get; }

    public IReadOnlyCollection<MaterialStream> Streams => _streams.Values;
    public IReadOnlyCollection<IUnitOperation> Units => _units.Values;
    public IReadOnlyCollection<string> TearStreamIds => _tearStreamIds;

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
    /// Explicitly registers a material stream as a tear stream (recycle cut) for iterative convergence.
    /// </summary>
    public void AddTearStream(MaterialStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _streams.TryAdd(stream.Id, stream);
        _tearStreamIds.Add(stream.Id);
    }

    /// <summary>
    /// Explicitly registers an existing material stream ID as a tear stream.
    /// </summary>
    public void AddTearStream(string streamId)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            throw new ArgumentException("Stream ID cannot be empty.", nameof(streamId));
        if (!_streams.ContainsKey(streamId))
            throw new InvalidOperationException($"Stream '{streamId}' does not exist in flowsheet '{Name}'.");
        _tearStreamIds.Add(streamId);
    }

    public bool IsTearStream(string streamId) => _tearStreamIds.Contains(streamId);

    public void ClearTearStreams() => _tearStreamIds.Clear();

    /// <summary>
    /// Computes the topological execution order of unit operations using Kahn's Algorithm O(V + E).
    /// When <paramref name="ignoreTearStreams"/> is true, registered tear streams are excluded from
    /// dependency checking, allowing cyclical flowsheet topologies to be sequenced sequentially.
    /// Throws <see cref="InvalidOperationException"/> if an un-torn cycle is detected.
    /// </summary>
    public IReadOnlyList<IUnitOperation> GetTopologicalExecutionOrder(bool ignoreTearStreams = false)
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
                // When tearing cycles, ignore dependencies coming through tear streams
                if (ignoreTearStreams && _tearStreamIds.Contains(inlet.Id))
                    continue;

                if (producerByStreamId.TryGetValue(inlet.Id, out IUnitOperation? sourceUnit) &&
                    uniqueUpstreamIds.Add(sourceUnit.Id))
                {
                    adjacencyList[sourceUnit.Id].Add(targetUnit);
                    inDegree[targetUnit.Id]++;
                }
            }
        }

        // Kahn's Algorithm: enqueue all units with in-degree == 0 (fed only by external feeds or torn recycles)
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

            string tearInfo = ignoreTearStreams && _tearStreamIds.Count > 0
                ? $" Current tear streams [{string.Join(", ", _tearStreamIds)}] did not resolve all cycles."
                : " Register tear streams via AddTearStream() or use SolveWithRecycle() to resolve recycles.";

            throw new InvalidOperationException(
                $"Cycle detected in process flowsheet '{Name}'. Units involved in recycle loop: {string.Join(", ", cyclicUnits)}.{tearInfo}");
        }

        return executionOrder;
    }

    /// <summary>
    /// Automatically detects recycle loops and selects tear streams to break all feedback cycles.
    /// Uses Depth-First Search (DFS) with three-color cycle detection to locate back-edges.
    /// </summary>
    public IReadOnlyList<MaterialStream> AutoSelectTearStreams()
    {
        var consumersByStreamId = new Dictionary<string, List<IUnitOperation>>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream inlet in unit.InletStreams)
            {
                if (!consumersByStreamId.TryGetValue(inlet.Id, out var list))
                {
                    list = [];
                    consumersByStreamId[inlet.Id] = list;
                }
                list.Add(unit);
            }
        }

        // Color states: 0 = Unvisited (White), 1 = Visiting (Gray), 2 = Visited (Black)
        var color = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in _units.Values)
            color[unit.Id] = 0;

        var detectedTearStreams = new List<MaterialStream>();

        void Dfs(IUnitOperation current)
        {
            color[current.Id] = 1; // Gray

            foreach (MaterialStream outlet in current.OutletStreams)
            {
                if (consumersByStreamId.TryGetValue(outlet.Id, out var targets))
                {
                    foreach (var target in targets)
                    {
                        if (color[target.Id] == 1)
                        {
                            // Back-edge detected! This outlet stream feeds an ancestor in active DFS path.
                            if (!_tearStreamIds.Contains(outlet.Id))
                            {
                                _tearStreamIds.Add(outlet.Id);
                                detectedTearStreams.Add(outlet);
                            }
                        }
                        else if (color[target.Id] == 0)
                        {
                            Dfs(target);
                        }
                    }
                }
            }

            color[current.Id] = 2; // Black
        }

        foreach (var unit in _units.Values)
        {
            if (color[unit.Id] == 0)
                Dfs(unit);
        }

        return detectedTearStreams;
    }

    /// <summary>
    /// Solves the process flowsheet, supporting closed-loop recycles via tear streams and convergence acceleration
    /// (Wegstein Bounded Secant or Direct Substitution).
    /// </summary>
    public FlowsheetSolveResult SolveWithRecycle(RecycleSolverOptions? options = null)
    {
        options ??= RecycleSolverOptions.Default;

        // Step 1: Detect whether flowsheet is naturally acyclic or requires tear streams
        if (_tearStreamIds.Count == 0)
        {
            bool isAcyclic = false;
            try
            {
                GetTopologicalExecutionOrder(ignoreTearStreams: false);
                isAcyclic = true;
            }
            catch (InvalidOperationException)
            {
                isAcyclic = false;
            }

            if (isAcyclic)
            {
                IReadOnlyList<IUnitOperation> order = SolveInternalAcyclic();
                return new FlowsheetSolveResult(
                    FlowsheetName: Name,
                    ExecutionOrder: order,
                    HasRecycle: false,
                    Iterations: 1,
                    IsConverged: true,
                    TearStreamIds: [],
                    ConvergenceHistory: []
                );
            }

            // Auto-detect tear streams from feedback loops
            AutoSelectTearStreams();
            if (_tearStreamIds.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Cycle detected in process flowsheet '{Name}', but no tear stream could be identified.");
            }
        }

        // Step 2: Obtain topological execution sequence for torn flowsheet
        IReadOnlyList<IUnitOperation> executionOrder = GetTopologicalExecutionOrder(ignoreTearStreams: true);

        // Step 3: Identify external feeds and equilibrate them
        var producerByStreamId = new Dictionary<string, IUnitOperation>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream outlet in unit.OutletStreams)
            {
                producerByStreamId[outlet.Id] = unit;
            }
        }

        var externalFeeds = _streams.Values
            .Where(s => !producerByStreamId.ContainsKey(s.Id))
            .ToList();

        foreach (MaterialStream feed in externalFeeds)
        {
            feed.SolveEquilibrium(ThermodynamicPackage);
        }

        // Step 4: Prepare tear streams and initial guess states
        var tearStreams = new List<MaterialStream>(_tearStreamIds.Count);
        var guessStates = new Dictionary<string, StreamStateVector>(StringComparer.OrdinalIgnoreCase);

        foreach (string tearId in _tearStreamIds)
        {
            if (!_streams.TryGetValue(tearId, out MaterialStream? tearStream))
                throw new InvalidOperationException($"Tear stream '{tearId}' is not found in flowsheet '{Name}'.");

            tearStreams.Add(tearStream);

            if (!tearStream.IsSolved)
            {
                tearStream.SolveEquilibrium(ThermodynamicPackage);
            }

            guessStates[tearId] = StreamStateVector.FromStream(tearStream);
        }

        // Step 5: Iterative tear stream convergence loop
        var accelerator = new WegsteinRecycleAccelerator(options);
        var convergenceHistory = new List<TearStreamConvergenceRecord>();
        bool allConverged = false;
        int completedIterations = 0;

        for (int iter = 1; iter <= options.MaxIterations; iter++)
        {
            completedIterations = iter;

            // Solve each unit in torn topological order
            foreach (IUnitOperation unit in executionOrder)
            {
                unit.Solve(ThermodynamicPackage);
            }

            // Check convergence across all tear streams
            bool iterConverged = true;
            var currentCalculated = new Dictionary<string, StreamStateVector>(StringComparer.OrdinalIgnoreCase);

            foreach (MaterialStream tearStream in tearStreams)
            {
                StreamStateVector guess = guessStates[tearStream.Id];
                StreamStateVector calc = StreamStateVector.FromStream(tearStream);
                currentCalculated[tearStream.Id] = calc;

                TearStreamConvergenceRecord record = accelerator.CheckConvergence(tearStream.Id, guess, calc, iter);
                convergenceHistory.Add(record);

                if (!record.IsConverged)
                {
                    iterConverged = false;
                }
            }

            if (iterConverged)
            {
                allConverged = true;
                break;
            }

            // If not converged and not on the last iteration, accelerate tear stream states
            if (iter < options.MaxIterations)
            {
                foreach (MaterialStream tearStream in tearStreams)
                {
                    StreamStateVector guess = guessStates[tearStream.Id];
                    StreamStateVector calc = currentCalculated[tearStream.Id];

                    StreamStateVector nextGuess = accelerator.Accelerate(tearStream.Id, guess, calc, iter);
                    guessStates[tearStream.Id] = nextGuess;
                    nextGuess.ApplyToStream(tearStream, ThermodynamicPackage);
                }
            }
        }

        return new FlowsheetSolveResult(
            FlowsheetName: Name,
            ExecutionOrder: executionOrder,
            HasRecycle: true,
            Iterations: completedIterations,
            IsConverged: allConverged,
            TearStreamIds: _tearStreamIds.ToList(),
            ConvergenceHistory: convergenceHistory
        );
    }

    private IReadOnlyList<IUnitOperation> SolveInternalAcyclic()
    {
        var producedStreamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream outlet in unit.OutletStreams)
            {
                producedStreamIds.Add(outlet.Id);
            }
        }

        foreach (MaterialStream stream in _streams.Values)
        {
            if (!producedStreamIds.Contains(stream.Id))
            {
                stream.SolveEquilibrium(ThermodynamicPackage);
            }
        }

        IReadOnlyList<IUnitOperation> executionOrder = GetTopologicalExecutionOrder(ignoreTearStreams: false);

        foreach (IUnitOperation unit in executionOrder)
        {
            unit.Solve(ThermodynamicPackage);
        }

        return executionOrder;
    }

    /// <summary>
    /// Solves the entire process flowsheet:
    /// - For acyclic flowsheets: solves in one topological pass.
    /// - For recycle flowsheets: automatically breaks cycles and iterates to convergence.
    /// Throws <see cref="InvalidOperationException"/> if convergence is not reached.
    /// </summary>
    public IReadOnlyList<IUnitOperation> Solve()
    {
        FlowsheetSolveResult result = SolveWithRecycle();
        if (result.HasRecycle && !result.IsConverged)
        {
            throw new InvalidOperationException(
                $"Recycle loop solver did not converge after {result.Iterations} iterations in flowsheet '{Name}'.");
        }
        return result.ExecutionOrder;
    }

    /// <summary>
    /// Exports the structural topology of the flowsheet, including units, streams, connectivity, and Graphviz DOT.
    /// </summary>
    public FlowsheetTopology GetTopology()
    {
        var producerByStreamId = new Dictionary<string, IUnitOperation>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream outlet in unit.OutletStreams)
            {
                producerByStreamId[outlet.Id] = unit;
            }
        }

        var consumersByStreamId = new Dictionary<string, List<IUnitOperation>>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream inlet in unit.InletStreams)
            {
                if (!consumersByStreamId.TryGetValue(inlet.Id, out var list))
                {
                    list = [];
                    consumersByStreamId[inlet.Id] = list;
                }
                list.Add(unit);
            }
        }

        var adjacencyList = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            var downstreamUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MaterialStream outlet in unit.OutletStreams)
            {
                if (consumersByStreamId.TryGetValue(outlet.Id, out var targets))
                {
                    foreach (var target in targets)
                        downstreamUnits.Add(target.Id);
                }
            }
            adjacencyList[unit.Id] = downstreamUnits.ToList();
        }

        bool isAcyclic = true;
        IReadOnlyList<string> executionOrder = [];
        try
        {
            executionOrder = GetTopologicalExecutionOrder(ignoreTearStreams: false).Select(u => u.Id).ToList();
        }
        catch (InvalidOperationException)
        {
            isAcyclic = false;
            if (_tearStreamIds.Count > 0)
            {
                try
                {
                    executionOrder = GetTopologicalExecutionOrder(ignoreTearStreams: true).Select(u => u.Id).ToList();
                }
                catch (InvalidOperationException)
                {
                    // Remaining cyclic dependencies
                }
            }
        }

        var unitNodes = _units.Values.Select(u => new UnitTopologyNode(
            Id: u.Id,
            Name: u.Name,
            UnitType: u.UnitType,
            InletStreamIds: u.InletStreams.Select(s => s.Id).ToList(),
            OutletStreamIds: u.OutletStreams.Select(s => s.Id).ToList(),
            EnergyDutyWatts: u.EnergyDutyWatts
        )).ToList();

        var streamEdges = _streams.Values.Select(s =>
        {
            producerByStreamId.TryGetValue(s.Id, out IUnitOperation? sourceUnit);
            consumersByStreamId.TryGetValue(s.Id, out List<IUnitOperation>? targets);

            bool isFeed = sourceUnit is null;
            bool isProduct = targets is null || targets.Count == 0;
            string? targetUnitId = targets is not null && targets.Count > 0
                ? string.Join(", ", targets.Select(t => t.Id))
                : null;

            var compositionDict = new Dictionary<string, double>();
            for (int i = 0; i < s.Compounds.Count; i++)
                compositionDict[s.Compounds[i].Id] = Math.Round(s.CompositionZ[i], 6);

            return new StreamTopologyEdge(
                StreamId: s.Id,
                StreamName: s.Name,
                SourceUnitId: sourceUnit?.Id,
                TargetUnitId: targetUnitId,
                IsExternalFeed: isFeed,
                IsFinalProduct: isProduct,
                MolarFlowMolPerSec: s.MolarFlowRateMolPerSec,
                MassFlowKgPerSec: s.MassFlowRateKgPerSec,
                TemperatureKelvin: s.TemperatureKelvin,
                PressureKpa: s.PressureKpa,
                Phase: s.IsSolved ? s.Phase.ToString() : "Unsolved",
                VaporFractionBeta: s.IsSolved ? s.VaporFractionBeta : 0.0,
                TotalEnthalpyFlowWatts: s.IsSolved ? s.TotalEnthalpyFlowWatts : 0.0,
                Composition: compositionDict,
                IsTearStream: _tearStreamIds.Contains(s.Id)
            );
        }).ToList();

        string dotGraph = ToDot();

        return new FlowsheetTopology(
            FlowsheetName: Name,
            Units: unitNodes,
            Streams: streamEdges,
            AdjacencyList: adjacencyList,
            ExecutionOrder: executionOrder,
            IsAcyclic: isAcyclic,
            DotGraph: dotGraph,
            TearStreamIds: _tearStreamIds.ToList()
        );
    }

    /// <summary>
    /// Generates a Graphviz DOT representation of the flowsheet graph with equipment blocks as styled nodes
    /// and streams as directed edges with flow and thermal state labels.
    /// </summary>
    public string ToDot()
    {
        var producerByStreamId = new Dictionary<string, IUnitOperation>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream outlet in unit.OutletStreams)
            {
                producerByStreamId[outlet.Id] = unit;
            }
        }

        var consumersByStreamId = new Dictionary<string, List<IUnitOperation>>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream inlet in unit.InletStreams)
            {
                if (!consumersByStreamId.TryGetValue(inlet.Id, out var list))
                {
                    list = [];
                    consumersByStreamId[inlet.Id] = list;
                }
                list.Add(unit);
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"digraph \"{EscapeDot(Name)}\" {{");
        sb.AppendLine("    rankdir=LR;");
        sb.AppendLine("    splines=ortho;");
        sb.AppendLine("    nodesep=0.6;");
        sb.AppendLine("    ranksep=0.8;");
        sb.AppendLine("    node [fontname=\"Helvetica,Arial,sans-serif\", fontsize=10];");
        sb.AppendLine("    edge [fontname=\"Helvetica,Arial,sans-serif\", fontsize=8];");
        sb.AppendLine();

        // Feed nodes subgraph
        var feedStreams = _streams.Values.Where(s => !producerByStreamId.ContainsKey(s.Id)).ToList();
        if (feedStreams.Count > 0)
        {
            sb.AppendLine("    // External Feed Boundaries");
            sb.AppendLine("    subgraph cluster_feeds {");
            sb.AppendLine("        label=\"Plant Feeds\";");
            sb.AppendLine("        style=\"dashed,rounded\";");
            sb.AppendLine("        color=\"#94A3B8\";");
            sb.AppendLine("        fontcolor=\"#64748B\";");
            sb.AppendLine("        fontsize=9;");
            foreach (var feed in feedStreams)
            {
                string nodeId = $"feed_{SanitizeDotId(feed.Id)}";
                string label = $"{EscapeDot(feed.Name)}\\n[F = {feed.MolarFlowRateMolPerSec:F2} mol/s | T = {feed.TemperatureKelvin:F1} K]";
                sb.AppendLine($"        \"{nodeId}\" [label=\"{label}\", shape=ellipse, style=filled, fillcolor=\"#E0F2FE\", color=\"#0284C7\", fontcolor=\"#0369A1\"];");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Equipment unit operations
        sb.AppendLine("    // Equipment Operations");
        foreach (IUnitOperation unit in _units.Values)
        {
            string unitNodeId = SanitizeDotId(unit.Id);
            var (fillColor, strokeColor) = unit.UnitType switch
            {
                "Mixer" => ("#FEF3C7", "#D97706"),
                "HeaterCooler" => ("#FFEDD5", "#EA580C"),
                "FlashDrum" => ("#F3E8FF", "#9333EA"),
                "ConversionReactor" => ("#FCE7F3", "#DB2777"),
                "Splitter" => ("#E0E7FF", "#4F46E5"),
                _ => ("#F1F5F9", "#475569")
            };

            double dutyKw = unit.EnergyDutyWatts / 1000.0;
            string dutyStr = Math.Abs(dutyKw) > 1e-3 ? $" | Q = {dutyKw:F2} kW" : "";
            string recordLabel = $"{{{unit.Id} ({EscapeDot(unit.Name)})|{unit.UnitType}{dutyStr}}}";

            sb.AppendLine($"    \"{unitNodeId}\" [label=\"{recordLabel}\", shape=record, style=\"filled,rounded\", fillcolor=\"{fillColor}\", color=\"{strokeColor}\", penwidth=1.5];");
        }
        sb.AppendLine();

        // Product nodes subgraph
        var productStreams = _streams.Values.Where(s => producerByStreamId.ContainsKey(s.Id) && (!consumersByStreamId.ContainsKey(s.Id) || consumersByStreamId[s.Id].Count == 0)).ToList();
        if (productStreams.Count > 0)
        {
            sb.AppendLine("    // External Product Boundaries");
            sb.AppendLine("    subgraph cluster_products {");
            sb.AppendLine("        label=\"Plant Products\";");
            sb.AppendLine("        style=\"dashed,rounded\";");
            sb.AppendLine("        color=\"#94A3B8\";");
            sb.AppendLine("        fontcolor=\"#64748B\";");
            sb.AppendLine("        fontsize=9;");
            foreach (var prod in productStreams)
            {
                string nodeId = $"prod_{SanitizeDotId(prod.Id)}";
                string phaseStr = prod.IsSolved ? prod.Phase.ToString() : "Stream";
                string label = $"{EscapeDot(prod.Name)}\\n[F = {prod.MolarFlowRateMolPerSec:F2} mol/s | {phaseStr}]";
                sb.AppendLine($"        \"{nodeId}\" [label=\"{label}\", shape=ellipse, style=filled, fillcolor=\"#DCFCE7\", color=\"#16A34A\", fontcolor=\"#15803D\"];");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Stream Edges
        sb.AppendLine("    // Material Stream Connectivity");
        foreach (MaterialStream stream in _streams.Values)
        {
            producerByStreamId.TryGetValue(stream.Id, out IUnitOperation? sourceUnit);
            consumersByStreamId.TryGetValue(stream.Id, out List<IUnitOperation>? targetUnits);

            bool isTear = _tearStreamIds.Contains(stream.Id);

            string edgeLabel = $"{stream.Id}\\n{stream.MolarFlowRateMolPerSec:F1} mol/s";
            if (stream.IsSolved)
                edgeLabel += $"\\n{stream.TemperatureKelvin:F1} K";
            if (isTear)
                edgeLabel += "\\n[Recycle/Tear]";

            if (sourceUnit is null)
            {
                string feedNodeId = $"feed_{SanitizeDotId(stream.Id)}";
                if (targetUnits is not null)
                {
                    foreach (var target in targetUnits)
                    {
                        sb.AppendLine($"    \"{feedNodeId}\" -> \"{SanitizeDotId(target.Id)}\" [label=\"{edgeLabel}\", color=\"#0284C7\"];");
                    }
                }
            }
            else if (targetUnits is null || targetUnits.Count == 0)
            {
                string prodNodeId = $"prod_{SanitizeDotId(stream.Id)}";
                sb.AppendLine($"    \"{SanitizeDotId(sourceUnit.Id)}\" -> \"{prodNodeId}\" [label=\"{edgeLabel}\", color=\"#16A34A\"];");
            }
            else
            {
                foreach (var target in targetUnits)
                {
                    if (isTear)
                    {
                        sb.AppendLine($"    \"{SanitizeDotId(sourceUnit.Id)}\" -> \"{SanitizeDotId(target.Id)}\" [label=\"{edgeLabel}\", color=\"#a855f7\", style=\"dashed\", penwidth=2.0];");
                    }
                    else
                    {
                        sb.AppendLine($"    \"{SanitizeDotId(sourceUnit.Id)}\" -> \"{SanitizeDotId(target.Id)}\" [label=\"{edgeLabel}\", color=\"#475569\"];");
                    }
                }
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Generates a comprehensive mass and energy balance report verifying overall conservation across
    /// the flowsheet boundary, component species conservation, and local equipment balance closure.
    /// </summary>
    public FlowsheetBalanceReport GenerateBalanceReport()
    {
        var producerByStreamId = new Dictionary<string, IUnitOperation>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream outlet in unit.OutletStreams)
            {
                producerByStreamId[outlet.Id] = unit;
            }
        }

        var consumersByStreamId = new Dictionary<string, List<IUnitOperation>>(StringComparer.OrdinalIgnoreCase);
        foreach (IUnitOperation unit in _units.Values)
        {
            foreach (MaterialStream inlet in unit.InletStreams)
            {
                if (!consumersByStreamId.TryGetValue(inlet.Id, out var list))
                {
                    list = [];
                    consumersByStreamId[inlet.Id] = list;
                }
                list.Add(unit);
            }
        }

        var externalFeeds = _streams.Values
            .Where(s => !producerByStreamId.ContainsKey(s.Id))
            .ToList();

        var finalProducts = _streams.Values
            .Where(s => producerByStreamId.ContainsKey(s.Id) && (!consumersByStreamId.ContainsKey(s.Id) || consumersByStreamId[s.Id].Count == 0))
            .ToList();

        // 1. Global Mass and Molar Flow Balances
        double totalFeedMolarFlow = externalFeeds.Sum(s => s.MolarFlowRateMolPerSec);
        double totalProductMolarFlow = finalProducts.Sum(s => s.MolarFlowRateMolPerSec);
        double molarDelta = totalFeedMolarFlow - totalProductMolarFlow;
        double relativeMolarError = totalFeedMolarFlow > 1e-12 ? Math.Abs(molarDelta) / totalFeedMolarFlow : 0.0;

        double totalFeedMassFlow = externalFeeds.Sum(s => s.MassFlowRateKgPerSec);
        double totalProductMassFlow = finalProducts.Sum(s => s.MassFlowRateKgPerSec);
        double massDelta = totalFeedMassFlow - totalProductMassFlow;
        double relativeMassError = totalFeedMassFlow > 1e-12 ? Math.Abs(massDelta) / totalFeedMassFlow : 0.0;
        bool isGlobalMassBalanced = relativeMassError < 1e-5 || Math.Abs(massDelta) < 1e-6;

        // 2. Global Energy Balance
        double totalFeedEnthalpy = externalFeeds.Sum(s => s.TotalEnthalpyFlowWatts);
        double totalProductEnthalpy = finalProducts.Sum(s => s.TotalEnthalpyFlowWatts);
        double totalDutyAdded = _units.Values.Sum(u => u.EnergyDutyWatts);
        double enthalpyDelta = (totalFeedEnthalpy + totalDutyAdded) - totalProductEnthalpy;
        double energyScale = Math.Abs(totalFeedEnthalpy) + Math.Abs(totalDutyAdded) + Math.Abs(totalProductEnthalpy);
        double relativeEnthalpyError = energyScale > 1e-6 ? Math.Abs(enthalpyDelta) / energyScale : 0.0;
        bool isGlobalEnergyBalanced = relativeEnthalpyError < 5e-4 || Math.Abs(enthalpyDelta) < 1.0;

        var globalSummary = new GlobalBalanceSummary(
            TotalFeedMolarFlowMolPerSec: totalFeedMolarFlow,
            TotalProductMolarFlowMolPerSec: totalProductMolarFlow,
            MolarFlowDeltaMolPerSec: molarDelta,
            RelativeMolarError: relativeMolarError,
            TotalFeedMassFlowKgPerSec: totalFeedMassFlow,
            TotalProductMassFlowKgPerSec: totalProductMassFlow,
            MassFlowDeltaKgPerSec: massDelta,
            RelativeMassError: relativeMassError,
            TotalFeedEnthalpyWatts: totalFeedEnthalpy,
            TotalProductEnthalpyWatts: totalProductEnthalpy,
            TotalHeatDutyAddedWatts: totalDutyAdded,
            EnthalpyDeltaWatts: enthalpyDelta,
            RelativeEnthalpyError: relativeEnthalpyError,
            IsMassBalanced: isGlobalMassBalanced,
            IsEnergyBalanced: isGlobalEnergyBalanced
        );

        // 3. Component Species Balances
        var uniqueCompounds = _streams.Values
            .SelectMany(s => s.Compounds)
            .DistinctBy(c => c.Id)
            .ToList();

        var componentSummaries = new List<ComponentBalanceSummary>(uniqueCompounds.Count);
        foreach (var compound in uniqueCompounds)
        {
            double feedMolar = 0.0;
            foreach (var feed in externalFeeds)
            {
                int idx = -1;
                for (int i = 0; i < feed.Compounds.Count; i++)
                {
                    if (string.Equals(feed.Compounds[i].Id, compound.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0)
                    feedMolar += feed.GetComponentMolarFlowMolPerSec(idx);
            }

            double prodMolar = 0.0;
            foreach (var prod in finalProducts)
            {
                int idx = -1;
                for (int i = 0; i < prod.Compounds.Count; i++)
                {
                    if (string.Equals(prod.Compounds[i].Id, compound.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0)
                    prodMolar += prod.GetComponentMolarFlowMolPerSec(idx);
            }

            double compMolarDelta = feedMolar - prodMolar;
            double feedMass = feedMolar * compound.MolarMass * 1e-3;
            double prodMass = prodMolar * compound.MolarMass * 1e-3;
            double compMassDelta = feedMass - prodMass;
            double relCompMassErr = feedMass > 1e-12 ? Math.Abs(compMassDelta) / feedMass : 0.0;
            bool isCompConserved = relCompMassErr < 1e-5 || Math.Abs(compMassDelta) < 1e-6;

            componentSummaries.Add(new ComponentBalanceSummary(
                CompoundId: compound.Id,
                CompoundName: compound.Name,
                MolarMassGPerMol: compound.MolarMass,
                FeedMolarFlowMolPerSec: feedMolar,
                ProductMolarFlowMolPerSec: prodMolar,
                MolarDeltaMolPerSec: compMolarDelta,
                FeedMassFlowKgPerSec: feedMass,
                ProductMassFlowKgPerSec: prodMass,
                MassDeltaKgPerSec: compMassDelta,
                RelativeMassError: relCompMassErr,
                IsConserved: isCompConserved
            ));
        }

        // 4. Equipment Unit Local Balances
        var unitSummaries = new List<UnitBalanceSummary>(_units.Count);
        foreach (IUnitOperation unit in _units.Values)
        {
            double inMass = unit.InletStreams.Sum(s => s.MassFlowRateKgPerSec);
            double outMass = unit.OutletStreams.Sum(s => s.MassFlowRateKgPerSec);
            double unitMassDelta = inMass - outMass;
            double relUnitMassErr = inMass > 1e-12 ? Math.Abs(unitMassDelta) / inMass : 0.0;
            bool isUnitMassBalanced = relUnitMassErr < 1e-5 || Math.Abs(unitMassDelta) < 1e-6;

            double inEnthalpy = unit.InletStreams.Sum(s => s.TotalEnthalpyFlowWatts);
            double outEnthalpy = unit.OutletStreams.Sum(s => s.TotalEnthalpyFlowWatts);
            double duty = unit.EnergyDutyWatts;
            double unitEnthalpyDelta = (inEnthalpy + duty) - outEnthalpy;
            double unitEnergyScale = Math.Abs(inEnthalpy) + Math.Abs(duty) + Math.Abs(outEnthalpy);
            double relUnitEnthalpyErr = unitEnergyScale > 1e-6 ? Math.Abs(unitEnthalpyDelta) / unitEnergyScale : 0.0;
            bool isUnitEnergyBalanced = relUnitEnthalpyErr < 5e-4 || Math.Abs(unitEnthalpyDelta) < 1.0;

            unitSummaries.Add(new UnitBalanceSummary(
                UnitId: unit.Id,
                UnitName: unit.Name,
                UnitType: unit.UnitType,
                InletMassFlowKgPerSec: inMass,
                OutletMassFlowKgPerSec: outMass,
                MassDeltaKgPerSec: unitMassDelta,
                RelativeMassError: relUnitMassErr,
                InletEnthalpyWatts: inEnthalpy,
                OutletEnthalpyWatts: outEnthalpy,
                HeatDutyWatts: duty,
                EnthalpyDeltaWatts: unitEnthalpyDelta,
                RelativeEnthalpyError: relUnitEnthalpyErr,
                IsMassBalanced: isUnitMassBalanced,
                IsEnergyBalanced: isUnitEnergyBalanced
            ));
        }

        // 5. Boundary Feeds and Products Stream Summaries
        var feedSummaries = externalFeeds.Select(s => new StreamBalanceSummary(
            StreamId: s.Id,
            StreamName: s.Name,
            Direction: "Feed",
            MolarFlowMolPerSec: s.MolarFlowRateMolPerSec,
            MassFlowKgPerSec: s.MassFlowRateKgPerSec,
            TemperatureKelvin: s.TemperatureKelvin,
            PressureKpa: s.PressureKpa,
            Phase: s.IsSolved ? s.Phase.ToString() : "Unsolved",
            EnthalpyWatts: s.IsSolved ? s.TotalEnthalpyFlowWatts : 0.0
        )).ToList();

        var productSummaries = finalProducts.Select(s => new StreamBalanceSummary(
            StreamId: s.Id,
            StreamName: s.Name,
            Direction: "Product",
            MolarFlowMolPerSec: s.MolarFlowRateMolPerSec,
            MassFlowKgPerSec: s.MassFlowRateKgPerSec,
            TemperatureKelvin: s.TemperatureKelvin,
            PressureKpa: s.PressureKpa,
            Phase: s.IsSolved ? s.Phase.ToString() : "Unsolved",
            EnthalpyWatts: s.IsSolved ? s.TotalEnthalpyFlowWatts : 0.0
        )).ToList();

        bool overallMassConserved = isGlobalMassBalanced && unitSummaries.All(u => u.IsMassBalanced);
        bool overallEnergyConserved = isGlobalEnergyBalanced && unitSummaries.All(u => u.IsEnergyBalanced);

        return new FlowsheetBalanceReport(
            FlowsheetName: Name,
            ThermodynamicModel: ThermodynamicPackage.Name,
            GeneratedAtUtc: DateTime.UtcNow,
            GlobalBalance: globalSummary,
            ComponentBalances: componentSummaries,
            UnitBalances: unitSummaries,
            ExternalFeeds: feedSummaries,
            FinalProducts: productSummaries,
            IsMassConserved: overallMassConserved,
            IsEnergyConserved: overallEnergyConserved
        );
    }

    private static string EscapeDot(string str) =>
        str.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");

    private static string SanitizeDotId(string id) =>
        id.Replace("-", "_").Replace(".", "_").Replace(":", "_").Replace("/", "_").Replace(" ", "_");
}
