using ChemFlow.Domain.Units;

namespace ChemFlow.Domain.Flowsheet;

/// <summary>
/// Mathematical convergence acceleration method for solving flowsheet recycle loops.
/// </summary>
public enum RecycleConvergenceMethod
{
    /// <summary>
    /// Direct substitution (Successive Substitution): x^(k+1) = y^(k).
    /// Simple and robust for mildly coupled systems, but may exhibit slow linear convergence.
    /// </summary>
    DirectSubstitution = 0,

    /// <summary>
    /// Wegstein Bounded Secant Acceleration: the chemical process industry standard (Aspen, HYSYS).
    /// Uses secant slopes s = (y_k - y_{k-1}) / (x_k - x_{k-1}) and bounded weight q = s / (s - 1).
    /// </summary>
    Wegstein = 1,

    /// <summary>
    /// Broyden Quasi-Newton method updating secant approximations to the inverse Jacobian.
    /// High-order convergence for tightly coupled multi-variable recycle networks.
    /// </summary>
    Broyden = 2
}

/// <summary>
/// Configuration options for flowsheet tear stream recycle solvers.
/// </summary>
public sealed record RecycleSolverOptions(
    RecycleConvergenceMethod Method = RecycleConvergenceMethod.Wegstein,
    int MaxIterations = 60,
    double Tolerance = 1e-5,
    double WegsteinLowerBound = -5.0,
    double WegsteinUpperBound = 0.0,
    double DampingFactor = 1.0
)
{
    public static RecycleSolverOptions Default { get; } = new();
}

/// <summary>
/// Progress snapshot of tear stream state convergence across iterations.
/// </summary>
public sealed record TearStreamConvergenceRecord(
    int Iteration,
    string StreamId,
    double FlowErrorRelative,
    double FlowDeltaMolPerSec,
    double TemperatureDeltaKelvin,
    double MaxCompositionDelta,
    bool IsConverged
);

/// <summary>
/// Comprehensive outcome of a flowsheet solve execution, including recycle convergence diagnostics.
/// </summary>
public sealed record FlowsheetSolveResult(
    string FlowsheetName,
    IReadOnlyList<IUnitOperation> ExecutionOrder,
    bool HasRecycle,
    int Iterations,
    bool IsConverged,
    IReadOnlyList<string> TearStreamIds,
    IReadOnlyList<TearStreamConvergenceRecord> ConvergenceHistory
);
