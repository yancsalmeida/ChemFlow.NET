using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Tests;

public class PengRobinsonThermodynamicsTests
{
    private readonly PengRobinsonThermodynamicPackage _prThermo = new();
    private readonly IReadOnlyList<ChemicalCompound> _benzeneToluene =
    [
        DefaultCompounds.Benzene,
        DefaultCompounds.Toluene
    ];
    private readonly IReadOnlyList<ChemicalCompound> _hexaneHeptane =
    [
        DefaultCompounds.NHexane,
        DefaultCompounds.NHeptane
    ];

    [Fact]
    public void SolveCubicCompressibility_AtLowPressure_YieldsIdealVaporCompressibilityNearUnity()
    {
        // At low pressure (e.g. A ≈ 0.001, B ≈ 0.0001), vapor Z must approach 1.0 (ideal gas limit)
        double A = 0.001;
        double B = 0.0001;

        (double zL, double zV) = PengRobinsonThermodynamicPackage.SolveCubicCompressibility(A, B);

        Assert.True(zV > 0.99 && zV <= 1.001);
    }

    [Fact]
    public void SolveCubicCompressibility_InTwoPhaseRegion_YieldsDistinctLiquidAndVaporRoots()
    {
        // For Benzene at 368.15 K and 101.325 kPa (saturation VLE envelope): A ≈ 0.0282, B ≈ 0.00246
        double A = 0.0282158;
        double B = 0.0024578;

        (double zL, double zV) = PengRobinsonThermodynamicPackage.SolveCubicCompressibility(A, B);

        Assert.True(zL > B, "Liquid Z must strictly exceed co-volume B");
        Assert.True(zV > zL, "Vapor Z must strictly exceed Liquid Z");
        Assert.InRange(zL, 0.0025, 0.010);
        Assert.InRange(zV, 0.90, 0.99);
    }

    [Fact]
    public void FugacityCoefficients_AtLowPressure_ApproachIdealUnity()
    {
        // Low pressure (P = 5.0 kPa): intermolecular forces are negligible, φ_i ≈ 1.0
        double[] z = [0.5, 0.5];
        double[] phiV = _prThermo.CalculateFugacityCoefficients(_benzeneToluene, z, 350.0, 5.0, isVapor: true);

        Assert.Equal(2, phiV.Length);
        Assert.InRange(phiV[0], 0.98, 1.02);
        Assert.InRange(phiV[1], 0.98, 1.02);
    }

    [Fact]
    public void IsothermalFlash_BenzeneTolueneEquimolar_ConservesMassAndMatchesVLE()
    {
        // Equimolar Benzene + Toluene at 95 °C (368.15 K) and 1 atm (101.325 kPa)
        double[] z = [0.5, 0.5];
        double tempK = 368.15;
        double pressKpa = 101.325;

        FlashResult result = _prThermo.CalculateIsothermalFlash(_benzeneToluene, z, tempK, pressKpa);

        Assert.Equal(PhaseState.VaporLiquidEquilibrium, result.Phase);
        Assert.InRange(result.VaporFractionBeta, 0.35, 0.65);
        Assert.True(result.Iterations > 0);

        // Volatility: Benzene is lighter than Toluene (y_benzene > x_benzene)
        Assert.True(result.VaporCompositionY[0] > result.LiquidCompositionX[0]);
        Assert.True(result.KValues[0] > 1.0);
        Assert.True(result.KValues[1] < 1.0);

        // Verify component mass balance: (1 - β)*x_i + β*y_i == z_i
        for (int i = 0; i < z.Length; i++)
        {
            double reconstructed = (1.0 - result.VaporFractionBeta) * result.LiquidCompositionX[i] +
                                   result.VaporFractionBeta * result.VaporCompositionY[i];
            Assert.Equal(z[i], reconstructed, precision: 6);
        }

        // Verify phase equilibrium: y_i == K_i * x_i
        for (int i = 0; i < z.Length; i++)
        {
            Assert.Equal(result.VaporCompositionY[i], result.KValues[i] * result.LiquidCompositionX[i], precision: 6);
        }
    }

    [Fact]
    public void IsothermalFlash_HighPressureSubcooledAndSuperheatedLimits_AreConsistent()
    {
        double[] z = [0.5, 0.5];
        double tempK = 368.15;

        // At 300 kPa, mixture should be subcooled liquid
        FlashResult subcooled = _prThermo.CalculateIsothermalFlash(_benzeneToluene, z, tempK, 300.0);
        Assert.Equal(PhaseState.SubcooledLiquid, subcooled.Phase);
        Assert.Equal(0.0, subcooled.VaporFractionBeta);

        // At 40 kPa, mixture should be superheated vapor
        FlashResult superheated = _prThermo.CalculateIsothermalFlash(_benzeneToluene, z, tempK, 40.0);
        Assert.Equal(PhaseState.SuperheatedVapor, superheated.Phase);
        Assert.Equal(1.0, superheated.VaporFractionBeta);
    }

    [Fact]
    public void HighPressureHydrocarbonVLE_HexaneHeptaneAt10Bar_ComputesNonIdealEquilibrium()
    {
        // High pressure non-ideal hydrocarbon mixture: n-Hexane (50%) + n-Heptane (50%) at 450 K and 1000 kPa (10 bar)
        double[] z = [0.5, 0.5];
        double tempK = 450.0;
        double pressKpa = 1000.0;

        FlashResult result = _prThermo.CalculateIsothermalFlash(_hexaneHeptane, z, tempK, pressKpa);

        // Flash should resolve physically and conserve mass
        Assert.True(result.VaporFractionBeta >= 0.0 && result.VaporFractionBeta <= 1.0);
        Assert.Equal(1.0, result.LiquidCompositionX.Sum(), precision: 6);
        Assert.Equal(1.0, result.VaporCompositionY.Sum(), precision: 6);

        // Fugacity coefficients at 10 bar must noticeably deviate from ideal gas unity
        double[] phiV = _prThermo.CalculateFugacityCoefficients(_hexaneHeptane, result.VaporCompositionY, tempK, pressKpa, isVapor: true);
        Assert.True(phiV[0] < 0.95 || phiV[0] > 1.05, "Vapor fugacity at 10 bar must show non-ideal real fluid behavior");
    }

    [Fact]
    public void PhaseEnthalpy_LiquidEnthalpyReflectsCohesiveAttractiveDeparture()
    {
        // Enthalpy of liquid Benzene/Toluene at 350 K, 101.325 kPa
        double[] x = [0.5, 0.5];
        double hLiquid = _prThermo.CalculatePhaseEnthalpy(_benzeneToluene, x, 350.0, 101.325, isVapor: false);
        double hVapor = _prThermo.CalculatePhaseEnthalpy(_benzeneToluene, x, 350.0, 101.325, isVapor: true);

        // Vapor enthalpy must exceed liquid enthalpy by roughly the latent heat of vaporization (~30,000 - 35,000 J/mol)
        double deltaH = hVapor - hLiquid;
        Assert.InRange(deltaH, 25000.0, 45000.0);
    }
}
