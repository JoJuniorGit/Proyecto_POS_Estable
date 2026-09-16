# DEUDA LEGACY REGISTRADA — Revisiones GGA de los ANEXOS 8.137 / 8.138 / 8.139

**Fecha:** 2026-09-16
**Alcance:** hallazgos de deuda PREEXISTENTE detectados por el hook GGA (revisa archivos completos, no diffs) en los archivos tocados por las slices 1–3 del change `cash-closure-integrity`. Todos fueron diferidos con excepción `--no-verify` autorizada por el mantainer (ANEXOS 8.137-F, 8.138-G1, 8.139-G1). Se registran aquí para su saneamiento en sesión futura.
**Mapeo con el audit:** `docs/analisis-exhaustivo-sistema-2026.md` — H-03 (DbContext en controllers), H-04 (entidades EF sin DTO), H-09 (CancellationToken), H-10 (consolidación de cierre).
**Nota:** los hallazgos de código NUEVO de las slices (CT/complejidad del coordinator, tests tautológicos, nombres de tests, ProblemDetails del 400) ya fueron resueltos y commiteados. Esto es SOLO lo diferido.

---

## 1. `Backend.API/Controllers/DailyClosureController.cs` (slice 1)

1. **[H-03] Persistencia en controller**: inyecta y usa `SalesDbContext` + `InventoryDbContext` (execution strategy, `BeginTransactionAsync`, `ExchangeRateResolver.ReadEffectiveTodayRateAsync`). La orquestación transaccional debe vivir en el service layer.
2. **Errores sin ProblemDetails**: `BadRequest(new { message = ... })` en sitios legacy (p. ej. ~:76, ~:87 y otros). NOTA: el 400 del preview YA usa `Problem(...)` (corregido en slice 1).
3. **[H-04] Entidades EF al cliente**: `GetClosure`/`CreateClosure` serializan `DailyClosure`/`ClosureDetail`.
4. **[H-09] CancellationToken**: ausente en `GetExpectedTotals` (nuestro método acepta validación pero no CT), `CreateClosure`, `GetClosure`, `GetTodayExchangeRateAsync`.
5. **Comentarios explicativos**: ~L46, ~L92, ~L95 + inline (`// 1. Identidad...`, `// 2. Totales esperados...`, `// Al cerrar el turno...`). Mantener solo marcadores `8.x-*`.
6. **Complejidad McCabe**: `CreateClosure` ~16 (>10). Mezcla auth + backdating + validación + transacción + armado de entidad (además SRP).

## 2. `Sales.Module/Services/DailyClosureService.cs` (slice 1)

7. **Rendimiento EF**: `GetClosureAsync` sin `.AsNoTracking()` y `.Include(dc => dc.Details)` sin `.AsSplitQuery()`.
8. **[H-04] Retorna entidad EF**: `GetClosureAsync`/`CreateClosureAsync` devuelven `DailyClosure` y fluyen al cliente.
9. **[H-09] CancellationToken**: ausente en `GetExpectedTotalsByPaymentMethodAsync`, `CreateClosureAsync`, `ExecuteClosureCoreAsync`, `GetClosureAsync`.
10. **Comentarios explicativos**: ~L63, ~L76, ~L112, ~L140, ~L200.
11. **Guard idiom**: `if (closure == null) throw new ArgumentNullException(...)` en `WriteClosedClosureReceipts` → usar `ArgumentNullException.ThrowIfNull`.
12. **Resiliencia/observabilidad**: `TryWriteFileWithRetry`/`TryWriteTextWithRetry` tienen `catch { }` sin logging y `Thread.Sleep` bloqueante en ruta async.
13. **SRP**: la clase acumula queries + orquestación transaccional + generación de texto de recibo + PDF + I/O multi-ruta.
14. **Complejidad borde**: `ExecuteClosureCoreAsync` ~12 y `GenerateReceiptContent` ~9.

## 3. `Backend.API/Controllers/CashDrawerController.cs` (slice 2)

15. **[H-04] Entidades EF al cliente**: `GetActiveSession`, `OpenSession`, `CloseSession`, `AddTransaction` devuelven `CashDrawerSession`/`CashTransaction` directamente (y `CashAdvanceResultDto` envuelve transacciones EF).
16. **[H-09] CancellationToken**: ausente en todas las actions y en los privados `ResolveAnchoredRateAsync`/`MapLocalTimesAsync`.
17. **Comentarios explicativos**: ~L52, ~L70-74, ~L141-143, ~L154-157, ~L159-160, ~L208-215 (mantener solo marcadores `8.x-*`).
18. **Errores sin ProblemDetails**: `AddTransaction` con `BadRequest/StatusCode(403)` y objetos anónimos (~L173, ~L184, ~L189, ~L194). El repo ya tiene helpers `ApiProblemResults` (`Backend.API/Controllers/ApiProblemResults.cs`) que DEBEN usarse.

## 4. `Sales.Module/Services/CashDrawerService.cs` + `Sales.Module/Interfaces/ICashDrawerService.cs` (slice 2)

19. **[H-04] Entidades EF**: `ICashDrawerService.GetHistoryAsync` devuelve `List<CashTransaction>`; `CashAdvanceResultDto` expone `CashTransaction ExpenseTransaction/IncomeTransaction`; los métodos públicos de `CashDrawerService` devuelven entidades EF.
20. **Anti-patrón de proyección**: `GetHistoryAsync` materializa `new CashTransaction { Sale = new Sale { ... } }` (instancias de entidad fuera de tracking).
21. **[H-09] CancellationToken**: ausente en todos los métodos async del servicio.
22. **Rendimiento EF**: `GetActiveSessionWithTransactionsAsync` con `Include...ThenInclude` sin `AsNoTracking`/`AsSplitQuery`; demás lecturas tracking.
23. **Comentarios explicativos**: ~L19, ~L67, ~L105-108, ~L141, ~L185-186, ~L240-242, ~L340-341.

## 5. `Backend.API/Startup/ServiceCollectionExtensions.cs` (slice 2)

24. **Comentarios explicativos/narrativos**: ~L29-31, L56-59, L64, L93, L119-120, L146, L175-176, L190-199, L205-212, L230-231; XML docs duplican lo que el código expresa.

## 6. `Backend.API/Controllers/ShiftsController.cs` (slice 3)

25. **[BLOCKER / RFC 7807] Errores sin helpers `ApiProblemResults`**: `:74`, `:106`, `:239`, `:266`, `:278` con objetos anónimos + deriva de casing `message`/`Message`. Usar `ApiBadRequest/ApiNotFound/ApiForbidden`.
26. **[BLOCKER / ZERO-TRUST — registrado también en 8.139-G2] `CloseShift` confía en `request.Currency` del cliente**: `:117` (`== "USD"`), `:134` (echo), `:185-187` (decide montos). Duplica la clasificación (segunda fuente de verdad vs `PaymentMethodCurrencyResolver`) y permite alterar el arqueo desde el cliente. Consolidar server-side con el resolver en el contexto de H-10/C3.
27. **[H-09] CancellationToken**: `:52`, `:59`, `:223`, `:244`.
28. **Falta sufijo Async**: `CloseShift` `:59`, `GetCurrentReport` `:223`, `GetReportById` `:244`.
29. **Dependencias inyectadas muertas**: `_paymentMethodService` y `_settingsService` (`:27-28`, `:44-45`).
30. **[H-03] DbContext cross-domain desde el controller**: `:225` (`DailyClosures`), `:150`/`:159`/`:305` (`Users`).
31. **Rendimiento EF**: `GetCurrentReport` `:225-228` (Include sin `AsNoTracking`/`AsSplitQuery`); `:159`, `:305`.
32. **Validación de argumentos**: `CloseShift` `:59` sin null-guard de `request`; `DeclaredAmounts` null → NRE en `:66`.
33. **Comentarios explicativos**: `:94`, `:169`, `:174`, `:142`, `:193` (mantener marcadores `8.x-*`).
34. **Magic strings**: `"Balanced"/"Surplus"/"Shortage"` (`:128`, `:288`), `"Bs.S"` (`:333`, `:350`) → constantes/enum.
35. **Contrato muerto**: `CloseShiftRequest.CashierName/CashierCedula` (`:323-324`) aceptados y descartados.
36. **Orden de guardas**: en `:270-274`, la tasa se computa antes del check `closure == null`.
37. **Legibilidad**: lambda `:84-212` sin indentación relativa (scope transaccional difícil de revisar).

## 7. Tests con comentarios legacy (slice 2)

38. Comentarios narrativos `//` en `CashDrawerClosureTests.cs` (~L150, L176, L190-196, L222), `Phase3ConcurrencyAndReservationTests.cs` (~L53, L64-65, L97, L101), `SecurityTests.cs` (~L49, L59, L82, L129, L190, L287), `CashDrawerServiceUnitTests.cs` (~L60, L78, L90, L100, L120, L148-149). Algunos en español mezclado (regla de idioma de artefactos).

## 8. Otros registros fuera de scope

39. **Web `RegisterClosePage.jsx:61`**: conserva fallback de clasificación `usd/dolar/$/divisa` (case/accent-sensitive) — misma familia que el ítem 26; unificar con el resolver cuando se saneen los clientes.
40. **OpenCode `question` tool**: no expone NI honra el control `custom` (bloqueo del review nativo RDD). Ver memoria Engram `compat/opencode-question-custom-control`.

---

## Agrupación sugerida para el saneamiento (sesión futura)

| Grupo | Tema | Ítems | Mapeo al plan |
|-------|------|-------|---------------|
| A | DTOs de caja/cierres (entidades fuera) | 3, 8, 15, 19, 20 | H-04 / C2 |
| B | CancellationToken sweep | 4, 9, 16, 21, 27 | H-09 / C3 |
| C | ProblemDetails + helpers `ApiProblemResults` | 2, 18, 25 | H-11-family / C2 |
| D | Comentarios explicativos + idioma | 5, 10, 17, 23, 24, 33, 38 | limpieza transversal |
| E | Rendimiento EF (AsNoTracking/AsSplitQuery) | 7, 22, 31 | rendimiento |
| F | SRP/complejidad | 6, 13, 14 | refactor estructural |
| G | Zero-trust cierre (resolver server-side) | 26, 39 | H-10 / C3 |
| H | Guardas/contratos/dead code | 11, 12, 28, 29, 32, 34, 35, 36, 37 | limpieza |
| I | DbContext fuera de controllers | 1, 30 | H-03 / C2 |

**Estado del change `cash-closure-integrity` al cierre de esta sesión:** slices 1–3 commiteadas (a94c536, a34584c, b016c5d, 711710b); coverage gate OK (Core 0.8096 / Sales 0.8540 / Inventory 0.7937); **pendiente**: fase `sdd-verify` + `sdd-archive` del change, y luego el resto del plan de remediación (C2/C3/C4).
