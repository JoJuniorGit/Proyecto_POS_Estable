using System;
using Xunit;
using Desktop.Client.Helpers;

namespace CommandCenter.Tests.Helpers
{
    /// <summary>
    /// Validates every possible rounding and currency conversion scenario for PricingHelper,
    /// ensuring compliance with the "Golden Rule" of financial accuracy.
    /// </summary>
    public class PricingHelperTests
    {
        // 1. Standard Rounding USD (RoundToDigital)
        // Importance: Validates that standard calculations (like card payments or bank transfers)
        // are accurately represented with 2 decimals, applying the AwayFromZero rule (commercial rounding).
        [Theory]
        [InlineData(10.455, 10.46)]
        [InlineData(10.454, 10.45)]
        [InlineData(10.445, 10.45)] // Far from zero rounding
        [InlineData(10.000, 10.00)]
        public void RoundToDigital_ShouldFollowAwayFromZeroRule(decimal input, decimal expected)
        {
            // Act
            decimal result = PricingHelper.RoundToDigital(input);

            // Assert
            Assert.Equal(expected, result);
        }

        // 2. Conversion to Bs.S Digital (2 decimal places)
        // Importance: Ensures that when converting USD to local currency, the resulting 
        // value retains proper precision for exact bank/digital transactions.
        [Theory]
        [InlineData(1.00, 36.50, 36.50)]
        [InlineData(1.25, 36.42, 45.53)] // 1.25 * 36.42 = 45.525 -> 45.53
        public void ToBsS_ShouldConvertAndRoundToDigitalCorrectly(decimal amountUsd, decimal rate, decimal expected)
        {
            // Act
            decimal result = PricingHelper.ToBsS(amountUsd, rate);

            // Assert
            Assert.Equal(expected, result);
        }

        // 3. Rounding to Bs.S Cash (RoundToCash - Whole Numbers)
        // Importance: Physical cash requires whole numbers. We must ensure that any
        // digital decimal value is correctly rounded to its nearest whole currency note/coin equivalent.
        [Theory]
        [InlineData(100.50, 101)]
        [InlineData(100.49, 100)]
        [InlineData(100.51, 101)]
        [InlineData(0.50, 1)] // Minimum cash amount
        [InlineData(0.49, 0)]
        public void RoundToCash_ShouldReturnInteger_WhenGivenDecimal(decimal input, decimal expected)
        {
            // Act
            decimal result = PricingHelper.RoundToCash(input);

            // Assert
            Assert.Equal(expected, result);
        }

        // 4. Complete Conversion Chain (USD -> Digital -> Cash)
        // Importance: Validates the end-to-end workflow of a transaction. An amount originates in USD,
        // gets converted to a digital Bs.S amount, and is then finally rounded for cash payment
        // without consistency loss.
        [Theory]
        [InlineData(1.25, 36.42, 46)] // USD 1.25 @ 36.42 = 45.525 -> Digital: 45.53 -> Cash: 46
        [InlineData(2.15, 36.50, 78)] // USD 2.15 @ 36.50 = 78.475 -> Digital: 78.48 -> Cash: 78
        public void CompleteConversionChain_FromUsdToCash_ShouldMaintainConsistency(decimal amountUsd, decimal rate, decimal expectedCash)
        {
            // Arrange
            decimal digitalAmount = PricingHelper.ToBsS(amountUsd, rate);

            // Act
            decimal cashResult = PricingHelper.RoundToCash(digitalAmount);

            // Assert
            Assert.Equal(expectedCash, cashResult);
        }

        // 5. Edge Cases
        // Importance: Confirms the engine's stability when handling 0, negative values (e.g. credit notes),
        // or very large numbers (preventing overflow issues while retaining precision).
        [Theory]
        [InlineData(0.00, 0.00)]
        [InlineData(-10.455, -10.46)]
        [InlineData(99999999.455, 99999999.46)] // Large values validation
        public void RoundToDigital_ShouldHandleEdgeCasesCorrectly(decimal input, decimal expected)
        {
            // Act
            decimal result = PricingHelper.RoundToDigital(input);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(0.00, 0)]
        [InlineData(-100.50, -101)]
        [InlineData(99999999.50, 100000000)] // Large values validation
        public void RoundToCash_ShouldHandleEdgeCasesCorrectly(decimal input, decimal expected)
        {
            // Act
            decimal result = PricingHelper.RoundToCash(input);

            // Assert
            Assert.Equal(expected, result);
        }
    }
}
