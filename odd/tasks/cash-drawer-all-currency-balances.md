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

- [ ] **T1 — Web: helpers de resumen y orden.** Agregar `buildPhysicalCurrencySummaries` y `sortPhysicalMethodBalances` en la lógica de caja, con pruebas. Route: inline.
- [ ] **T2 — Web: UI y CSS.** Renderizar el resumen por moneda siempre visible, local primero, y el detalle por método ordenado. Route: inline.
- [ ] **T3 — WPF: resumen y orden.** Agregar `PhysicalCurrencyBalances`, reconstrucción desde el perfil y orden de `PhysicalMethodBalances`; XAML del resumen. Route: inline.
- [ ] **T4 — Verificación y ANEXO.** Lint/tests/build Web; build/tests WPF; ANEXO LVI. Route: inline.

## Log

### L1 — Pedido original (verbatim)

Necesito que la caja Muestre siempre la existencia de todas las monedas en Fisico, dandole prioridad a la moneda local

### L2 — Hallazgos de exploración

- Web: `RegisterPage` muestra `EXISTENCIAS POR MÉTODO FÍSICO` solo si `physicalMethodBalances.length > 0`; las tarjetas se pintan en el orden devuelto por el backend (por `PaymentMethodId`). El backend devuelve todas las filas de métodos, pero una moneda sin fila no aparece.
- WPF: `CashDrawerView` oculta la sección con `HasPhysicalMethodBalances` (count > 0); `PhysicalMethodBalances` se llena en el orden del servicio. `CurrencyDisplay` expone local/primaria/secundaria y `SymbolFor`.
- El perfil monetario define la moneda local: secundaria en `Dual`, primaria en `Single`. El resumen puede construirse en el cliente sin backend.
- El KPI de efectivo esperado no es la existencia física; la regla se aplica a la sección de existencias físicas.
