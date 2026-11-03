using ChemFlow.Domain.Data;
using ChemFlow.Domain.Entities;
using ChemFlow.Domain.ValueObjects;

namespace ChemFlow.Tests;

public class CompoundTests
{
    [Fact]
    public void Water_At100Celsius_HasSaturationPressureOf1Atm()
    {
        // Arrange: 100 °C = 373.15 K
        ChemicalCompound water = DefaultCompounds.Water;
        double temperatureKelvin = 373.15;

        // Act
        double saturationPressureKpa = water.GetSaturationPressureKpa(temperatureKelvin);

        // Assert: 1 atm = 101.325 kPa (within 0.15 kPa tolerance)
        Assert.InRange(saturationPressureKpa, 101.15, 101.50);
    }

    [Theory]
    [InlineData("H2O", 373.15)]      // Water: 100.0 °C = 373.15 K
    [InlineData("C2H5OH", 351.44)]   // Ethanol: 78.29 °C = 351.44 K
    [InlineData("C6H6", 353.24)]     // Benzene: 80.09 °C = 353.24 K
    [InlineData("C7H8", 383.78)]     // Toluene: 110.63 °C = 383.78 K
    [InlineData("n-C6H14", 341.88)]  // n-Hexane: 68.73 °C = 341.88 K
    [InlineData("n-C7H16", 371.58)]  // n-Heptane: 98.43 °C = 371.58 K
    public void GetNormalBoilingPointKelvin_MatchesLiteratureValues(string compoundId, double expectedTbKelvin)
    {
        // Arrange
        ChemicalCompound compound = DefaultCompounds.All.Single(c => c.Id == compoundId);

        // Act
        double actualTbKelvin = compound.GetNormalBoilingPointKelvin();

        // Assert: within 0.25 K of literature normal boiling point
        Assert.InRange(actualTbKelvin, expectedTbKelvin - 0.25, expectedTbKelvin + 0.25);
    }

    [Fact]
    public void Enthalpy_AtReferenceTemperature_LiquidIsZeroAndVaporEqualsHeatOfVaporization()
    {
        // Arrange
        ChemicalCompound benzene = DefaultCompounds.Benzene;
        double tRef = ChemicalCompound.ReferenceTemperatureKelvin;

        // Act
        double liquidEnthalpy = benzene.CalculateLiquidEnthalpyJPerMol(tRef);
        double vaporEnthalpy = benzene.CalculateVaporEnthalpyJPerMol(tRef);

        // Assert
        Assert.Equal(0.0, liquidEnthalpy, precision: 6);
        Assert.Equal(benzene.HeatOfVaporizationJPerMol, vaporEnthalpy, precision: 6);
    }

    [Fact]
    public void AntoineCoefficients_WithNonPositiveB_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new AntoineCoefficients(16.0, -100.0, -40.0));
    }
}
