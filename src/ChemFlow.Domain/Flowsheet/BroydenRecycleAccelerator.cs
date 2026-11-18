namespace ChemFlow.Domain.Flowsheet;

/// <summary>
/// Full-matrix Good Broyden Quasi-Newton tear-stream convergence accelerator with
/// dimensionless state scaling, Sherman-Morrison inverse Jacobian updates, and
/// trust-region step safeguards for tightly coupled reaction-separation recycles.
/// </summary>
public sealed class BroydenRecycleAccelerator : IRecycleAccelerator
{
    private sealed class StreamBroydenState
    {
        public required double FlowScale;
        public required double[] LastU;
        public required double[] LastR;
        public required double[,] InverseJacobian;
    }

    private readonly RecycleSolverOptions _options;
    private readonly Dictionary<string, StreamBroydenState> _states = new(StringComparer.OrdinalIgnoreCase);

    public BroydenRecycleAccelerator(RecycleSolverOptions? options = null)
    {
        _options = options ?? RecycleSolverOptions.Default;
    }

    public void Reset() => _states.Clear();

    public TearStreamConvergenceRecord CheckConvergence(
        string streamId,
        StreamStateVector guessX,
        StreamStateVector calculatedY,
        int iteration) =>
        RecycleConvergenceHelper.EvaluateConvergence(_options, streamId, guessX, calculatedY, iteration);

    public StreamStateVector Accelerate(
        string streamId,
        StreamStateVector guessX,
        StreamStateVector calculatedY,
        int iteration)
    {
        int nComp = guessX.CompositionZ.Length;
        int dim = 2 + nComp;

        if (!_states.TryGetValue(streamId, out StreamBroydenState? state) ||
            state.LastU.Length != dim ||
            iteration <= 1)
        {
            double flowScale = Math.Max(1.0, Math.Max(guessX.MolarFlowRateMolPerSec, calculatedY.MolarFlowRateMolPerSec));
            double[] u0 = PackScaled(guessX, flowScale);
            double[] y0 = PackScaled(calculatedY, flowScale);
            double[] r0 = Subtract(y0, u0);

            _states[streamId] = new StreamBroydenState
            {
                FlowScale = flowScale,
                LastU = u0,
                LastR = r0,
                InverseJacobian = CreateScaledIdentity(dim, -1.0)
            };

            return calculatedY;
        }

        double[] u = PackScaled(guessX, state.FlowScale);
        double[] y = PackScaled(calculatedY, state.FlowScale);
        double[] r = Subtract(y, u);

        double[] s = Subtract(u, state.LastU);
        double[] df = Subtract(r, state.LastR);

        // Good Broyden Sherman-Morrison inverse Jacobian update:
        // H_{k} = H_{k-1} + ((s - H_{k-1} * df) * (s^T * H_{k-1})) / (s^T * H_{k-1} * df)
        double[] hDf = MultiplyMatVec(state.InverseJacobian, df);
        double[] sTH = MultiplyVecMat(s, state.InverseJacobian);
        double denom = Dot(s, hDf);
        double normS = Norm2(s);
        double normHDf = Norm2(hDf);

        if (normS > 1e-12 && normHDf > 1e-12 && Math.Abs(denom) > 1e-10 * normS * normHDf)
        {
            double[] numerLeft = Subtract(s, hDf);
            for (int i = 0; i < dim; i++)
            {
                double factor = numerLeft[i] / denom;
                for (int j = 0; j < dim; j++)
                {
                    state.InverseJacobian[i, j] += factor * sTH[j];
                }
            }
        }
        else if (Norm2(df) < 1e-12 && normS > 1e-8)
        {
            // Reset inverse Jacobian to -I (Direct Substitution fallback) if residual stagnates
            state.InverseJacobian = CreateScaledIdentity(dim, -1.0);
        }

        // Quasi-Newton update: u_{k+1} = u_k - damping * H_k * r_k
        double[] step = MultiplyMatVec(state.InverseJacobian, r);
        double damping = _options.DampingFactor is > 0.0 and <= 1.0 ? _options.DampingFactor : 1.0;

        // Trust-region safeguard: bound max coordinate step in scaled coordinates
        double maxAbsStep = 0.0;
        for (int i = 0; i < dim; i++)
        {
            double val = Math.Abs(step[i]);
            if (val > maxAbsStep) maxAbsStep = val;
        }

        const double maxTrustStep = 0.65;
        double stepScale = maxAbsStep > maxTrustStep ? maxTrustStep / maxAbsStep : 1.0;

        double[] nextU = new double[dim];
        for (int i = 0; i < dim; i++)
        {
            nextU[i] = u[i] - damping * stepScale * step[i];
        }

        // Guard divergence: if Newton step yields wildly negative flow/temp, blend conservatively with direct substitution
        if (nextU[0] < 0.0 || nextU[1] < 0.4 || nextU[1] > 5.0 || double.IsNaN(nextU[0]) || double.IsNaN(nextU[1]))
        {
            state.InverseJacobian = CreateScaledIdentity(dim, -1.0);
            state.LastU = u;
            state.LastR = r;
            return calculatedY;
        }

        state.LastU = u;
        state.LastR = r;

        return UnpackScaled(nextU, state.FlowScale, calculatedY.PressureKpa, nComp);
    }

    private static double[] PackScaled(StreamStateVector v, double flowScale)
    {
        double[] u = new double[2 + v.CompositionZ.Length];
        u[0] = v.MolarFlowRateMolPerSec / flowScale;
        u[1] = v.TemperatureKelvin / 300.0;
        for (int i = 0; i < v.CompositionZ.Length; i++)
            u[2 + i] = v.CompositionZ[i];
        return u;
    }

    private static StreamStateVector UnpackScaled(double[] u, double flowScale, double pressureKpa, int nComp)
    {
        double flow = Math.Max(0.0, u[0] * flowScale);
        double temp = Math.Clamp(u[1] * 300.0, 150.0, 1500.0);

        double[] z = new double[nComp];
        double sum = 0.0;
        for (int i = 0; i < nComp; i++)
        {
            z[i] = Math.Max(0.0, u[2 + i]);
            sum += z[i];
        }

        if (sum > 0.0)
        {
            for (int i = 0; i < nComp; i++)
                z[i] /= sum;
        }
        else if (nComp > 0)
        {
            z[0] = 1.0;
        }

        return new StreamStateVector(flow, temp, pressureKpa, z);
    }

    private static double[,] CreateScaledIdentity(int dim, double scale)
    {
        double[,] m = new double[dim, dim];
        for (int i = 0; i < dim; i++)
            m[i, i] = scale;
        return m;
    }

    private static double[] Subtract(double[] a, double[] b)
    {
        double[] res = new double[a.Length];
        for (int i = 0; i < a.Length; i++)
            res[i] = a[i] - b[i];
        return res;
    }

    private static double[] MultiplyMatVec(double[,] m, double[] v)
    {
        int n = v.Length;
        double[] res = new double[n];
        for (int i = 0; i < n; i++)
        {
            double acc = 0.0;
            for (int j = 0; j < n; j++)
                acc += m[i, j] * v[j];
            res[i] = acc;
        }
        return res;
    }

    private static double[] MultiplyVecMat(double[] v, double[,] m)
    {
        int n = v.Length;
        double[] res = new double[n];
        for (int j = 0; j < n; j++)
        {
            double acc = 0.0;
            for (int i = 0; i < n; i++)
                acc += v[i] * m[i, j];
            res[j] = acc;
        }
        return res;
    }

    private static double Dot(double[] a, double[] b)
    {
        double acc = 0.0;
        for (int i = 0; i < a.Length; i++)
            acc += a[i] * b[i];
        return acc;
    }

    private static double Norm2(double[] v) => Math.Sqrt(Dot(v, v));
}
