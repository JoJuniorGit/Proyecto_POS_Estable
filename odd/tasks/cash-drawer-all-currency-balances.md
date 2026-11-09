# Cash drawer — all physical currency balances, local first

Branch: `V0.15` · Delivery: work-unit commits on the feature branch · Tests: `npm run lint`, `npm test`, `npm run build`, `dotnet build CommandCenter.slnx -c Release`, targeted `dotnet test`.

## Specs

- **S1 — Resumen físico por moneda siempre visible.** La caja muestra un resumen de existencia física por moneda, no solo por método. El resumen incluye todas las monedas del perfil activo: local primero, luego primaria/secundaria, y cualquier otra moneda presente en los saldos por método. Una moneda sin métodos se muestra en `0.00`.
- **S2 — Prioridad de la moneda local.** La moneda local del perfil (secundaria en `Dual`, primaria en `Single`) aparece primero en el resumen y en el detalle por método. Las demás siguen el orden primaria, secundaria, otras.
- **S3 — Detalle por método ordenado.** Las tarjetas de saldo por método físico se ordenan por prioridad de moneda y luego por nombre/ID, sin perder ninguna moneda ni método.
- **S4 — Siempre visible.** La sección de existencias físicas no se oculta cuando no hay métodos: el resumen de monedas del perfil se muestra igualmente con saldos en cero.
- **S5 — Paridad Web/WPF.** La regla se aplica en `RegisterPage` (Web) y `CashDrawerView`/`CashDrawerViewModel` (WPF), usando los datos y el perfil monetario existentes.
- **S6 — Sin backend nuevo.** No se agregan endpoints, DTOs ni cálculos de saldo en el servidor. El cliente agrega/ordena la información ya devuelta por `physicalMethodBalances` y el perfil monetario.
- **S7 — Permisos intactos.** No se altera el arqueo ciego ni la visibilidad por rol; solo se reorganiza y completa la presentación de los saldos ya expuestos.
- **S8 — Verificación.** Lint/tests/build Web y build/tests WPF pasan; se agregan pruebas unitarias de los helpers/colecciones de resumen.

## Tasks

- [x] **T1 — Web: helpers de resumen y orden.** `buildPhysicalCurrencySummaries` y `sortPhysicalMethodBalances` con pruebas en `physicalBalances.test.js`. Commit `5fcaca8`.
- [x] **T2 — Web: UI y CSS.** `RegisterPage` muestra el resumen siempre visible, local primero, y el detalle por método ordenado. Commit `5fcaca8`.
- [x] **T3 — WPF: resumen y orden.** `PhysicalCurrencyBalances`, reconstrucción desde el perfil y orden de `PhysicalMethodBalances`; XAML del resumen. Commit `5fcaca8`.
- [x] **T4 — Verificación y ANEXO.** Web lint/tests/build OK; WPF build 0/0, targeted 9/9, suite backend 4207/4207; ANEXO LVI. Commit `5fcaca8` + docs.

## Log

### L1 — Pedido original (verbatim)

Necesito que la caja Muestre siempre la existencia de todas las monedas en Fisico, dandole prioridad a la moneda local

### L2 — Hallazgos de exploración

- Web: `RegisterPage` muestra `EXISTENCIAS POR MÉTODO FÍSICO` solo si `physicalMethodBalances.length > 0`; las tarjetas se pintan en el orden devuelto por el backend (por `PaymentMethodId`). El backend devuelve todas las filas de métodos, pero una moneda sin fila no aparece.
- WPF: `CashDrawerView` oculta la sección con `HasPhysicalMethodBalances` (count > 0); `PhysicalMethodBalances` se llena en el orden del servicio. `CurrencyDisplay` expone local/primaria/secundaria y `SymbolFor`.
- El perfil monetario define la moneda local: secundaria en `Dual`, primaria en `Single`. El resumen puede construirse en el cliente sin backend.
- El KPI de efectivo esperado no es la existencia física; la regla se aplica a la sección de existencias físicas.

### L3 — Implementación y verificación

- Web: `buildPhysicalCurrencySummaries` arma el resumen local/primaria/secundaria + monedas extra de los métodos, con totales y ceros; `sortPhysicalMethodBalances` ordena local primero y luego por nombre/ID. `RegisterPage` renderiza `EXISTENCIAS FÍSICAS POR MONEDA` sin condicionar a que existan métodos.
- WPF: `CashDrawerViewModel` agrega `PhysicalCurrencyBalances` y lo reconstruye desde `CurrencyDisplay` + `PhysicalMethodBalances`; `CashDrawerView` muestra el resumen y mantiene el detalle por método. El mensaje de perfil usa `WeakReferenceMessenger` para no dejar handlers estáticos.
- Evidencia Web: `npm run lint` OK; `npm test` 752/752; `npm run build` OK; bundle `Backend.API/wwwroot` regenerado.
- Evidencia WPF: build 0/0; `CashDrawerMonetaryProfileDisplayTests` 9/9; suite backend completa 4207/4207.
- Sin cambios de backend, permisos ni arqueo ciego.
