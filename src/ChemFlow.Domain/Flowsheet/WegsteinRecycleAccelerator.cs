using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Flowsheet;

/// <summary>
/// Stream state vector capturing all numerical degrees of freedom for tear stream convergence.
/// </summary>
public sealed record StreamStateVector(
    double MolarFlowRateMolPerSec,
    double TemperatureKelvin,
    double PressureKpa,
    double[] CompositionZ
)
{
    public static StreamStateVector FromStream(MaterialStream stream)
    {
        double[] z = new double[stream.CompositionZ.Count];
        for (int i = 0; i < z.Length; i++)
            z[i] = stream.CompositionZ[i];

        return new StreamStateVector(
            stream.MolarFlowRateMolPerSec,
            stream.TemperatureKelvin,
            stream.PressureKpa,
            z
        );
    }

    public void ApplyToStream(MaterialStream stream, IThermodynamicPackage? thermo = null)
    {
        double[] normalizedZ = new double[CompositionZ.Length];
        double sum = 0.0;
        for (int i = 0; i < normalizedZ.Length; i++)
        {
            normalizedZ[i] = Math.Max(0.0, CompositionZ[i]);
            sum += normalizedZ[i];
        }

        if (sum > 0.0)
        {
            for (int i = 0; i < normalizedZ.Length; i++)
                normalizedZ[i] /= sum;
        }

        stream.UpdateState(
            molarFlowRateMolPerSec: Math.Max(0.0, MolarFlowRateMolPerSec),
            temperatureKelvin: Math.Max(50.0, TemperatureKelvin),
            pressureKpa: Math.Max(1.0, PressureKpa),
            compositionZ: normalizedZ,
            thermo: thermo
        );
    }
}

/// <summary>
/// Implements the Wegstein Bounded Secant Convergence Acceleration algorithm,
/// the chemical engineering gold standard for solving closed-loop process recycles.
/// </summary>
public sealed class WegsteinRecycleAccelerator
{
    private readonly RecycleSolverOptions _options;
    private readonly Dictionary<string, (StreamStateVector Guess, StreamStateVector Calculated)> _history = new(StringComparer.OrdinalIgnoreCase);

    public WegsteinRecycleAccelerator(RecycleSolverOptions? options = null)
    {
        _options = options ?? RecycleSolverOptions.Default;
    }

    public void Reset()
    {
        _history.Clear();
    }

    /// <summary>
    /// Computes the accelerated tear stream state estimate for iteration k + 1 given current guess x_k and calculated y_k.
    /// </summary>
    public StreamStateVector Accelerate(string streamId, StreamStateVector guessX, StreamStateVector calculatedY, int iteration)
    {
        if (iteration <= 1 || _options.Method == RecycleConvergenceMethod.DirectSubstitution || !_history.TryGetValue(streamId, out var prev))
        {
            // First iteration or Direct Substitution: x^(k+1) = y^(k)
            _history[streamId] = (guessX, calculatedY);
            return calculatedY;
        }

        StreamStateVector prevX = prev.Guess;
        StreamStateVector prevY = prev.Calculated;

        // Accelerate molar flow rate
        double nextFlow = AccelerateScalar(
            prevX.MolarFlowRateMolPerSec,
            guessX.MolarFlowRateMolPerSec,
            prevY.MolarFlowRateMolPerSec,
            calculatedY.MolarFlowRateMolPerSec,
            minVal: 0.0
        );

        // Accelerate temperature
        double nextTemp = AccelerateScalar(
            prevX.TemperatureKelvin,
            guessX.TemperatureKelvin,
            prevY.TemperatureKelvin,
            calculatedY.TemperatureKelvin,
            minVal: 50.0
        );

        // Accelerate composition fractions
        int n = guessX.CompositionZ.Length;
        double[] nextZ = new double[n];
        double sumZ = 0.0;
        for (int i = 0; i < n; i++)
        {
            double prevXi = i < prevX.CompositionZ.Length ? prevX.CompositionZ[i] : guessX.CompositionZ[i];
            double prevYi = i < prevY.CompositionZ.Length ? prevY.CompositionZ[i] : calculatedY.CompositionZ[i];

            nextZ[i] = AccelerateScalar(
                prevXi,
                guessX.CompositionZ[i],
                prevYi,
                calculatedY.CompositionZ[i],
                minVal: 0.0
            );
            sumZ += nextZ[i];
        }

        if (sumZ > 0.0)
        {
            for (int i = 0; i < n; i++)
                nextZ[i] /= sumZ;
        }

        // Pressure is set directly by upstream equipment
        double nextPressure = calculatedY.PressureKpa;

        _history[streamId] = (guessX, calculatedY);

        return new StreamStateVector(nextFlow, nextTemp, nextPressure, nextZ);
    }

    private double AccelerateScalar(double prevX, double curX, double prevY, double curY, double minVal = 0.0)
    {
        double dx = curX - prevX;
        double dy = curY - prevY;

        double q;
        if (Math.Abs(dx) < 1e-12)
        {
            q = 0.0; // Direct substitution
        }
        else
        {
            double s = dy / dx;
            // Bounded secant Wegstein weight q = s / (s - 1)
            if (Math.Abs(s - 1.0) < 1e-6)
            {
                q = _options.WegsteinLowerBound;
            }
            else
            {
                q = s / (s - 1.0);
            }
        }

        // Apply bounded Wegstein clamps [WegsteinLowerBound, WegsteinUpperBound]
        q = Math.Max(_options.WegsteinLowerBound, Math.Min(_options.WegsteinUpperBound, q));

        // x^(k+1) = q * x^(k) + (1 - q) * y^(k)
        double nextVal = q * curX + (1.0 - q) * curY;

        // Apply relaxation damping factor if specified: x_damped = alpha * nextVal + (1 - alpha) * curX
        if (_options.DampingFactor < 1.0 && _options.DampingFactor > 0.0)
        {
            nextVal = _options.DampingFactor * nextVal + (1.0 - _options.DampingFactor) * curX;
        }

        return Math.Max(minVal, nextVal);
    }

    /// <summary>
    /// Checks convergence between guess state x and calculated state y.
    /// </summary>
    public TearStreamConvergenceRecord CheckConvergence(string streamId, StreamStateVector guessX, StreamStateVector calculatedY, int iteration)
    {
        double flowDelta = Math.Abs(calculatedY.MolarFlowRateMolPerSec - guessX.MolarFlowRateMolPerSec);
        double flowScale = Math.Max(1.0, Math.Max(calculatedY.MolarFlowRateMolPerSec, guessX.MolarFlowRateMolPerSec));
        double relFlowError = flowDelta / flowScale;

        double tempDelta = Math.Abs(calculatedY.TemperatureKelvin - guessX.TemperatureKelvin);

        double maxCompositionDelta = 0.0;
        int n = Math.Min(guessX.CompositionZ.Length, calculatedY.CompositionZ.Length);
        for (int i = 0; i < n; i++)
        {
            double dz = Math.Abs(calculatedY.CompositionZ[i] - guessX.CompositionZ[i]);
            if (dz > maxCompositionDelta)
                maxCompositionDelta = dz;
        }

        bool isConverged = relFlowError <= _options.Tolerance
                           && tempDelta <= Math.Max(0.02, _options.Tolerance * 100.0)
                           && maxCompositionDelta <= Math.Max(1e-4, _options.Tolerance * 10.0);

        return new TearStreamConvergenceRecord(
            Iteration: iteration,
            StreamId: streamId,
            FlowErrorRelative: relFlowError,
            FlowDeltaMolPerSec: flowDelta,
            TemperatureDeltaKelvin: tempDelta,
            MaxCompositionDelta: maxCompositionDelta,
            IsConverged: isConverged
        );
    }
}
