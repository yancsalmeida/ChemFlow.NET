namespace ChemFlow.Domain.Mathematics;

/// <summary>
/// Implements the Thomas Algorithm (TriDiagonal Matrix Algorithm - TDMA)
/// solving tridiagonal systems of linear equations in O(N) operations:
///   A[j] * x[j-1] + B[j] * x[j] + C[j] * x[j+1] = D[j],  for j = 0, ..., N-1.
/// Used in the Wang-Henke Bubble Point method for rigorous multicomponent distillation column solving.
/// </summary>
public static class ThomasAlgorithm
{
    /// <summary>
    /// Solves the tridiagonal system Ax = D.
    /// </summary>
    /// <param name="a">Subdiagonal elements of length N (a[0] is unused or 0).</param>
    /// <param name="b">Main diagonal elements of length N.</param>
    /// <param name="c">Superdiagonal elements of length N (c[N-1] is unused or 0).</param>
    /// <param name="d">Right-hand side vector of length N.</param>
    /// <returns>Solution vector x of length N.</returns>
    public static double[] Solve(ReadOnlySpan<double> a, ReadOnlySpan<double> b, ReadOnlySpan<double> c, ReadOnlySpan<double> d)
    {
        int n = b.Length;
        if (a.Length != n || c.Length != n || d.Length != n)
            throw new ArgumentException("All tridiagonal coefficient spans must have equal length N.");
        if (n == 0)
            return [];
        if (n == 1)
        {
            if (Math.Abs(b[0]) < 1e-15)
                throw new InvalidOperationException("Singular matrix in Thomas Algorithm.");
            return [d[0] / b[0]];
        }

        double[] cPrime = new double[n];
        double[] dPrime = new double[n];
        double[] x = new double[n];

        // Forward elimination
        if (Math.Abs(b[0]) < 1e-15)
            throw new InvalidOperationException("Zero pivot encountered at row 0 in Thomas Algorithm.");

        cPrime[0] = c[0] / b[0];
        dPrime[0] = d[0] / b[0];

        for (int i = 1; i < n; i++)
        {
            double m = b[i] - a[i] * cPrime[i - 1];
            if (Math.Abs(m) < 1e-15)
                throw new InvalidOperationException($"Zero pivot encountered at row {i} in Thomas Algorithm.");

            cPrime[i] = c[i] / m;
            dPrime[i] = (d[i] - a[i] * dPrime[i - 1]) / m;
        }

        // Back substitution
        x[n - 1] = dPrime[n - 1];
        for (int i = n - 2; i >= 0; i--)
        {
            x[i] = dPrime[i] - cPrime[i] * x[i + 1];
        }

        return x;
    }
}
