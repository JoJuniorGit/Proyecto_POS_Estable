using System;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Desktop.Client.Converters;
using Desktop.Client.Services;
using MonetaryMode = global::Core.DTOs.MonetaryMode;
using MonetaryProfileDto = global::Core.DTOs.MonetaryProfileDto;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T6-U19: the real WPF binding pipeline must refresh bindings hosted in
/// TextBlock.Inlines (Run elements), not only visual-tree children. This test exercises the
/// private refresh walk directly so it does not create a process-wide Application (which would
/// change the behavior of later WPF dialog tests).
/// </summary>
public class CurrencyBindingRefresherHostTests
{
    [Fact]
    public void ProfileChange_RefreshesRunInlineBindings()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
                {
                    Mode = MonetaryMode.Dual,
                    PrimaryCurrencyCode = "USD",
                    PrimaryCurrencySymbol = "$",
                    SecondaryCurrencyCode = "VES",
                    SecondaryCurrencySymbol = "Bs.S"
                });

                var viewModel = new AmountViewModel { Amount = 12.5m };
                var run = new Run { DataContext = viewModel };
                run.SetBinding(Run.TextProperty, new Binding(nameof(AmountViewModel.Amount))
                {
                    Converter = new PrimaryAmountConverter()
                });

                var textBlock = new TextBlock { DataContext = viewModel };
                textBlock.Inlines.Add(run);
                run.GetBindingExpression(Run.TextProperty)!.UpdateTarget();

                var before = run.Text;
                Assert.False(string.IsNullOrWhiteSpace(before));

                CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
                {
                    Mode = MonetaryMode.Dual,
                    PrimaryCurrencyCode = "EUR",
                    PrimaryCurrencySymbol = "EUR",
                    SecondaryCurrencyCode = "VES",
                    SecondaryCurrencySymbol = "Bs.S"
                });

                var refreshTree = typeof(CurrencyBindingRefresher).GetMethod(
                    "RefreshTree",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(refreshTree);
                refreshTree!.Invoke(null, new object[] { textBlock });

                var after = run.Text;
                Assert.NotEqual(before, after);
                Assert.Contains("EUR", after, StringComparison.Ordinal);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                CurrencyDisplay.SetMonetaryProfile(null);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private sealed class AmountViewModel
    {
        public decimal Amount { get; set; }
    }
}
