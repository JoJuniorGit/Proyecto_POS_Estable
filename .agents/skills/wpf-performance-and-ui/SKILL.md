---
name: wpf-performance-and-ui
description: >-
  Standardizes WPF MVVM desktop patterns, UI thread management with Dispatcher.BeginInvoke, DataGrid column
  binding via the BindingProxy Freezable pattern, memory leak prevention (IDisposable, WeakReferenceMessenger),
  and POS keyboard hotkey standards with modal isolation. Activate this skill when building or editing XAML views,
  WPF viewmodels, keyboard shortcuts, DataGrid layouts, or resolving UI freezing and memory leaks.
---

# WPF Performance, MVVM & UI Polishing Guide

This skill governs desktop client development in `.NET 10 WPF`, focusing on 60fps responsiveness, clean MVVM declarative bindings, memory leak prevention, and POS industry hotkey standards.

---

## 1. Concrete XAML Pattern: `BindingProxy` & `VirtualizingStackPanel`

`DataGridColumn` is not a `FrameworkElement` and cannot access the VisualTree or inherit `DataContext`. The canonical solution uses a `Freezable` proxy declared in `UserControl.Resources` combined with row recycling for 60fps scrolling:

### A. C# Proxy Helper: `Desktop.Client/Helpers/BindingProxy.cs`
```csharp
using System.Windows;

namespace Desktop.Client.Helpers;

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
```

### B. Complete XAML Layout Example: `InventoryView.xaml`
```xml
<UserControl x:Class="Desktop.Client.Views.InventoryView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:helpers="clr-namespace:Desktop.Client.Helpers">
    
    <UserControl.Resources>
        <!-- 1. Puente de datos Freezable conectado al DataContext de la vista -->
        <helpers:BindingProxy x:Key="Proxy" Data="{Binding}" />
        <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
    </UserControl.Resources>

    <Grid Margin="12">
        <DataGrid x:Name="ProductsDataGrid"
                  ItemsSource="{Binding Products}" 
                  AutoGenerateColumns="False" 
                  CanUserAddRows="False"
                  HeadersVisibility="Column"
                  RowHeight="40"
                  
                  <!-- 2. Virtualización de alto rendimiento para miles de productos -->
                  VirtualizingStackPanel.IsVirtualizing="True"
                  VirtualizingStackPanel.VirtualizationMode="Recycling"
                  ScrollViewer.IsDeferredScrollingEnabled="False">

            <DataGrid.Columns>
                <!-- Columna estándar -->
                <DataGridTextColumn Header="Producto" Binding="{Binding Name}" Width="*" />
                <DataGridTextColumn Header="SKU" Binding="{Binding SKU}" Width="120" />
                <DataGridTextColumn Header="Stock" Binding="{Binding StockQuantity}" Width="80" />

                <!-- 3. Columna con Visibilidad y Encabezado Dinámicos enlazados vía BindingProxy -->
                <DataGridTemplateColumn Header="{Binding Data.WholesalePriceHeader, Source={StaticResource Proxy}, FallbackValue='Precio Mayor (Bs.S)'}" 
                                        Visibility="{Binding Data.ShowWholesale, Source={StaticResource Proxy}, Converter={StaticResource BooleanToVisibilityConverter}}"
                                        Width="140">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <TextBlock Text="{Binding DisplayWholesalePrice}" 
                                       FontWeight="Bold" 
                                       Foreground="#D97706" 
                                       HorizontalAlignment="Right"
                                       VerticalAlignment="Center"/>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>

                <!-- Columna Mínimo Mayor con Visibilidad enlazada vía BindingProxy -->
                <DataGridTextColumn Header="Cant. Mín. Mayor" 
                                    Binding="{Binding MinWholesaleQuantity}" 
                                    Visibility="{Binding Data.ShowWholesale, Source={StaticResource Proxy}, Converter={StaticResource BooleanToVisibilityConverter}}"
                                    Width="110"/>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</UserControl>
```

---

## 2. Zero `MessageBox.Show` Policy & `IDialogService`

**Rule**: Calling `MessageBox.Show()` directly freezes execution, causes UI thread blocks, and breaks automated unit testing. Always inject and use `IDialogService`:

```csharp
public async Task HandleCancelSaleAsync()
{
    bool confirmed = await _dialogService.ShowConfirmAsync(
        "Cancelar Venta", 
        "¿Está seguro de que desea cancelar la venta actual? Esta acción no se puede deshacer.");

    if (confirmed)
    {
        ClearCart();
    }
}
```

---

## 3. Window Lifecycle & Orphan Window Prevention

When opening secondary modal or tool windows (e.g. Floating Scanner, Daily Closure Dialog), ALWAYS assign the parent `Owner`:

```csharp
public void OpenScannerWindow()
{
    var scannerWindow = new ScannerWindow
    {
        Owner = Application.Current?.MainWindow, // Closes automatically if main window closes
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };
    scannerWindow.Show();
}
```

---

## 4. UI Thread & Dispatcher Focus Management

Never call synchronous `Focus()` inside a key handler if the active event pump might intercept it. Use `Dispatcher.BeginInvoke`:

```csharp
private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key == Key.F2)
    {
        e.Handled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchInput.Focus();
            SearchInput.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }
}
```

---

## 5. Memory Hygiene & Leak Prevention Checklist

1. **IDisposable on ViewModels**:
   ```csharp
   public void Dispose()
   {
       var oldCts = Interlocked.Exchange(ref _searchCts, null);
       try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }
       WeakReferenceMessenger.Default.UnregisterAll(this);
   }
   ```
2. **DataContextChanged Unhooking**:
   ```csharp
   private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
   {
       if (e.OldValue is INotifyPropertyChanged oldVm) oldVm.PropertyChanged -= ViewModel_PropertyChanged;
       if (e.NewValue is INotifyPropertyChanged newVm) newVm.PropertyChanged += ViewModel_PropertyChanged;
   }
   ```

---

## 6. POS Keyboard Hotkey Standards & Modal Gating

| Key | Standard Action | Modal Gating Guard |
| :--- | :--- | :--- |
| `F1` | Checkout / Cobrar | Suppressed when modal is open |
| `F2` | Focus Search Input | Suppressed when modal is open |
| `F3` | Change / Pick Customer | Suppressed when modal is open |
| `F4` | Hold Order (En Espera) | Suppressed when modal is open |
| `F5` | Sync BCV Rate / Catalog | Suppressed when modal is open |
| `F7` | Toggle Price List (Retail/Wholesale) | Allowed outside modals |
| `F8` | Cancel / Clear Current Sale | Prompt confirmation |
| `ESC` | Hierarchy: Close Top Modal -> Clear Search | Closes active dialog first |
| `+` / `-` / `Supr` | Modify Quantity / Remove Item | **Suppressed when focused in TextBox/Input** |

---

## 7. Self-Evaluation Test Suite

Run WPF ViewModel and UI unit tests:
```powershell
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~PosHotkeys|FullyQualifiedName~Inventory"
```
