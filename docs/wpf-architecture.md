# Arquitectura del Cliente Desktop WPF (.NET 10)

Este documento describe los estándares de rendimiento a 60 fps, manejo del hilo UI con Dispatcher, patrones MVVM y prevención de fugas de memoria en el cliente de escritorio `Desktop.Client`.

---

## 1. Patrón `BindingProxy` para DataGrid (Freezable)

Dado que `DataGridColumn` no deriva de `FrameworkElement`, no hereda el `DataContext` ni accede al VisualTree. Se utiliza un puente `Freezable`:

```xml
<UserControl.Resources>
    <!-- Puente Freezable conectado al DataContext del UserControl -->
    <helpers:BindingProxy x:Key="Proxy" Data="{Binding}" />
    <BooleanToVisibilityConverter x:Key="BoolToVis" />
</UserControl.Resources>

<!-- Columna con visibilidad enlazada al ViewModel principal -->
<DataGridTemplateColumn Visibility="{Binding Data.ShowWholesale, Source={StaticResource Proxy}, Converter={StaticResource BoolToVis}}">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <TextBlock Text="{Binding DisplayWholesalePrice}" />
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

---

## 2. Virtualización de Listas a 60 FPS

En vistas con grandes volúmenes de datos (`InventoryView.xaml` y `PosView.xaml`), se debe aplicar virtualización con reciclaje de celdas y desplazamiento suave por píxel:

```xml
<DataGrid VirtualizingStackPanel.IsVirtualizing="True"
          VirtualizingStackPanel.VirtualizationMode="Recycling"
          VirtualizingPanel.ScrollUnit="Pixel"
          ScrollViewer.IsDeferredScrollingEnabled="False">
```

---

## 3. Manejo del Hilo UI (`Dispatcher.BeginInvoke`)

Para evitar bloqueos durante la captura de escáneres físicos o atajos de teclado, las llamadas de enfoque deben enviarse asíncronamente con prioridad de entrada:

```csharp
Dispatcher.BeginInvoke(new Action(() =>
{
    SearchInput.Focus();
    SearchInput.SelectAll();
}), System.Windows.Threading.DispatcherPriority.Input);
```

---

## 4. Matriz de Atajos de Teclado POS y Supresión (`IsAnyModalOpen`)

| Atajo | Acción | Regla de Supresión / Guarda |
| :--- | :--- | :--- |
| `F1` | Cobrar / Checkout | Suprimido si `IsAnyModalOpen == true` o foco en `TextBox` secundario |
| `F2` | Enfocar Buscador | **Permitido siempre** (devuelve el foco a la caja de búsqueda) |
| `F3` | Cambiar Cliente | Suprimido si `IsAnyModalOpen == true` |
| `F4` | Pedido en Espera | Suprimido si `IsAnyModalOpen == true` |
| `F5` | Sincronizar Tasa BCV | Suprimido si `IsAnyModalOpen == true` |
| `F8` | Limpiar Carrito | Suprimido si `IsAnyModalOpen == true` |
| `ESC` | Cierre Jerárquico | Cierra modal activo primero; limpia buscador si no hay modales |

---

## 5. Higiene de Memoria (`IDisposable` & `Interlocked.Exchange`)

Todos los ViewModels deben desuscribirse de la mensajería débil y liberar tokens de cancelación atómicamente:

```csharp
public void Dispose()
{
    var oldCts = Interlocked.Exchange(ref _searchCts, null);
    try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }
    WeakReferenceMessenger.Default.UnregisterAll(this);
}
```
