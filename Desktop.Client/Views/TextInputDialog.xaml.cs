using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Client.Views;

public partial class TextInputDialog : UserControl
{
    public TextInputDialog(string prompt, string hint = "Name")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        MaterialDesignThemes.Wpf.HintAssist.SetHint(InputTextBox, hint);

        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
        };
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            MaterialDesignThemes.Wpf.DialogHost.CloseDialogCommand.Execute(InputTextBox.Text, this);
        }
    }
}
