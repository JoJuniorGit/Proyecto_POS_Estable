using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Helpers;

public enum AtmMode
{
    None,
    Currency,
    Percentage,
    Quantity
}

public static class AtmBehavior
{
    private static bool _isInternalUpdate = false;

    #region Mode Attached Property
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode",
            typeof(AtmMode),
            typeof(AtmBehavior),
            new PropertyMetadata(AtmMode.None, OnModeChanged));

    public static AtmMode GetMode(DependencyObject obj) => (AtmMode)obj.GetValue(ModeProperty);
    public static void SetMode(DependencyObject obj, AtmMode value) => obj.SetValue(ModeProperty, value);
    #endregion

    #region Value Attached Property
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.RegisterAttached(
            "Value",
            typeof(decimal),
            typeof(AtmBehavior),
            new FrameworkPropertyMetadata(0m, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static decimal GetValue(DependencyObject obj) => (decimal)obj.GetValue(ValueProperty);
    public static void SetValue(DependencyObject obj, decimal value) => obj.SetValue(ValueProperty, value);
    #endregion

    #region RecalculateTrigger Attached Property
    public static readonly DependencyProperty RecalculateTriggerProperty =
        DependencyProperty.RegisterAttached(
            "RecalculateTrigger",
            typeof(string),
            typeof(AtmBehavior),
            new PropertyMetadata(string.Empty));

    public static string GetRecalculateTrigger(DependencyObject obj) => (string)obj.GetValue(RecalculateTriggerProperty);
    public static void SetRecalculateTrigger(DependencyObject obj, string value) => obj.SetValue(RecalculateTriggerProperty, value);
    #endregion

    #region IsFirstEditAfterFocus Attached Property
    public static readonly DependencyProperty IsFirstEditAfterFocusProperty =
        DependencyProperty.RegisterAttached(
            "IsFirstEditAfterFocus",
            typeof(bool),
            typeof(AtmBehavior),
            new PropertyMetadata(false));

    public static bool GetIsFirstEditAfterFocus(DependencyObject obj) => (bool)obj.GetValue(IsFirstEditAfterFocusProperty);
    public static void SetIsFirstEditAfterFocus(DependencyObject obj, bool value) => obj.SetValue(IsFirstEditAfterFocusProperty, value);
    #endregion

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;

        tb.PreviewTextInput -= TextBox_PreviewTextInput;
        tb.PreviewKeyDown -= TextBox_PreviewKeyDown;
        tb.GotFocus -= TextBox_GotFocus;
        tb.LostFocus -= TextBox_LostFocus;
        tb.PreviewMouseLeftButtonDown -= TextBox_PreviewMouseLeftButtonDown;
        DataObject.RemovePastingHandler(tb, TextBox_Pasting);

        AtmMode newMode = (AtmMode)e.NewValue;
        if (newMode != AtmMode.None)
        {
            tb.PreviewTextInput += TextBox_PreviewTextInput;
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.GotFocus += TextBox_GotFocus;
            tb.LostFocus += TextBox_LostFocus;
            tb.PreviewMouseLeftButtonDown += TextBox_PreviewMouseLeftButtonDown;
            DataObject.AddPastingHandler(tb, TextBox_Pasting);

            // Format initial display
            FormatAndDisplay(tb, GetValue(tb));
        }
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        if (_isInternalUpdate) return;

        decimal newVal = (decimal)e.NewValue;
        FormatAndDisplay(tb, newVal);
    }

    private static void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox tb) return;

        e.Handled = true;

        if (string.IsNullOrEmpty(e.Text)) return;

        foreach (char c in e.Text)
        {
            if (c >= '0' && c <= '9')
            {
                int digit = c - '0';
                ProcessDigit(tb, digit);
            }
        }
    }

    private static void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            e.Handled = true;
            if (tb.SelectionLength > 0 || GetIsFirstEditAfterFocus(tb))
            {
                SetIsFirstEditAfterFocus(tb, false);
                UpdateValue(tb, 0m);
                tb.Select(tb.Text.Length, 0);
            }
            else
            {
                ProcessBackspace(tb);
            }
        }
        else if (e.Key == Key.Space)
        {
            e.Handled = true;
        }
    }

    private static void TextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            tb.Focus();
        }
    }

    private static void TextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            SetIsFirstEditAfterFocus(tb, true);
            tb.SelectAll();
        }
    }

    private static void TextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        SetIsFirstEditAfterFocus(tb, false);

        // Ensure display format is clean at rest
        decimal currentVal = GetValue(tb);
        FormatAndDisplay(tb, currentVal);

        // Execute LostFocus recalculation on ViewModel if trigger is specified
        string trigger = GetRecalculateTrigger(tb);
        if (!string.IsNullOrEmpty(trigger) && tb.DataContext is ProductDialogViewModel vm)
        {
            if (vm.RecalculatePricingCommand.CanExecute(trigger))
            {
                vm.RecalculatePricingCommand.Execute(trigger);
            }
        }
    }

    private static void TextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox tb) return;
        e.CancelCommand();
        e.Handled = true;

        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            string text = e.DataObject.GetData(typeof(string)) as string ?? string.Empty;
            foreach (char c in text)
            {
                if (c >= '0' && c <= '9')
                {
                    ProcessDigit(tb, c - '0');
                }
            }
        }
    }

    public static void ProcessDigit(TextBox tb, int digit)
    {
        bool isReset = tb.SelectionLength > 0 || GetIsFirstEditAfterFocus(tb);
        SetIsFirstEditAfterFocus(tb, false);

        long cents;
        if (isReset)
        {
            cents = 0;
        }
        else
        {
            decimal currentVal = GetValue(tb);
            cents = (long)Math.Round(currentVal * 100m, MidpointRounding.AwayFromZero);
        }

        if (cents < 999999999L)
        {
            cents = (cents * 10) + digit;
        }

        decimal newDecimal = cents / 100m;
        UpdateValue(tb, newDecimal);
        tb.Select(tb.Text.Length, 0);
    }

    public static void ProcessBackspace(TextBox tb)
    {
        SetIsFirstEditAfterFocus(tb, false);
        decimal currentVal = GetValue(tb);
        long cents = (long)Math.Round(currentVal * 100m, MidpointRounding.AwayFromZero);

        cents /= 10;

        decimal newDecimal = cents / 100m;
        UpdateValue(tb, newDecimal);
        tb.Select(tb.Text.Length, 0);
    }

    public static void UpdateValue(TextBox tb, decimal newDecimal)
    {
        _isInternalUpdate = true;
        try
        {
            SetValue(tb, newDecimal);
            FormatAndDisplay(tb, newDecimal);
        }
        finally
        {
            _isInternalUpdate = false;
        }
    }

    public static void FormatAndDisplay(TextBox tb, decimal val)
    {
        AtmMode mode = GetMode(tb);
        if (mode == AtmMode.Currency || mode == AtmMode.Quantity)
        {
            tb.Text = val.ToString("N2", CultureInfo.InvariantCulture);
        }
        else if (mode == AtmMode.Percentage)
        {
            tb.Text = $"{val.ToString("0.00", CultureInfo.InvariantCulture)}%";
        }
        tb.CaretIndex = tb.Text.Length;
    }
}
