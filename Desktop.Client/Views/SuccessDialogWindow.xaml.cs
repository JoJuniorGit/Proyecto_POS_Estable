using System;
using System.Windows;
using System.Windows.Threading;

namespace Desktop.Client.Views
{
    public partial class SuccessDialogWindow : Window
    {
        private DispatcherTimer _timer;

        public SuccessDialogWindow(string message)
        {
            InitializeComponent();
            DataContext = new { Message = message };
            
            // Setup auto-close timer (7 seconds)
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(7);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            _timer.Stop();
            this.DialogResult = true;
            this.Close();
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            this.DialogResult = true;
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            base.OnClosed(e);
        }
    }
}
