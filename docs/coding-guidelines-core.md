# Coding Guidelines — Core (Reglas Universales)
## Sistema POS "CommandCenter"

**Versión:** 1.0.0
**Estado:** Vigente y de Aplicación Obligatoria
**Stack:** .NET 10 (C# 13), PostgreSQL 16/18, React 19 + Vite 8, WPF (.NET 10)

---

## 1. Filosofía Arquitectónica y Principios Fundamentales

El sistema opera bajo un modelo de desarrollo ágil donde **la automatización y la disciplina técnica reemplazan a la burocracia**. Cada línea de código debe diseñarse para ser mantenible, auditable y escalable.

### Los 7 Pilares de Calidad
1. **Mantenibilidad Primero:** Código simple, explícito y desacoplado. Tamaño objetivo: **300 a 500 líneas** por clase.
2. **Consistencia Total:** Las mismas convenciones en todo el repositorio. Se prohíbe introducir nueva deuda técnica.
3. **Seguridad por Defecto (Zero-Trust):** RBAC estricto (`Cashier`, `Manager`, `Admin`, `Driver`); `Driver` bloqueado en ventas, caja y cierres.
4. **Rendimiento Consciente:** Cero consultas N+1, `.AsNoTracking()` en lecturas, `.AsSplitQuery()` en relaciones complejas.
5. **Testeabilidad y Regresión Cero:** Suite completa al 100% de éxito.
6. **Observabilidad y Resiliencia:** Logs estructurados, captura centralizada de excepciones.
7. **Accesibilidad Operativa:** Interfaces optimizadas para teclado (hotkeys) y táctil/móvil.

### 1.1. Anti-God Objects y Modularización

#### A. Límites de Tamaño y SRP
* Máximo **300 a 500 líneas** por archivo de clase.
* Cada clase debe tener una única razón para cambiar.
  * *Ejemplo (clases parciales):* `SalesService.cs`, `SalesService.Pricing.cs`, `SalesService.HoldOrders.cs`.

#### B. Estrategias de División
1. **Clases Parciales (táctico):** Dividir en archivos cohesivos sin romper contratos públicos.
2. **Sub-servicios Inyectados (estratégico):** Extraer a clases independientes `Scoped` para cálculo puro o flujos independientes.

#### C. Métricas de Complejidad
* **Ciclomática (McCabe):** Máximo **10 por método**.
* **Métodos Públicos:** Máximo **15 a 20** por clase.
* **Acoplamiento:** Cero dependencias circulares; comunicación entre dominios vía **MediatR**.

---

## 2. Directrices de Backend (.NET 10 / C#)

### 2.1. Convenciones de Nomenclatura

| Elemento | Regla | Ejemplo Correcto | Ejemplo Prohibido |
|---|---|---|---|
| **Clases** | `PascalCase` | `SalesService` | `salesService` |
| **Interfaces** | `I` + `PascalCase` | `ISalesService` | `Delivery` |
| **Métodos** | `PascalCase` | `GetActiveSessionAsync` | `getActiveSession` |
| **Async** | Sufijo `Async` | `RegisterDeliveryOrderAsync` | `RegisterDeliveryOrder` |
| **Variables** | `camelCase` | `cancellationToken` | `order_id` |
| **Campos Privados** | `_` + `camelCase` | `_context` | `_snake_case` |
| **Propiedades** | `PascalCase` | `InvoiceNumber` | `invoice_number` |
| **Constantes** | `PascalCase` | `SecurityConstants.RoleAdmin` | `ROLE_ADMIN` |
| **Enums** | `PascalCase` singular | `OrderStatus.Pending` | `STATUS_PENDING` |
| **DTOs/Eventos** | `PascalCase` + sufijo | `DeliveryOrderDto` | `DeliveryData` |

### 2.2. Idiomática y Estilo
* **File-Scoped Namespaces:** Obligatorio.
* **Validación de Argumentos:** `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace`.
* **CancellationToken:** Todos los métodos asíncronos deben aceptar y propagar `CancellationToken`.
* **Prohibición de `async void`:** Solo `async Task` o `ValueTask`. En UI, usar `SafeFireAndForget`.

### 2.4. Integridad Financiera y Aritmética Multimoneda
* **Tipos de Datos Monetarios:** Queda **estrictamente prohibido** el uso de `float` o `double` para montos, subtotales, totales, comisiones o tasas de cambio. Se debe usar obligatoriamente `decimal`.
* **Redondeo Fiscal:**
  * Transacciones comerciales: `MidpointRounding.AwayFromZero` a 2 decimales.
  * Tasa BCV: Redondeo hacia arriba a 2 decimales (`Math.Ceiling(rate * 100m) / 100m`).
  * La tasa **redondeada es la referencia absoluta** para todo cálculo.
* **Precio unitario Bs.S como ley:** Ceiling a 2 decimales (`PricingCalculator.ToBsSCeiling`).
* **Inmutabilidad del Historial:** Nunca recalcular montos históricos con la tasa actual. Usar snapshots persistidos (`AppliedRate`, `TotalUSD`, `FinalPaidAmountBsS`, `TotalBsS`).

---

## 5. Preparación para Múltiples Sucursales (Multi-Branch Readiness)

> **DECISIÓN (8.25-E3):** El multi-branch es **intención arquitectónica futura**, no requisito actual. Sucursal única.

1. **BranchId:** Cuando se habilite, toda entidad transaccional incluirá `BranchId` (default `1`).
2. **Facturación Scoped:** Prefijos por sucursal y caja (ej. `B01-C01-0001245`).
3. **Stock por Sucursal:** Futuro `ProductBranchStock`.

---

## 7. Métricas de Aceptación (Definition of Done)

- [ ] 0 Errores y 0 Advertencias en .NET (`dotnet build -c Release`).
- [ ] 0 Errores de linting en Frontend (`npm run lint`).
- [ ] 100% de pruebas superadas en .NET y Web.
- [ ] Cobertura mantenida o incrementada.
- [ ] Sin dependencias vulnerables.
- [ ] Documentación sincronizada si se alteran contratos.
