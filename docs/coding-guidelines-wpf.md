# Coding Guidelines — Desktop WPF / MVVM

> Sección WPF. Aplica a cambios en `Desktop.Client`, `Desktop.Client.Core`.

---

## 4.1. Adopción de CommunityToolkit.Mvvm

* **Source Generators:**
  * `[ObservableProperty]` sobre campos privados en `_camelCase`.
  * `[RelayCommand]` sobre métodos con lógica de comandos.
* **Prohibición de `_snake_case`:** Todo campo privado debe seguir `_camelCase`.
* **Idioma:** Mensajes de validación y diálogos en español formal.

## 4.2. Ciclo de Vida y Prevención de Fugas de Memoria

* **`IDisposable`:** Cualquier ViewModel con timers, `CancellationTokenSource` o `WeakReferenceMessenger` debe implementar `IDisposable`.
* **Cierre de Vistas:** En `OnClosed`, invocar `(DataContext as IDisposable)?.Dispose()`.
* **Virtualización:** `DataGrid` y `ListBox` con listas extensas deben usar `VirtualizingStackPanel.IsVirtualizing="True"` y `VirtualizationMode="Recycling"`.
