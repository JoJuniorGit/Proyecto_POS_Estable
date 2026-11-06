using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using MonetaryMode = global::Core.DTOs.MonetaryMode;
using MonetaryProfileDto = global::Core.DTOs.MonetaryProfileDto;
using Desktop.Client.Converters;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

[CollectionDefinition("WpfHost", DisableParallelization = true)]
public sealed class WpfHostCollection
{
}

/// <summary>
/// 8.211-T6-U19: the real WPF host must refresh bindings hosted in TextBlock.Inlines (Run
/// elements), not only visual-tree children. The static XAML audit cannot prove this path.
/// </summary>
[Collection("WpfHost")]
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
                _ = Application.Current ?? new Application();
                CurrencyBindingRefresher.Install();

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

                var textBlock = new TextBlock();
                textBlock.Inlines.Add(run);

                var window = new Window { Content = textBlock };
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

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

                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var after = run.Text;

                window.Close();
                Assert.NotEqual(before, after);
                Assert.Contains("EUR", after, StringComparison.Ordinal);
            }
            catch (Exception exception)
            {
                failure = exception;
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




