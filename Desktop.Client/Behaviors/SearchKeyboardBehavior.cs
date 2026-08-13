using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace Desktop.Client.Behaviors
{
    /// <summary>
    /// A custom behavior for a TextBox that redirects Up and Down arrow keys 
    /// to navigate a bound ListBox's selection without stealing focus from the TextBox.
    /// Also listens for the Enter key to execute a designated Command while keeping focus.
    /// </summary>
    public class SearchKeyboardBehavior : Behavior<TextBox>
    {
        public static readonly DependencyProperty TargetListBoxProperty =
            DependencyProperty.Register(
                nameof(TargetListBox),
                typeof(ListBox),
                typeof(SearchKeyboardBehavior),
                new PropertyMetadata(null));

        public ListBox TargetListBox
        {
            get => (ListBox)GetValue(TargetListBoxProperty);
            set => SetValue(TargetListBoxProperty, value);
        }

        public static readonly DependencyProperty EnterCommandProperty =
            DependencyProperty.Register(
                nameof(EnterCommand),
                typeof(ICommand),
                typeof(SearchKeyboardBehavior),
                new PropertyMetadata(null));

        public ICommand EnterCommand
        {
            get => (ICommand)GetValue(EnterCommandProperty);
            set => SetValue(EnterCommandProperty, value);
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.PreviewKeyDown += AssociatedObject_PreviewKeyDown;
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            AssociatedObject.PreviewKeyDown -= AssociatedObject_PreviewKeyDown;
        }

        private void AssociatedObject_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TargetListBox == null || TargetListBox.Items.Count == 0)
                return;

            if (e.Key == Key.Down)
            {
                e.Handled = true; // Prevent text cursor from jumping
                if (TargetListBox.SelectedIndex < TargetListBox.Items.Count - 1)
                {
                    TargetListBox.SelectedIndex++;
                    TargetListBox.ScrollIntoView(TargetListBox.SelectedItem);
                }
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true; // Prevent text cursor from jumping
                if (TargetListBox.SelectedIndex > 0)
                {
                    TargetListBox.SelectedIndex--;
                    TargetListBox.ScrollIntoView(TargetListBox.SelectedItem);
                }
            }
            else if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                // Only swallow enter if there's actually a valid selection and command
                if (TargetListBox.SelectedItem != null && EnterCommand != null)
                {
                    e.Handled = true;

                    if (EnterCommand.CanExecute(TargetListBox.SelectedItem))
                    {
                        EnterCommand.Execute(TargetListBox.SelectedItem);
                    }
                    
                    // Keep the focus strictly locked to the textbox
                    AssociatedObject.Focus();
                }
            }
        }
    }
}
