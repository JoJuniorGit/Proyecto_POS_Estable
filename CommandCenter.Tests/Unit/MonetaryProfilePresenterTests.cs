using System;
using System.ComponentModel;
using Core.DTOs;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T6-U12: WPF binds to an observable presenter so profile refreshes notify open views.
public class MonetaryProfilePresenterTests
{
    [Fact]
    public void ProfileChange_RefreshesPresenterAndRaisesPropertyChanged()
    {
        var presenter = MonetaryProfilePresenter.Instance;
        var changes = 0;
        PropertyChangedEventHandler handler = (_, _) => changes++;
        presenter.PropertyChanged += handler;
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

            Assert.True(presenter.IsSingle);
            Assert.Equal("EUR", presenter.PrimaryCurrencyCode);
            Assert.Equal("€", presenter.LocalCurrencySymbol);
            Assert.Null(presenter.SecondaryCurrencyCode);
            Assert.True(changes > 0);

            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "COP",
                SecondaryCurrencySymbol = "COL$",
                IsConfigured = true
            });

            Assert.False(presenter.IsSingle);
            Assert.Equal("COP", presenter.SecondaryCurrencyCode);
            Assert.Equal("COL$", presenter.LocalCurrencySymbol);
        }
        finally
        {
            presenter.PropertyChanged -= handler;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }
}

