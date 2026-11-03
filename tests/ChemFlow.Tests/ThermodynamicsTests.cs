using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Tests;

public class ThermodynamicsTests
{
    private readonly IThermodynamicPackage _thermo = new RaoultThermodynamicPackage();
    private readonly IReadOnlyList<ChemicalCompound> _benzeneToluene =
    [
        DefaultCompounds.Benzene,
        DefaultCompounds.Toluene
    ];

    [Fact]
    public void BubbleAndDewPressure_BenzeneTolueneEquimolarAt368K_AreConsistent()
    {
        // Arrange: Equimolar Benzene (50%) + Toluene (50%) at 95 °C (368.15 K)
        double[] z = [0.5, 0.5];
        double temperatureKelvin = 368.15;

        // Act
        double pBubble = _thermo.CalculateBubblePointPressureKpa(_benzeneToluene, z, temperatureKelvin);
        double pDew = _thermo.CalculateDewPointPressureKpa(_benzeneToluene, z, temperatureKelvin);

        // Assert: For a zeotropic mixture, P_bubble > P_dew
        Assert.True(pBubble > pDew);
        Assert.InRange(pBubble, 105.0, 115.0);
        Assert.InRange(pDew, 85.0, 95.0);

        // Inverse temperature calculations must recover 368.15 K
        double tBubbleRecovered = _thermo.CalculateBubblePointTemperatureKelvin(_benzeneToluene, z, pBubble);
        double tDewRecovered = _thermo.CalculateDewPointTemperatureKelvin(_benzeneToluene, z, pDew);

        Assert.Equal(temperatureKelvin, tBubbleRecovered, precision: 5);
        Assert.Equal(temperatureKelvin, tDewRecovered, precision: 5);
    }

    [Fact]
    public void IsothermalFlash_InTwoPhaseRegion_ConvergesAndConservesComponentMass()
    {
        // Arrange: Equimolar Benzene + Toluene at T = 368.15 K (95 °C) and P = 101.325 kPa (1 atm)
        double[] z = [0.5, 0.5];
        double temperatureKelvin = 368.15;
        double pressureKpa = 101.325;

        // Act
        FlashResult result = _thermo.CalculateIsothermalFlash(_benzeneToluene, z, temperatureKelvin, pressureKpa);

        // Assert
        Assert.Equal(PhaseState.VaporLiquidEquilibrium, result.Phase);
        Assert.InRange(result.VaporFractionBeta, 0.40, 0.60);
        Assert.True(result.Iterations is > 0 and <= 15);

        // Benzene (more volatile) must be enriched in vapor (y_1 > z_1 > x_1)
        Assert.True(result.VaporCompositionY[0] > z[0]);
        Assert.True(result.LiquidCompositionX[0] < z[0]);

        // Component material balance: (1 - β)*x_i + β*y_i == z_i
        for (int i = 0; i < z.Length; i++)
        {
            double reconstructedZi =
                (1.0 - result.VaporFractionBeta) * result.LiquidCompositionX[i] +
                result.VaporFractionBeta * result.VaporCompositionY[i];

            Assert.Equal(z[i], reconstructedZi, precision: 9);
        }
    }

    [Fact]
    public void IsothermalFlash_AboveBubblePressure_ReturnsSubcooledLiquid()
    {
        // Arrange: High pressure (200 kPa at 368.15 K) keeps mixture 100% liquid
        double[] z = [0.5, 0.5];
        FlashResult result = _thermo.CalculateIsothermalFlash(_benzeneToluene, z, 368.15, 200.0);

        Assert.Equal(PhaseState.SubcooledLiquid, result.Phase);
        Assert.Equal(0.0, result.VaporFractionBeta, precision: 10);
        Assert.Equal(z[0], result.LiquidCompositionX[0], precision: 10);
    }

    [Fact]
    public void IsothermalFlash_BelowDewPressure_ReturnsSuperheatedVapor()
    {
        // Arrange: Low pressure (50 kPa at 368.15 K) vaporizes 100% of the mixture
        double[] z = [0.5, 0.5];
        FlashResult result = _thermo.CalculateIsothermalFlash(_benzeneToluene, z, 368.15, 50.0);

        Assert.Equal(PhaseState.SuperheatedVapor, result.Phase);
        Assert.Equal(1.0, result.VaporFractionBeta, precision: 10);
        Assert.Equal(z[0], result.VaporCompositionY[0], precision: 10);
    }

    [Fact]
    public void IsothermalFlash_TernaryMixture_ConservesMassAndSatisfiesEquilibrium()
    {
        // Arrange: n-Hexane (30%), Benzene (40%), Toluene (30%) at 360 K and 101.325 kPa
        IReadOnlyList<ChemicalCompound> ternary =
        [
            DefaultCompounds.NHexane,
            DefaultCompounds.Benzene,
            DefaultCompounds.Toluene
        ];
        double[] z = [0.30, 0.40, 0.30];

        // Act
        FlashResult result = _thermo.CalculateIsothermalFlash(ternary, z, 360.0, 101.325);

        // Assert
        Assert.Equal(PhaseState.VaporLiquidEquilibrium, result.Phase);
        Assert.Equal(1.0, result.LiquidCompositionX.Sum(), precision: 9);
        Assert.Equal(1.0, result.VaporCompositionY.Sum(), precision: 9);

        for (int i = 0; i < ternary.Count; i++)
        {
            // Equilibrium relation: y_i == K_i * x_i
            Assert.Equal(result.KValues[i] * result.LiquidCompositionX[i], result.VaporCompositionY[i], precision: 8);
        }
    }
}
