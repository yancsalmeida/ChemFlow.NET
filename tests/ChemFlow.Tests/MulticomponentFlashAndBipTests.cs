using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Tests;

public class MulticomponentFlashAndBipTests
{
    private readonly IReadOnlyList<ChemicalCompound> _quaternarySystem =
    [
        DefaultCompounds.NHexane,
        DefaultCompounds.Benzene,
        DefaultCompounds.NHeptane,
        DefaultCompounds.Toluene
    ];

    [Fact]
    public void BinaryInteractionMatrix_SymmetryAndDiagonalZero_EnforcedStrictly()
    {
        var bim = new BinaryInteractionMatrix();
        bim.Set("n-C6H14", "n-C7H16", 0.0035);

        // Symmetry: k_ij == k_ji
        Assert.Equal(0.0035, bim.Get("n-C6H14", "n-C7H16"));
        Assert.Equal(0.0035, bim.Get("n-C7H16", "n-C6H14"));

        // Diagonal: k_ii == 0.0
        bim.Set("n-C6H14", "n-C6H14", 0.05); // Attempting to set diagonal
        Assert.Equal(0.0, bim.Get("n-C6H14", "n-C6H14"));

        // Unspecified pair defaults to 0.0
        Assert.Equal(0.0, bim.Get("C6H6", "n-C6H14"));
    }

    [Fact]
    public void BinaryInteractionMatrix_ToDenseMatrix_GeneratesCorrectDimensionsAndValues()
    {
        var bim = BinaryInteractionMatrix.CreateDefaultIndustrialMatrix();
        double[,] matrix = bim.ToMatrix(_quaternarySystem);

        int n = _quaternarySystem.Count;
        Assert.Equal(n, matrix.GetLength(0));
        Assert.Equal(n, matrix.GetLength(1));

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(0.0, matrix[i, i]); // Diagonals must be 0
            for (int j = 0; j < n; j++)
            {
                Assert.Equal(matrix[i, j], matrix[j, i]); // Symmetric
            }
        }

        // Verify specific literature values
        int idxHexane = 0;
        int idxBenzene = 1;
        Assert.Equal(0.0125, matrix[idxHexane, idxBenzene]);
    }

    [Fact]
    public void WilsonKValues_AtGivenTemperatureAndPressure_OrderComponentsByVolatility()
    {
        // 4-component mixture: n-Hexane (lightest), Benzene, n-Heptane, Toluene (heaviest)
        double tempK = 373.15; // 100 °C
        double pressKpa = 101.325; // 1 atm

        double[] kValues = MulticomponentIsothermalFlashSolver.CalculateWilsonKValues(
            _quaternarySystem, tempK, pressKpa);

        Assert.Equal(4, kValues.Length);

        // Volatility ordering: K(n-Hexane) > K(Benzene) > K(n-Heptane) > K(Toluene)
        Assert.True(kValues[0] > kValues[1], "n-Hexane must be more volatile than Benzene");
        Assert.True(kValues[1] > kValues[2], "Benzene must be more volatile than n-Heptane");
        Assert.True(kValues[2] > kValues[3], "n-Heptane must be more volatile than Toluene");

        // Light components above boiling point at 100°C should have K > 1
        Assert.True(kValues[0] > 1.0); // n-Hexane (Tb = 68.7 °C)
        Assert.True(kValues[1] > 1.0); // Benzene (Tb = 80.1 °C)
    }

    [Fact]
    public void MulticomponentFlash_QuaternaryEquimolarMixture_SolvesAccuratelyAndConservesMass()
    {
        // Quaternary equimolar mixture: n-Hexane (25%), Benzene (25%), n-Heptane (25%), Toluene (25%)
        // Operating at 90 °C (363.15 K) and 101.325 kPa (two-phase VLE region)
        var bim = BinaryInteractionMatrix.CreateDefaultIndustrialMatrix();
        var solver = new MulticomponentIsothermalFlashSolver(bim);

        double[] z = [0.25, 0.25, 0.25, 0.25];
        double tempK = 360.0;
        double pressKpa = 101.325;
        FlashResult result = solver.Solve(_quaternarySystem, z, tempK, pressKpa);

        Assert.Equal(PhaseState.VaporLiquidEquilibrium, result.Phase);
        Assert.True(result.VaporFractionBeta > 0.1 && result.VaporFractionBeta < 0.9);
        Assert.True(result.Iterations > 0);

        // 1. Total mole fraction normalization
        Assert.Equal(1.0, result.LiquidCompositionX.Sum(), precision: 8);
        Assert.Equal(1.0, result.VaporCompositionY.Sum(), precision: 8);

        // 2. Component mass balance: z_i = (1 - β)*x_i + β*y_i
        for (int i = 0; i < z.Length; i++)
        {
            double reconstructedZ = (1.0 - result.VaporFractionBeta) * result.LiquidCompositionX[i] +
                                    result.VaporFractionBeta * result.VaporCompositionY[i];
            Assert.Equal(z[i], reconstructedZ, precision: 6);
        }

        // 3. Equilibrium partition ratio: y_i = K_i * x_i
        for (int i = 0; i < z.Length; i++)
        {
            double expectedY = result.KValues[i] * result.LiquidCompositionX[i];
            Assert.Equal(expectedY, result.VaporCompositionY[i], precision: 6);
        }

        // 4. Relative volatility check: vapor enriched in lights, liquid enriched in heavies
        Assert.True(result.VaporCompositionY[0] > result.LiquidCompositionX[0]); // n-Hexane
        Assert.True(result.LiquidCompositionX[3] > result.VaporCompositionY[3]); // Toluene
    }

    [Fact]
    public void BinaryInteractionParameters_ShiftPhaseEquilibrium_DemonstratingNonIdealInteractions()
    {
        // Compare flash of Hexane + Heptane without BIP (k_ij = 0.0) vs with literature BIP (k_ij = 0.0035)
        IReadOnlyList<ChemicalCompound> binary = [DefaultCompounds.NHexane, DefaultCompounds.NHeptane];
        double[] z = [0.5, 0.5];
        double tempK = 356.0; // 82.85 °C (inside VLE envelope between Tb_hexane=68.7°C and Tb_heptane=98.4°C)
        double pressKpa = 101.325;

        var solverIdealPR = new MulticomponentIsothermalFlashSolver(BinaryInteractionMatrix.Empty);

        var bimNonIdeal = new BinaryInteractionMatrix();
        bimNonIdeal.Set("n-C6H14", "n-C7H16", 0.0035);
        var solverNonIdealPR = new MulticomponentIsothermalFlashSolver(bimNonIdeal);

        FlashResult resIdeal = solverIdealPR.Solve(binary, z, tempK, pressKpa);
        FlashResult resNonIdeal = solverNonIdealPR.Solve(binary, z, tempK, pressKpa);

        // Both must be two-phase VLE
        Assert.Equal(PhaseState.VaporLiquidEquilibrium, resIdeal.Phase);
        Assert.Equal(PhaseState.VaporLiquidEquilibrium, resNonIdeal.Phase);

        // Positive k_ij represents unfavorable cross-interaction (higher activity/volatility of mixture components)
        // therefore vapor fraction β with positive k_ij should be higher than ideal (k_ij = 0)
        Assert.True(resNonIdeal.VaporFractionBeta > resIdeal.VaporFractionBeta,
            $"Expected non-ideal beta ({resNonIdeal.VaporFractionBeta:F4}) > ideal beta ({resIdeal.VaporFractionBeta:F4})");
    }

    [Fact]
    public void MichelsenPhaseStability_DetectsEquilibriumInstabilityInTwoPhaseRegion()
    {
        double[] z = [0.5, 0.5];
        double tempK = 368.15;
        double pressKpa = 101.325;

        var pkg = new PengRobinsonThermodynamicPackage();
        double[,] kij = new double[2, 2];
        double[] wilsonK = MulticomponentIsothermalFlashSolver.CalculateWilsonKValues(
            [DefaultCompounds.Benzene, DefaultCompounds.Toluene], tempK, pressKpa);

        double[] ComputeFugacity(IReadOnlyList<double> x, bool isVapor) =>
            MulticomponentIsothermalFlashSolver.CalculateFugacityCoefficients(
                [DefaultCompounds.Benzene, DefaultCompounds.Toluene], x, tempK, pressKpa, kij, isVapor);

        var stability = MichelsenPhaseStability.Evaluate(
            [DefaultCompounds.Benzene, DefaultCompounds.Toluene],
            z,
            tempK,
            pressKpa,
            wilsonK,
            ComputeFugacity
        );

        // Equimolar Benzene/Toluene at 95 °C, 1 atm must be two-phase
        Assert.True(stability.IsTwoPhase);
        Assert.True(stability.RefinedKValues[0] > 1.0); // Benzene K > 1
        Assert.True(stability.RefinedKValues[1] < 1.0); // Toluene K < 1
    }

    [Fact]
    public void PengRobinsonPackage_IntegrationWithInteractionMatrix_MaintainsConsistency()
    {
        var bim = BinaryInteractionMatrix.CreateDefaultIndustrialMatrix();
        var pr = new PengRobinsonThermodynamicPackage(bim);

        Assert.NotNull(pr.InteractionMatrix);
        Assert.Equal(0.0030, pr.InteractionMatrix.Get("n-C6H14", "n-C7H16"));

        double[] z = [0.4, 0.6];
        // At 360.0 K (86.85 °C), mixture is inside VLE envelope (between Tb_hexane=68.7°C and Tb_heptane=98.4°C)
        FlashResult flash = pr.CalculateIsothermalFlash(
            [DefaultCompounds.NHexane, DefaultCompounds.NHeptane],
            z,
            360.0,
            101.325);

        Assert.Equal(PhaseState.VaporLiquidEquilibrium, flash.Phase);
        Assert.True(flash.VaporFractionBeta > 0.0 && flash.VaporFractionBeta < 1.0);
    }
}
