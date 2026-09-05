using Microsoft.Xaml.Behaviors;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Client.Behaviors;

/// <summary>
/// A WPF Behavior for TextBox that implements a fixed-point currency input logic.
/// Digits are shifted from cents to units (e.g., typing '5', '0', '0' results in '5.00').
/// </summary>
public class NumericCalculatorBehavior : Behavior<TextBox>
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(decimal), typeof(NumericCalculatorBehavior),
            new FrameworkPropertyMetadata(0m, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public decimal Value
    {
        get => (decimal)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NumericCalculatorBehavior behavior)
        {
            behavior.UpdateText();
        }
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewTextInput += OnPreviewTextInput;
        AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
        DataObject.AddPastingHandler(AssociatedObject, OnPasting);
        
        // Initial formatting
        UpdateText();
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.PreviewTextInput -= OnPreviewTextInput;
            AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
            DataObject.RemovePastingHandler(AssociatedObject, OnPasting);
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Only allow digits
        if (e.Text.All(char.IsDigit))
        {
            // Convert current decimal to cents (long) to perform shift
            // Using Math.Round to avoid precision issues before truncation
            long currentCents = (long)Math.Round(Value * 100, MidpointRounding.AwayFromZero);
            
            if (int.TryParse(e.Text, out int digit))
            {
                // Shift left: (1.23 * 10) + 0.04 -> 12.34
                // value = (123 * 10 + 4) / 100 = 12.34
                Value = (currentCents * 10 + digit) / 100m;
            }
        }
        
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            // Shift right: 12.34 -> 1.23
            // value = (1234 / 10) / 100 = 1.23
            long currentCents = (long)Math.Round(Value * 100, MidpointRounding.AwayFromZero);
            Value = (currentCents / 10) / 100m;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            Value = 0m;
            e.Handled = true;
        }
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            string text = (string)e.DataObject.GetData(DataFormats.Text);
            Value = ParseAmount(text);
        }
        e.CancelCommand();
        e.Handled = true;
    }

    public static decimal ParseAmount(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return 0m;
        string str = rawText.Trim();
        bool isNegative = str.StartsWith('-');
        if (isNegative) str = str.Substring(1).Trim();

        if (str.StartsWith("Bs.S", StringComparison.OrdinalIgnoreCase)) str = str.Substring(4).Trim();
        else if (str.StartsWith("Bs", StringComparison.OrdinalIgnoreCase)) str = str.Substring(2).Trim();
        else if (str.StartsWith("$")) str = str.Substring(1).Trim();

        if (string.IsNullOrWhiteSpace(str)) return 0m;

        if (str.Contains(",,") || str.Contains("..") || str.Contains(".,") || str.Contains(",."))
            return 0m;

        if (str.Any(c => !char.IsDigit(c) && c != '.' && c != ','))
            return 0m;

        int dotCount = str.Count(c => c == '.');
        int commaCount = str.Count(c => c == ',');
        int totalSeps = dotCount + commaCount;

        if (totalSeps == 0)
        {
            if (decimal.TryParse(str, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var whole))
                return isNegative ? -whole : whole;
            return 0m;
        }

        if (totalSeps == 1)
        {
            var normalized = str.Replace(',', '.');
            if (decimal.TryParse(normalized, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var singleVal))
                return isNegative ? -singleVal : singleVal;
            return 0m;
        }

        int lastDot = str.LastIndexOf('.');
        int lastComma = str.LastIndexOf(',');
        int lastSepIndex = Math.Max(lastDot, lastComma);

        string intPart = str.Substring(0, lastSepIndex).Replace(".", "").Replace(",", "");
        string decPart = str.Substring(lastSepIndex + 1);

        string combined = $"{intPart}.{decPart}";
        if (decimal.TryParse(combined, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var multiVal))
            return isNegative ? -multiVal : multiVal;

        return 0m;
    }

    private void UpdateText()
    {
        if (AssociatedObject != null)
        {
            AssociatedObject.Text = Value.ToString("N2");
            AssociatedObject.CaretIndex = AssociatedObject.Text.Length;
        }
    }
}
