using System;
using System.Threading;
using System.Windows.Controls;
using Desktop.Client.Helpers;
using Xunit;

namespace CommandCenter.Tests;

public class AtmBehaviorTests
{
    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw exception;
        }
    }

    [Fact]
    public void ProcessDigit_WithExistingValueAndSelection_ResetsToCentsNotAppended()
    {
        RunOnSta(() =>
        {
            var tb = new TextBox();
            AtmBehavior.SetMode(tb, AtmMode.Currency);
            AtmBehavior.SetValue(tb, 1.56m);
            AtmBehavior.FormatAndDisplay(tb, 1.56m);

            Assert.Equal("1.56", tb.Text);

            // User focuses or selects all text (e.g. "1.56")
            tb.SelectAll();
            Assert.True(tb.SelectionLength > 0);

            // User types "1"
            AtmBehavior.ProcessDigit(tb, 1);

            // The value must be 0.01, NOT 15.61
            Assert.Equal(0.01m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.01", tb.Text);
        });
    }

    [Fact]
    public void ProcessDigit_SubsequentDigits_ShiftCorrectly()
    {
        RunOnSta(() =>
        {
            var tb = new TextBox();
            AtmBehavior.SetMode(tb, AtmMode.Currency);
            AtmBehavior.SetValue(tb, 1.56m);
            AtmBehavior.FormatAndDisplay(tb, 1.56m);

            // Initial edit with selection -> types 1 -> becomes 0.01
            tb.SelectAll();
            AtmBehavior.ProcessDigit(tb, 1);
            Assert.Equal(0.01m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.01", tb.Text);

            // Types 5 (no selection) -> becomes 0.15
            AtmBehavior.ProcessDigit(tb, 5);
            Assert.Equal(0.15m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.15", tb.Text);

            // Types 6 (no selection) -> becomes 1.56
            AtmBehavior.ProcessDigit(tb, 6);
            Assert.Equal(1.56m, AtmBehavior.GetValue(tb));
            Assert.Equal("1.56", tb.Text);
        });
    }

    [Fact]
    public void ProcessDigit_WithIsFirstEditAfterFocus_ResetsToCents()
    {
        RunOnSta(() =>
        {
            var tb = new TextBox();
            AtmBehavior.SetMode(tb, AtmMode.Currency);
            AtmBehavior.SetValue(tb, 1.56m);
            AtmBehavior.FormatAndDisplay(tb, 1.56m);

            // Simulate GotFocus setting IsFirstEditAfterFocus
            AtmBehavior.SetIsFirstEditAfterFocus(tb, true);

            // User types 1
            AtmBehavior.ProcessDigit(tb, 1);

            Assert.Equal(0.01m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.01", tb.Text);
            Assert.False(AtmBehavior.GetIsFirstEditAfterFocus(tb));
        });
    }

    [Fact]
    public void ProcessBackspace_WithoutSelection_ShiftsDown()
    {
        RunOnSta(() =>
        {
            var tb = new TextBox();
            AtmBehavior.SetMode(tb, AtmMode.Currency);
            AtmBehavior.SetValue(tb, 1.56m);
            AtmBehavior.FormatAndDisplay(tb, 1.56m);

            AtmBehavior.ProcessBackspace(tb);
            Assert.Equal(0.15m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.15", tb.Text);

            AtmBehavior.ProcessBackspace(tb);
            Assert.Equal(0.01m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.01", tb.Text);

            AtmBehavior.ProcessBackspace(tb);
            Assert.Equal(0.00m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.00", tb.Text);
        });
    }

    [Fact]
    public void QuantityMode_FormatsAndEditsCorrectly()
    {
        RunOnSta(() =>
        {
            var tb = new TextBox();
            AtmBehavior.SetMode(tb, AtmMode.Quantity);
            AtmBehavior.SetValue(tb, 6.00m);
            AtmBehavior.FormatAndDisplay(tb, 6.00m);

            Assert.Equal("6.00", tb.Text);

            tb.SelectAll();
            AtmBehavior.ProcessDigit(tb, 1);

            Assert.Equal(0.01m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.01", tb.Text);
        });
    }

    [Fact]
    public void PercentageMode_FormatsAndEditsCorrectly()
    {
        RunOnSta(() =>
        {
            var tb = new TextBox();
            AtmBehavior.SetMode(tb, AtmMode.Percentage);
            AtmBehavior.SetValue(tb, 30.00m);
            AtmBehavior.FormatAndDisplay(tb, 30.00m);

            Assert.Equal("30.00%", tb.Text);

            tb.SelectAll();
            AtmBehavior.ProcessDigit(tb, 1);

            Assert.Equal(0.01m, AtmBehavior.GetValue(tb));
            Assert.Equal("0.01%", tb.Text);
        });
    }
}
