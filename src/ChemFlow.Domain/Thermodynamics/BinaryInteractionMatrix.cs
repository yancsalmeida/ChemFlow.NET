using ChemFlow.Domain.Entities;

namespace ChemFlow.Domain.Thermodynamics;

/// <summary>
/// Encapsulates the symmetric binary interaction parameter matrix (k_ij = k_ji, k_ii = 0)
/// utilized in van der Waals quadratic mixing rules for cubic equations of state:
///   a_ij = sqrt(a_i * a_j) * (1 - k_ij)
/// </summary>
public sealed class BinaryInteractionMatrix
{
    private readonly Dictionary<(string, string), double> _parameters = new();

    /// <summary>
    /// Creates an empty binary interaction matrix (all k_ij = 0.0 by default).
    /// </summary>
    public BinaryInteractionMatrix()
    {
    }

    /// <summary>
    /// Creates a binary interaction matrix from an existing dictionary of pairs.
    /// </summary>
    public BinaryInteractionMatrix(IDictionary<(string, string), double> parameters)
    {
        if (parameters is null) return;
        foreach (var (pair, val) in parameters)
        {
            Set(pair.Item1, pair.Item2, val);
        }
    }

    /// <summary>
    /// Creates a binary interaction matrix from a dense 2D array matching the specified compounds list.
    /// </summary>
    public BinaryInteractionMatrix(IReadOnlyList<ChemicalCompound> compounds, double[,] denseMatrix)
    {
        if (compounds is null) throw new ArgumentNullException(nameof(compounds));
        if (denseMatrix is null) throw new ArgumentNullException(nameof(denseMatrix));

        int n = compounds.Count;
        if (denseMatrix.GetLength(0) != n || denseMatrix.GetLength(1) != n)
        {
            throw new ArgumentException(
                $"Matrix dimensions ({denseMatrix.GetLength(0)}x{denseMatrix.GetLength(1)}) must match compounds count ({n}x{n}).",
                nameof(denseMatrix));
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i != j)
                {
                    Set(compounds[i].Id, compounds[j].Id, denseMatrix[i, j]);
                }
            }
        }
    }

    /// <summary>
    /// Gets the binary interaction parameter k_ij between two compounds by ID. Returns 0.0 if not configured.
    /// </summary>
    public double Get(string idA, string idB)
    {
        if (string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase))
            return 0.0;

        (string, string) key = MakeKey(idA, idB);
        return _parameters.TryGetValue(key, out double val) ? val : 0.0;
    }

    /// <summary>
    /// Gets the binary interaction parameter k_ij between two compounds. Returns 0.0 if not configured.
    /// </summary>
    public double Get(ChemicalCompound compoundA, ChemicalCompound compoundB) =>
        Get(compoundA.Id, compoundB.Id);

    /// <summary>
    /// Sets the binary interaction parameter k_ij (enforcing symmetry k_ij = k_ji, with k_ii = 0).
    /// </summary>
    public BinaryInteractionMatrix Set(string idA, string idB, double kij)
    {
        if (string.IsNullOrWhiteSpace(idA)) throw new ArgumentException("Compound ID cannot be null or empty.", nameof(idA));
        if (string.IsNullOrWhiteSpace(idB)) throw new ArgumentException("Compound ID cannot be null or empty.", nameof(idB));

        if (string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase))
            return this; // k_ii is strictly 0.0

        if (kij < -1.0 || kij > 1.0)
            throw new ArgumentOutOfRangeException(nameof(kij), "Binary interaction parameter k_ij must be between -1.0 and 1.0.");

        _parameters[MakeKey(idA, idB)] = kij;
        return this;
    }

    /// <summary>
    /// Sets the binary interaction parameter k_ij between two compounds.
    /// </summary>
    public BinaryInteractionMatrix Set(ChemicalCompound compoundA, ChemicalCompound compoundB, double kij) =>
        Set(compoundA.Id, compoundB.Id, kij);

    /// <summary>
    /// Generates a dense n x n array of interaction parameters for the given list of compounds.
    /// </summary>
    public double[,] ToMatrix(IReadOnlyList<ChemicalCompound> compounds)
    {
        if (compounds is null || compounds.Count == 0)
            return new double[0, 0];

        int n = compounds.Count;
        double[,] matrix = new double[n, n];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                matrix[i, j] = Get(compounds[i].Id, compounds[j].Id);
            }
        }

        return matrix;
    }

    /// <summary>
    /// Creates a deep copy of this binary interaction matrix.
    /// </summary>
    public BinaryInteractionMatrix Clone() => new(_parameters);

    /// <summary>
    /// Empty matrix with all k_ij = 0.0 (ideal mixing assumption).
    /// </summary>
    public static BinaryInteractionMatrix Empty => new();

    /// <summary>
    /// Factory providing validated literature binary interaction parameters for common industrial hydrocarbon and polar mixtures
    /// (DECHEMA Chemistry Data Series / Knapp et al. Vapor-Liquid Equilibria for Mixtures of Low Boiling Substances).
    /// </summary>
    public static BinaryInteractionMatrix CreateDefaultIndustrialMatrix()
    {
        var bim = new BinaryInteractionMatrix();

        // Hydrocarbon - Hydrocarbon pairs (aliphatic / aromatic)
        bim.Set("n-C6H14", "n-C7H16", 0.0030); // n-Hexane / n-Heptane
        bim.Set("C6H6", "C7H8", 0.0000);       // Benzene / Toluene (ideal regular solution)
        bim.Set("n-C6H14", "C6H6", 0.0125);    // n-Hexane / Benzene
        bim.Set("n-C6H14", "C7H8", 0.0110);    // n-Hexane / Toluene
        bim.Set("n-C7H16", "C6H6", 0.0130);    // n-Heptane / Benzene
        bim.Set("n-C7H16", "C7H8", 0.0095);    // n-Heptane / Toluene

        // Polar / Alcohol / Water pairs
        bim.Set("H2O", "C2H5OH", -0.0750);     // Water / Ethanol (strong hydrogen bonding cross-association)
        bim.Set("H2O", "C6H6", 0.2200);        // Water / Benzene (highly non-ideal liquid immiscibility gap)
        bim.Set("H2O", "C7H8", 0.2400);        // Water / Toluene
        bim.Set("H2O", "n-C6H14", 0.4900);     // Water / n-Hexane
        bim.Set("H2O", "n-C7H16", 0.5000);     // Water / n-Heptane

        // Ethanol / Hydrocarbons
        bim.Set("C2H5OH", "n-C6H14", 0.0650);  // Ethanol / n-Hexane
        bim.Set("C2H5OH", "C6H6", 0.0380);     // Ethanol / Benzene
        bim.Set("C2H5OH", "C7H8", 0.0420);     // Ethanol / Toluene

        return bim;
    }

    private static (string, string) MakeKey(string a, string b)
    {
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? (a.ToUpperInvariant(), b.ToUpperInvariant())
            : (b.ToUpperInvariant(), a.ToUpperInvariant());
    }
}
