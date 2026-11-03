namespace ChemFlow.Domain.ValueObjects;

/// <summary>
/// Represents the empirical constants (A, B, C) for the Antoine vapor pressure equation:
/// ln(P_sat [kPa]) = A - B / (T [K] + C)
/// </summary>
/// <remarks>
/// Implemented as a C# <c>readonly record struct</c> (Value Object):
/// - Allocated on the stack (no Heap/GC overhead, similar to a C struct)
/// - Immutable by design
/// - Value-based equality out of the box
/// </remarks>
public readonly record struct AntoineCoefficients
{
    public double A { get; }
    public double B { get; }
    public double C { get; }

    public AntoineCoefficients(double a, double b, double c)
    {
        if (b <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(b),
                "Constant B must be strictly positive so that vapor pressure increases with temperature.");
        }

        A = a;
        B = b;
        C = c;
    }

    /// <summary>
    /// Calculates the saturation vapor pressure P_sat (in kPa) at a given temperature T (in Kelvin).
    /// Formula: P_sat = exp(A - B / (T + C))
    /// </summary>
    public double CalculateSaturationPressureKpa(double temperatureKelvin)
    {
        if (temperatureKelvin <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureKelvin),
                "Absolute temperature must be greater than 0 K.");
        }

        double denominator = temperatureKelvin + C;
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureKelvin),
                $"Temperature {temperatureKelvin} K is outside the valid physical range for these Antoine coefficients (T + C <= 0).");
        }

        return Math.Exp(A - (B / denominator));
    }

    /// <summary>
    /// Inverse Antoine equation: calculates the saturation (boiling) temperature T_sat (in Kelvin)
    /// at a given pressure P (in kPa).
    /// Formula: T_sat = B / (A - ln(P)) - C
    /// </summary>
    public double CalculateSaturationTemperatureKelvin(double pressureKpa)
    {
        if (pressureKpa <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pressureKpa),
                "Pressure must be strictly positive (> 0 kPa).");
        }

        double denominator = A - Math.Log(pressureKpa);
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pressureKpa),
                "Pressure exceeds the valid range of the Antoine correlation.");
        }

        return (B / denominator) - C;
    }
}
