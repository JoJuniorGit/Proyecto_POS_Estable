using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Client.Behaviors;

/// <summary>
/// Attached behavior that implements "decimal-cents" ATM-style input.
/// Digits shift right-to-left: typing "1" → 0.01, "12" → 0.12, "123" → 1.23.
/// Includes fresh-focus ready-to-overwrite, paste handling, and strict key blocking.
/// </summary>
public static class DecimalCentsBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(DecimalCentsBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty RawDigitsProperty =
        DependencyProperty.RegisterAttached(
            "RawDigits",
            typeof(string),
            typeof(DecimalCentsBehavior),
            new PropertyMetadata(""));

    private static readonly DependencyProperty IsFreshFocusProperty =
        DependencyProperty.RegisterAttached(
            "IsFreshFocus",
            typeof(bool),
            typeof(DecimalCentsBehavior),
            new PropertyMetadata(true));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;

        if ((bool)e.NewValue)
        {
            tb.PreviewTextInput += TextBox_PreviewTextInput;
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.GotFocus += TextBox_GotFocus;
            DataObject.AddPastingHandler(tb, TextBox_OnPasting);
        }
        else
        {
            tb.PreviewTextInput -= TextBox_PreviewTextInput;
            tb.PreviewKeyDown -= TextBox_PreviewKeyDown;
            tb.GotFocus -= TextBox_GotFocus;
            DataObject.RemovePastingHandler(tb, TextBox_OnPasting);
        }
    }

    private static void TextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            var current = tb.Text.Replace(".", "").Replace(",", "").TrimStart('0');
            tb.SetValue(RawDigitsProperty, current);
            tb.SetValue(IsFreshFocusProperty, true);
            tb.SelectAll();
        }
    }

    private static void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox tb) return;

        if (e.Text.Length == 0 || !char.IsDigit(e.Text[0]))
        {
            e.Handled = true;
            return;
        }

        bool isFresh = (bool)tb.GetValue(IsFreshFocusProperty);
        string raw;

        if (isFresh)
        {
            raw = e.Text;
            tb.SetValue(IsFreshFocusProperty, false);
        }
        else
        {
            raw = (string)tb.GetValue(RawDigitsProperty) + e.Text;
        }

        if (raw.Length > 9)
            raw = raw[..9];

        tb.SetValue(RawDigitsProperty, raw);
        UpdateDisplay(tb, raw);
        e.Handled = true;
    }

    private static void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        bool isFresh = (bool)tb.GetValue(IsFreshFocusProperty);

        if (e.Key == Key.Back)
        {
            if (isFresh)
            {
                tb.SetValue(RawDigitsProperty, "");
                tb.SetValue(IsFreshFocusProperty, false);
            }
            else
            {
                var raw = (string)tb.GetValue(RawDigitsProperty);
                if (raw.Length > 0)
                    raw = raw[..^1];
                tb.SetValue(RawDigitsProperty, raw);
            }
            UpdateDisplay(tb, (string)tb.GetValue(RawDigitsProperty));
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            tb.SetValue(RawDigitsProperty, "");
            tb.SetValue(IsFreshFocusProperty, false);
            UpdateDisplay(tb, "");
            e.Handled = true;
        }
    }

    private static void TextBox_OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox tb) return;

        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            var pastedText = (string)e.DataObject.GetData(DataFormats.Text);
            var rawDigits = Regex.Replace(pastedText, @"\D", "");
            if (!string.IsNullOrEmpty(rawDigits))
            {
                if (rawDigits.Length > 9)
                    rawDigits = rawDigits[..9];

                tb.SetValue(RawDigitsProperty, rawDigits);
                tb.SetValue(IsFreshFocusProperty, false);
                UpdateDisplay(tb, rawDigits);
            }
        }
        e.CancelCommand();
        e.Handled = true;
    }

    private static void UpdateDisplay(TextBox tb, string rawDigits)
    {
        if (string.IsNullOrEmpty(rawDigits))
        {
            tb.Text = "0.00";
            tb.CaretIndex = tb.Text.Length;
            return;
        }

        var padded = rawDigits.PadLeft(3, '0');
        var wholePart = padded[..^2];
        var decimalPart = padded[^2..];

        wholePart = wholePart.TrimStart('0');
        if (string.IsNullOrEmpty(wholePart)) wholePart = "0";

        if (decimal.TryParse($"{wholePart}.{decimalPart}", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal val))
        {
            tb.Text = val.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            tb.Text = $"{wholePart}.{decimalPart}";
        }
        tb.CaretIndex = tb.Text.Length;
    }
}
