using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.Validation;

namespace Desktop.Client.Behaviors;

/// <summary>
/// Strict, reusable quantity editor for partial pickups. It owns text sanitation and
/// normalization; the ViewModel owns business validation (pending maximum and confirmation).
/// </summary>
public static class QuantityInputBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(QuantityInputBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty AllowDecimalsProperty =
        DependencyProperty.RegisterAttached(
            "AllowDecimals",
            typeof(bool),
            typeof(QuantityInputBehavior),
            new PropertyMetadata(true));

    public static readonly DependencyProperty MaxDecimalPlacesProperty =
        DependencyProperty.RegisterAttached(
            "MaxDecimalPlaces",
            typeof(int),
            typeof(QuantityInputBehavior),
            new PropertyMetadata(QuantityInputParser.DefaultMaxDecimalPlaces));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetAllowDecimals(DependencyObject element) =>
        (bool)element.GetValue(AllowDecimalsProperty);

    public static void SetAllowDecimals(DependencyObject element, bool value) =>
        element.SetValue(AllowDecimalsProperty, value);

    public static int GetMaxDecimalPlaces(DependencyObject element) =>
        (int)element.GetValue(MaxDecimalPlacesProperty);

    public static void SetMaxDecimalPlaces(DependencyObject element, int value) =>
        element.SetValue(MaxDecimalPlacesProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox) return;

        if ((bool)e.NewValue)
        {
            textBox.PreviewTextInput -= OnPreviewTextInput;
            textBox.PreviewKeyDown -= OnPreviewKeyDown;
            textBox.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            textBox.GotKeyboardFocus -= OnGotKeyboardFocus;
            textBox.LostKeyboardFocus -= OnLostKeyboardFocus;
            DataObject.RemovePastingHandler(textBox, OnPasting);

            textBox.PreviewTextInput += OnPreviewTextInput;
            textBox.PreviewKeyDown += OnPreviewKeyDown;
            textBox.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            textBox.GotKeyboardFocus += OnGotKeyboardFocus;
            textBox.LostKeyboardFocus += OnLostKeyboardFocus;
            DataObject.AddPastingHandler(textBox, OnPasting);
        }
        else
        {
            textBox.PreviewTextInput -= OnPreviewTextInput;
            textBox.PreviewKeyDown -= OnPreviewKeyDown;
            textBox.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            textBox.GotKeyboardFocus -= OnGotKeyboardFocus;
            textBox.LostKeyboardFocus -= OnLostKeyboardFocus;
            DataObject.RemovePastingHandler(textBox, OnPasting);
        }
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.IsKeyboardFocusWithin)
        {
            return;
        }

        // Focus first and handle the click so the mouse does not clear SelectAll from GotKeyboardFocus.
        textBox.Focus();
        e.Handled = true;
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        var allowDecimals = GetAllowDecimals(textBox);
        var maxDecimalPlaces = GetMaxDecimalPlaces(textBox);
        var inserted = e.Text;

        if (string.IsNullOrEmpty(inserted)
            || inserted.Any(character => !QuantityInputParser.IsAllowedCharacter(character, allowDecimals)))
        {
            e.Handled = true;
            return;
        }

        if (inserted.Length == 1
            && (inserted[0] is '.' or ',')
            && allowDecimals
            && (textBox.Text.Length == 0 || textBox.SelectionLength == textBox.Text.Length))
        {
            var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            textBox.Text = "0" + separator;
            textBox.CaretIndex = textBox.Text.Length;
            e.Handled = true;
            return;
        }

        var prospective = QuantityInputParser.BuildText(
            textBox.Text,
            textBox.SelectionStart,
            textBox.SelectionLength,
            inserted);

        if (!QuantityInputParser.IsValidTypingText(prospective, allowDecimals, maxDecimalPlaces))
        {
            e.Handled = true;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            return;
        }

        if (e.Key is Key.Space or Key.OemMinus or Key.Subtract or Key.OemPlus or Key.Add)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        var allowDecimals = GetAllowDecimals(textBox);
        var maxDecimalPlaces = GetMaxDecimalPlaces(textBox);
        var current = textBox.Text;

        if (string.IsNullOrWhiteSpace(current))
        {
            textBox.Text = "0";
            textBox.CaretIndex = textBox.Text.Length;
            return;
        }

        if (!QuantityInputParser.TryParse(current, allowDecimals, maxDecimalPlaces, out var value, out _))
        {
            return;
        }

        var normalized = QuantityInputParser.Format(value);
        if (!string.Equals(current, normalized, StringComparison.Ordinal))
        {
            textBox.Text = normalized;
            textBox.CaretIndex = textBox.Text.Length;
        }
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            e.Handled = true;
            return;
        }

        var pasted = (string)e.DataObject.GetData(DataFormats.Text);
        if (!string.IsNullOrEmpty(pasted) && pasted[0] is '.' or ',')
        {
            pasted = "0" + pasted;
        }

        var allowDecimals = GetAllowDecimals(textBox);
        var maxDecimalPlaces = GetMaxDecimalPlaces(textBox);
        var prospective = QuantityInputParser.BuildText(
            textBox.Text,
            textBox.SelectionStart,
            textBox.SelectionLength,
            pasted);

        if (!QuantityInputParser.IsValidPaste(prospective, allowDecimals, maxDecimalPlaces))
        {
            e.CancelCommand();
            e.Handled = true;
            return;
        }

        var caret = textBox.SelectionStart + pasted.Length;
        textBox.Text = prospective;
        textBox.CaretIndex = Math.Min(caret, textBox.Text.Length);
        e.CancelCommand();
        e.Handled = true;
    }
}
