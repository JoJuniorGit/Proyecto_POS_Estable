# Settings modular sections — Web

Branch: `V0.15` · Delivery: work-unit commits on the feature branch · Tests: `npm run lint`, `npm test`, `npm run build`.

## Specs

- **S1 — Navegación modular con lo existente.** La página `Configuración del Sistema` del cliente Web usa pestañas/panel lateral para cuatro dominios que ya existen en la web: `Moneda, Tasas y Formato`, `Métodos de Pago y Caja`, `Conectividad y Red Local`, `Sistema y Mantenimiento`. No se crea un apartado `Inventario y Reglas de Venta` vacío: la web no expone hoy controles de inventario, caja chica, comisiones, redondeo ni zona horaria.
- **S2 — Encabezado y feedback persistente.** El título `Configuración del Sistema` y el alert de mensajes permanecen fuera de las pestañas y visibles en todos los apartados.
- **S3 — Moneda.** `SettingsCurrencyFormat` se renderiza dentro de `Moneda, Tasas y Formato`, conservando selección, vista previa y persistencia actuales.
- **S4 — Métodos de pago protagonistas.** `SettingsPaymentMethods` se renderiza dentro de `Métodos de Pago y Caja`. La tabla de escritorio y las tarjetas móviles muestran la moneda asociada (`currency`) como dato de solo lectura. Se conservan alta, tipo Físico/Digital, requiere referencia, estado activo y eliminación.
- **S5 — Red.** `SettingsPairing` se renderiza dentro de `Conectividad y Red Local`, conservando QR, interfaces, datos de conexión manual y recarga.
- **S6 — Sistema.** La tarjeta `Reiniciar Sistema` se mueve al final de `Sistema y Mantenimiento`, aislada con borde de advertencia sutil; conserva confirmación, estado de reinicio y permisos.
- **S7 — Estado sin recargas.** Todos los paneles permanecen montados; cambiar de pestaña solo alterna visibilidad. No se repiten `GET /api/pairing/info` ni `GET /api/paymentmethods` al cambiar de apartado.
- **S8 — Accesibilidad.** La navegación usa `role="tablist"`, `role="tab"`, `role="tabpanel"`, `aria-selected`, `aria-controls`, `aria-labelledby`, foco roving y teclas `ArrowLeft/ArrowRight/ArrowUp/ArrowDown/Home/End`.
- **S9 — Sin backend nuevo.** No se agregan endpoints, DTOs ni controles de configuración nuevos. No se modifica el backend.
- **S10 — Verificación.** `npm run lint`, `npm test` y `npm run build` deben pasar sin errores.

## Tasks

- [ ] **T1 — Shell de pestañas y reubicación de paneles.** Reestructurar `SettingsPage.jsx`, agregar metadatos de secciones, navegación accesible y CSS de sidebar/responsive; mover moneda, pagos, red y reinicio a sus paneles. Route: inline.
- [ ] **T2 — Moneda en métodos de pago.** Agregar columna `Moneda` en la tabla de escritorio y chip en las tarjetas móviles usando `m.currency`. Route: inline.
- [ ] **T3 — Pruebas y verificación.** Test de metadatos/navegación por teclado; lint, test y build. Route: inline.
- [ ] **T4 — ANEXO y cierre.** Registrar ANEXO LV en `docs/reporte.txt`. Route: inline.

## Log

### L1 — Pedido original (verbatim)

Aplica las mejoras a la version WEb

### L2 — Hallazgos de exploración

- `SettingsPage.jsx` monta en una sola página: `SettingsPairing`, `SettingsCurrencyFormat`, la tarjeta de reinicio y `SettingsPaymentMethods`.
- La web no tiene controles de stock negativo, pricing/redondeo, comisiones de adelanto, zona horaria ni régimen monetario; no se agregan por S9.
- `SettingsPairing` carga `/api/pairing/info` en `useEffect` al montar y `SettingsPage` carga `/api/paymentmethods` una vez; mantener los paneles montados evita recargas al cambiar de pestaña.
- `SettingsPaymentMethods` ya recibe `m.currency` del backend, pero no lo muestra; se agrega solo lectura.
- No hay un componente de tabs reutilizable en la web; se implementa uno local con roles ARIA y navegación por teclado.
