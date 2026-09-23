using System;
using System.Windows;
using System.Windows.Threading;

namespace Desktop.Client.Views;

public partial class SuccessDialogWindow : Window
{
    private readonly DispatcherTimer _timer;

    public bool SecondaryActionClicked { get; private set; }

    public SuccessDialogWindow(string message, string? secondaryActionLabel = null)
    {
        InitializeComponent();
        DataContext = new
        {
            Message = message,
            SecondaryActionLabel = secondaryActionLabel ?? string.Empty,
            HasSecondaryAction = !string.IsNullOrWhiteSpace(secondaryActionLabel)
        };

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(7)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        DialogResult = true;
        Close();
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = true;
        Close();
    }

    private void SecondaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        SecondaryActionClicked = true;
        DialogResult = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        base.OnClosed(e);
    }
}
