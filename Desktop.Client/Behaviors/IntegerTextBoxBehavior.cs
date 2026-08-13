using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Client.Behaviors;

public static class IntegerTextBoxBehavior
{
    public static readonly DependencyProperty IsIntegerOnlyProperty =
        DependencyProperty.RegisterAttached(
            "IsIntegerOnly",
            typeof(bool),
            typeof(IntegerTextBoxBehavior),
            new UIPropertyMetadata(false, OnIsIntegerOnlyChanged));

    public static bool GetIsIntegerOnly(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsIntegerOnlyProperty);
    }

    public static void SetIsIntegerOnly(DependencyObject obj, bool value)
    {
        obj.SetValue(IsIntegerOnlyProperty, value);
    }

    private static void OnIsIntegerOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox textBox)
        {
            if ((bool)e.NewValue)
            {
                textBox.PreviewTextInput += TextBox_PreviewTextInput;
                DataObject.AddPastingHandler(textBox, TextBox_Pasting);
            }
            else
            {
                textBox.PreviewTextInput -= TextBox_PreviewTextInput;
                DataObject.RemovePastingHandler(textBox, TextBox_Pasting);
            }
        }
    }

    private static void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsTextAllowed(e.Text);
    }

    private static void TextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            string text = (string)e.DataObject.GetData(typeof(string));
            if (!IsTextAllowed(text))
            {
                e.CancelCommand();
            }
        }
        else
        {
            e.CancelCommand();
        }
    }

    private static bool IsTextAllowed(string text)
    {
        return Regex.IsMatch(text, "^[0-9]+$");
    }
}
