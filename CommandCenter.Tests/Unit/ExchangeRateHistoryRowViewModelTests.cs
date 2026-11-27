using System;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Core.Helpers;
using Xunit;

namespace CommandCenter.Tests.Unit;

[Collection(CurrencyDisplayStateCollection.Name)]
public sealed class ExchangeRateHistoryRowViewModelTests
{
    [Fact]
    public void RateDisplay_UsesTheConfiguredCurrencyFormat()
    {
        var original = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            var row = CreateRow(924.36m, 923.91m);

            Assert.Equal("924,3600", row.RateDisplay);
            Assert.Equal("+0,4500", row.VariationDisplay);
            Assert.Equal("Vigente", row.StatusLabel);
            Assert.Equal("#DCFCE7", row.StatusBackground);
            Assert.Equal("#15803D", row.StatusForeground);

            CurrencyDisplay.Current = MoneyDisplayFormat.International;
            var internationalRow = CreateRow(924.36m, 923.91m);

            Assert.Equal("924.3600", internationalRow.RateDisplay);
            Assert.Equal("+0.4500", internationalRow.VariationDisplay);
        }
        finally
        {
            CurrencyDisplay.Current = original;
        }
    }

    [Fact]
    public void HistoricalRow_WithoutPreviousRate_ShowsNeutralVariation()
    {
        var row = new ExchangeRateHistoryRowViewModel(CreateItem(924.36m), isCurrent: false, previous: null);

        Assert.Equal("—", row.VariationDisplay);
        Assert.Equal("#94A3B8", row.VariationForeground);
        Assert.Equal("Histórica", row.StatusLabel);
        Assert.Equal("#F1F5F9", row.StatusBackground);
        Assert.Equal("#64748B", row.StatusForeground);
    }

    private static ExchangeRateHistoryRowViewModel CreateRow(decimal rate, decimal previousRate) =>
        new(CreateItem(rate), isCurrent: true, previous: CreateItem(previousRate));

    private static ExchangeRateHistoryDto CreateItem(decimal rate) => new()
    {
        Date = new DateOnly(2026, 11, 25),
        Rate = rate,
        UpdatedAt = new DateTime(2026, 11, 25, 22, 22, 0, DateTimeKind.Utc)
    };
}
