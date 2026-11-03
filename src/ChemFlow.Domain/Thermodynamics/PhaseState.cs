namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Represents the thermodynamic phase state of a stream or equilibrium calculation.
/// </summary>
public enum PhaseState
{
    /// <summary>Single-phase liquid (β = 0, P &gt;= P_bubble).</summary>
    SubcooledLiquid = 0,

    /// <summary>Two-phase vapor-liquid equilibrium (0 &lt; β &lt; 1, P_dew &lt; P &lt; P_bubble).</summary>
    VaporLiquidEquilibrium = 1,

    /// <summary>Single-phase vapor (β = 1, P &lt;= P_dew).</summary>
    SuperheatedVapor = 2
}
