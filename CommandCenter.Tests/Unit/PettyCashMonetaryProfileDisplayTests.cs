using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T6-U7: AmountUsd keeps its legacy name but is presented as the profile primary
// currency. Single mode must not leak the old USD/Bs.S pair into petty-cash summaries.
public class PettyCashMonetaryProfileDisplayTests
{
    [Fact]
    public void SingleProfile_PettyCashAmountUsd_IsPresentedAsPrimaryCurrency()
    {
        var previous = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "EUR",
                PrimaryCurrencySymbol = "€",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            });

            var row = new PettyCashExpenseRowViewModel(new PettyCashExpenseClientDto
            {
                AmountBsS = 100m,
                AmountUsd = 100m,
                Currency = PettyCashCurrencyClient.Usd
            });

            Assert.StartsWith("€", row.AmountUsdDisplay, System.StringComparison.Ordinal);
            Assert.StartsWith("€", row.AmountBsSDisplay, System.StringComparison.Ordinal);
            Assert.Equal("EUR", PettyCashLabels.Currency(PettyCashCurrencyClient.Usd));

            var wait = new AuthorizationWaitViewModel(
                Mock.Of<IAuthorizationHubService>(),
                new PettyCashExpenseAuthorizationRequestContext
                {
                    SessionId = 1,
                    Currency = PettyCashCurrencyClient.Usd,
                    AmountBsS = 100m,
                    AmountUsd = 100m,
                    Category = PettyCashCategoryClient.Varios
                },
                _ => Task.CompletedTask);

            Assert.Contains("€", wait.PettyCashAmountDisplay, System.StringComparison.Ordinal);
            Assert.DoesNotContain("Bs.S", wait.PettyCashAmountDisplay, System.StringComparison.Ordinal);
            Assert.DoesNotContain("$", wait.PettyCashAmountDisplay, System.StringComparison.Ordinal);
        }
        finally
        {
            CurrencyDisplay.Current = previous;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }
}
