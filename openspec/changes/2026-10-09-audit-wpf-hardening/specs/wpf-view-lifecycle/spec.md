# Spec (delta): Ciclo de vida de vistas WPF — PERF-04/PERF-05 (8.156)

Fuente: auditoría integral re-emitida. PERF-04: "Guardar la referencia al delegado Action<bool> y desuscribirla en el método OnClosed de la ventana, llamando a ViewModel.Dispose()". PERF-05: "Adoptar el patrón implementado en DailyClosureView.xaml.cs".

## ADDED Requirements

### Requirement: REQ-WVL-01 — Los diálogos no dejan delegados vivos

`ProductDialog` y `ServerConnectionDialog` MUST almacenar el handler de `RequestClose` en un campo (`Action<bool>? _closeHandler`), suscribirlo una vez y desuscribirlo (`-=`, con null del campo) en `OnClosed` ANTES de disponer el VM. No MUST quedar lambdas anónimas suscritas al evento del VM (capturan `this` y sobreviven al cierre).

#### Scenario: Cierre desuscribe

- GIVEN un diálogo abierto con handler suscrito
- WHEN se cierra la ventana
- THEN el handler fue removido del VM antes de `ViewModel.Dispose()`
- AND no queda lambda anónima suscrita.

### Requirement: REQ-WVL-02 — Las vistas se desenganchan al desalojarse

`InventoryView` y `LoginView` MUST adoptar el patrón de `DailyClosureView` (8.149-W11): `_boundViewModel` con `HookViewModel`/`UnhookViewModel` idempotentes, enganche en `Loaded` y en `DataContextChanged` (solo si cambió la instancia), desenganche en `Unloaded`; `LoginView` MUST NOT disponer el `LoginViewModel` (singleton).

#### Scenario: Unloaded libera la referencia

- GIVEN una vista cargada con VM enganchado
- WHEN la vista se descarga (`Unloaded`)
- THEN el `PropertyChanged` del VM quedó desuscrito y la vista es recolectable.

#### Scenario: Re-carga y cambio de VM

- GIVEN la vista recargada o con `DataContext` nuevo
- THEN se engancha exactamente una vez al VM vigente (sin doble suscripción).

#### Scenario: LoginView no dispone el singleton

- GIVEN `LoginView` descargada
- THEN el VM NO fue dispuesto por la vista.

### Requirement: REQ-WVL-03 — Evidencia

- Build 0/0 y focused de ciclo de vida (`LoginViewModelLifecycleTests`, `ViewModelDisposalTests`) verde; los cambios son de code-behind XAML (verificación estructural + E2E gated como cobertura runtime del repo).
