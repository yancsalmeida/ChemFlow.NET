namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Immutable result of an isothermal (T, P) flash calculation.
/// </summary>
public sealed record FlashResult(
    PhaseState Phase,
    double TemperatureKelvin,
    double PressureKpa,
    double VaporFractionBeta,
    IReadOnlyList<double> OverallCompositionZ,
    IReadOnlyList<double> LiquidCompositionX,
    IReadOnlyList<double> VaporCompositionY,
    IReadOnlyList<double> KValues,
    double MolarEnthalpyJPerMol,
    int Iterations
);
