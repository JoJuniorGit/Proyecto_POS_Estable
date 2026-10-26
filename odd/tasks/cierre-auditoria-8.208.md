# Cierre final de residuales de auditoría (8.208)

Objetivo: cerrar los 2 hallazgos abiertos y los 12 parciales que quedaron tras 8.207.
Rama: `V0.15`. Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` + carril PostgreSQL aislado.

## Specs

- **S1 — CLEAN-11:** eliminar `BaseViewModel.cs`, `CheckoutSummary.jsx` y `CheckoutHeader.jsx` sin consumidores.
- **S2 — CLEAN-12:** unificar los parsers decimales de `SupplierInvoiceViewModel.Staging` y `ExchangeRateViewModel` en `FlexibleDecimalParser`.
- **S3 — CLEAN-05-RES:** eliminar tolerancias locales `0.05` en PendingOrders web y checkout WPF; usar totales/preview del servidor.
- **S4 — CLEAN-06-RES:** los `catch` de Settings ViewModel registran el error y revierten el estado optimista.
- **S5 — CLEAN-07/08:** eliminar catches silenciosos en `UserService.GetUsersAsync`, `CustomerPickerViewModel` y Register web.
- **S6 — PERF-08/09:** evitar reconsultas del mismo producto en `AddItemAsync` y usar el cliente por defecto cacheado en `StartSale`/adelanto.
- **S7 — PERF-10:** reducir el GET extra y el envío pago-por-pago del checkout en espera.
- **S8 — PERF-R04:** cerrar las fugas WPF restantes (`PosViewModel`, `Process.Start`, OCR, diálogos).
- **S9 — SRE-09:** `SupplierInvoiceLine` con token `xmin`.
- **S10 — SRE-10:** idempotencia en apertura/cierre de caja y stage de facturas; guard de cierre por ventana del día.
- **S11 — SRE-11:** auditar `403` originados por excepción.
- **S12 — SRE-12:** logging estructurado en el handler global y protección de 5xx ante saturación.
- **S13 — Verificación:** build 0/0; focalizados; suite completa con PostgreSQL; estado final de la auditoría.

## Tasks

| ID | Specs | Estado | Evidencia |
| --- | --- | --- | --- |
| WU1 | S1, S5 | completado | archivos huérfanos eliminados; `UserService`/`CustomerPicker` con log y error |
| WU2 | S2 | completado | `FlexibleDecimalParser` compartido; `TryParseRate` delega |
| WU3 | S3, S4 | completado | PendingOrders usa `isFullLiquidation` del preview; Settings revierte y loguea |
| WU4 | S6, S7 | completado | producto reutilizado en AddItem; cliente default cacheado; batch de cantidades |
| WU5 | S8, S9 | completado | `_cart.Dispose`, `Process.Start` using, PNG IHDR, diálogos limpios; `SupplierInvoiceLine` xmin |
| WU6 | S10 | completado | idempotencia opcional open/close/stage; guard de cierre por sesión |
| WU7 | S11, S12 | completado | 403 por excepción auditado; log estructurado y `EnqueueCritical` |
| WU8 | S13 | completado | build 0/0; .NET 3595/3595; web 626/626; lint/build OK; matriz 39/39 |

## Log

- **L1 (2026-10-26):** inicio por pedido del mantenedor. Estado de partida: 25 cerrados, 12 parciales, 2 abiertos.
- **L2 (2026-10-26, cierre):** CLEAN-11/12 cerrados (archivos huérfanos eliminados; parser decimal unificado), CLEAN-05/06/07/08 cerrados, PERF-08/09/10 y PERF-R04 cerrados, SRE-09 (xmin de `SupplierInvoiceLine` + migración vacía de snapshot), SRE-10 (idempotencia opcional en apertura/cierre/stage y guard de cierre por sesión), SRE-11 y SRE-12 (auditoría de 403 por excepción, logging estructurado y contrapresión para logs críticos). Evidencia: build 0/0; suite .NET **3595/3595** con PostgreSQL aislado; web **626/626**, `npm run lint` limpio y `npm run build` OK. El clúster se detuvo al finalizar.
