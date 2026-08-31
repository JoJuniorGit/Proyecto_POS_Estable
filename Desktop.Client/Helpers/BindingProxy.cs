using System.Windows;

namespace Desktop.Client.Helpers;

/// <summary>
/// Provee un puente de datos mediante Freezable para permitir que elementos no visuales
/// (como DataGridColumn) puedan enlazar propiedades al DataContext de la vista.
/// </summary>
public class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public object Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));
}
