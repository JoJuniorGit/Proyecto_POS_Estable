# Auditoría Exhaustiva del Sistema — CommandCenter POS (V0.1)
**Fecha:** 15 de Septiembre de 2026  
**Autor:** Antigravity (Senior System Architect)  
**Alcance:** Backend.API, Core, Sales.Module, Inventory.Module, Logistics.Module, Desktop.Client (WPF), Desktop.Client.Core, Web.Frontend (React 19), UpdaterService.  
**Estado:** Hallazgos Nuevos y Áreas de Mejora Priorizadas  

---

## 1. Resumen Ejecutivo

El sistema **CommandCenter POS** cuenta con sólidas bases técnicas: suite de 1.111 pruebas automatizadas en .NET y 268 en Web Frontend al 100% de éxito, arquitectura orientada a eventos para notificaciones en tiempo real (SignalR), endurecimiento de cabeceras de seguridad y políticas multimoneda estrictas con redondeo fiscal hacia arriba (Ceiling).

No obstante, una inspección forense y exhaustiva del código fuente revela **fricciones arquitectónicas estructurales, fugas de abstracción de base de datos a nivel de controladores, ventanas sutiles de riesgo en el ciclo contable de medianoche, brechas de autenticación en redes locales (LAN) para terminales móviles, retención de recursos en clientes WPF y componentes monolíticos que superan los límites de mantenibilidad definidos por las reglas del proyecto (300 a 500 líneas)**.

A continuación se desglosan los hallazgos categorizados por disciplina técnica, acompañados de evidencia empírica en el código, análisis de impacto y recomendaciones concretas de remediación.

---

## 2. Arquitectura General y Modularidad (Clean Architecture)

### 2.1. Fuga Masiva de `DbContext` a Controladores API (Violación de Capas)
* **Severidad:** ALTA (Arquitectónica)
* **Archivos Afectados:**
  * [`Backend.API/Controllers/SettingsController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/SettingsController.cs)
  * [`Backend.API/Controllers/ExchangeRateController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ExchangeRateController.cs)
  * [`Backend.API/Controllers/UsersController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/UsersController.cs)
  * [`Backend.API/Controllers/ReceiptsController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ReceiptsController.cs)
  * [`Backend.API/Controllers/ShiftsController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ShiftsController.cs)
  * [`Backend.API/Controllers/DailyClosureController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/DailyClosureController.cs)
  * [`Backend.API/Controllers/CashDrawerController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/CashDrawerController.cs)
  * [`Backend.API/Controllers/ReservationsController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ReservationsController.cs)

* **Diagnóstico:**
  La regla en `docs/coding-guidelines-backend.md` y `clean-architecture` establece taxativamente:
  $$\text{Database} \longrightarrow \text{EF Entity} \longrightarrow \text{Service Layer} \longrightarrow \text{DTO} \longrightarrow \text{Controller / UI}$$
  Sin embargo, 8 controladores inyectan directamente instancias de `SalesDbContext` y/o `InventoryDbContext`. En lugar de delegar en interfaces de servicio (`IUserService`, `IReceiptService`, `IExchangeRateService`), los controladores ejecutan consultas LINQ, aplican proyecciones arbitrarias, gestionan transacciones directas (`BeginTransactionAsync`) y mutan estados de entidades en memoria.
* **Impacto:**
  * Imposibilidad de testear endpoints de forma aislada sin levantar la base de datos completa.
  * Acoplamiento del protocolo de transporte (HTTP) al motor de persistencia (EF Core / Npgsql).
  * Lógica de negocio fragmentada entre servicios y controladores.
* **Recomendación:**
  Extraer la lógica de acceso a datos a los servicios de dominio correspondientes (`IUserService`, `IReceiptService`, `ISystemSettingsService`), eliminando la inyección de `DbContext` en la capa de controladores API.

---

### 2.2. Evasión del Contrato DTO: Entidades EF Core Expuestas en la API Pública
* **Severidad:** ALTA
* **Archivos Afectados:**
  * [`Backend.API/Controllers/CashDrawerController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/CashDrawerController.cs)
  * [`Core/Interfaces/IInventoryService.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Core/Interfaces/IInventoryService.cs)
  * [`Sales.Module/Services/PaymentMethodService.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Sales.Module/Services/PaymentMethodService.cs)

* **Diagnóstico:**
  * En `CashDrawerController.cs`:
    ```csharp
    [HttpGet("active-session")]
    public async Task<ActionResult<CashDrawerSession?>> GetActiveSession()
    
    [HttpPost("open")]
    public async Task<ActionResult<CashDrawerSession>> OpenSession(...)

    [HttpPost("transaction")]
    public async Task<ActionResult<CashTransaction>> AddTransaction(...)
    ```
    Los endpoints retornan instancias de entidades EF Core (`CashDrawerSession`, `CashTransaction`) serializadas directamente hacia los clientes (Desktop y Web).
  * En `IInventoryService.cs`, los contratos públicos reciben y devuelven la entidad mutable `Product`:
    ```csharp
    Task<Product?> GetProductByIdAsync(int id);
    Task<Product> CreateProductAsync(Product product);
    Task UpdateProductAsync(Product product);
    ```
  * En `PaymentMethodService.cs`, se cachea `IEnumerable<PaymentMethod>` en `IMemoryCache`:
    ```csharp
    _cache.TryGetValue(CacheKeys.ActivePaymentMethods, out IEnumerable<PaymentMethod>? cached);
    ```
    Almacenar entidades EF en `IMemoryCache` permite que cualquier mutación accidental de una propiedad en un consumidor altere la instancia global en memoria para toda la aplicación.
* **Impacto:**
  * Exposición de detalles internos de la base de datos y riesgo de sobre-exposición (Over-Posting / Mass Assignment).
  * Riesgo de referencias circulares durante la serialización JSON.
  * Violación de las directrices `efcore-postgres-concurrency` y `coding-guidelines-backend.md`.
* **Recomendación:**
  Crear y estandarizar `CashDrawerSessionDto`, `CashTransactionDto` y `PaymentMethodDto` inmutables. En `IInventoryService`, operar exclusivamente con DTOs en las capas de frontera.

---

### 2.3. Dependencia Circular y Anti-Patrón Service Locator en `CashDrawerService`
* **Severidad:** CRÍTICA (Integridad Transaccional y Diseño)
* **Archivo Afectado:**
  * [`Sales.Module/Services/CashDrawerService.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Sales.Module/Services/CashDrawerService.cs#L19-L36)

* **Diagnóstico:**
  Existe un ciclo de dependencia Scoped entre `CashDrawerService` y `SalesService`:
  * `SalesService` necesita `ICashDrawerService` para registrar movimientos de caja (vueltos, ingresos).
  * `CashDrawerService` necesita `ISalesService` para generar la venta contable asociada a un adelanto de efectivo (`ProcessCashAdvanceAsync`).
  Para "evitar" la excepción del contenedor de dependencias, se implementó un Service Locator manual con `IServiceProvider?`:
  ```csharp
  private ISalesService? GetSalesService()
  {
      return _serviceProvider?.GetService(typeof(ISalesService)) as ISalesService;
  }
  ```
  Y en la línea 535:
  ```csharp
  var salesService = GetSalesService();
  if (salesService == null)
  {
      AppLogger.LogWarn("[CASH] ProcessCashAdvanceAsync no pudo obtener ISalesService del contenedor DI; no se generó la venta contable.");
  }
  else
  {
      createdSale = await salesService.CreateCashAdvanceSaleAsync(...);
  }
  ```
* **Impacto:**
  Si `_serviceProvider` falla o `salesService` resulta nulo, el sistema **entrega el dinero físico al cliente pero OMITIRÁ SILENCIOSAMENTE la generación de la venta contable**, generando descuadre de inventario, pérdida de trazabilidad fiscal y desbalance financiero.
* **Recomendación:**
  Eliminar el Service Locator. Desacoplar la creación de ventas contables por adelanto utilizando un comando de MediatR (`CreateCashAdvanceSaleCommand`) o extrayendo la orquestación a un servicio coordinador de aplicación `CashAdvanceCoordinator` que dependa limpiamente de ambos sin ciclo.

---

### 2.4. Duplicación Masiva de Lógica de Cierre en Controladores
* **Severidad:** MEDIA
* **Archivos Afectados:**
  * [`Backend.API/Controllers/DailyClosureController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/DailyClosureController.cs)
  * [`Backend.API/Controllers/ShiftsController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ShiftsController.cs)

* **Diagnóstico:**
  `DailyClosureController.CreateClosure` y `ShiftsController.CloseShift` implementan esencialmente la misma rutina transaccional:
  * Validación de duplicados de métodos de pago.
  * Apertura de transacción `Serializable`.
  * Consulta de totales esperados (`GetExpectedTotalsByPaymentMethodAsync`).
  * Mapeo manual de detalles de cierre.
  * Invocación a `_dailyClosureService.CreateClosureAsync`.
  * Invocación a `_cashDrawerService.RolloverSessionAfterClosureAsync`.
  * Generación de comprobantes en disco (`WriteClosedClosureReceipts`).
  Sin embargo, presentan diferencias divergentes: `ShiftsController` realiza conversiones manuales para métodos USD (`expected.ExpectedAmountBsS / exchangeRate`), mientras que `DailyClosureController` confía en montos en Bs.S.
* **Impacto:**
  Cualquier cambio futuro en la regla de negocio de cierre Z exige modificar dos controladores distintos, con alto riesgo de inconsistencia.
* **Recomendación:**
  Consolidar toda la orquestación del cierre en `IDailyClosureService.ExecuteClosureAsync(ClosureRequestDto request)`. Los controladores deben limitarse a recibir la petición, autorizar y delegar.

---

## 3. Flujo de Datos, Concurrencia y Persistencia (EF Core / PostgreSQL)

### 3.1. Brecha Contable de Medianoche en Cierre Z (`Midnight Accounting Gap`)
* **Severidad:** CRÍTICA (Integridad Financiera)
* **Archivo Afectado:**
  * [`Sales.Module/Services/DailyClosureService.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Sales.Module/Services/DailyClosureService.cs#L24-L65)

* **Diagnóstico:**
  En `GetExpectedTotalsByPaymentMethodAsync`:
  ```csharp
  var venDate = Core.Helpers.TimeZoneHelper.GetVenezuelaDate(dateUtc);
  var tz = Core.Helpers.TimeZoneHelper.GetVenezuelaTimeZone();
  var startOfDayLocal = venDate.ToDateTime(TimeOnly.MinValue);
  var startOfDayUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startOfDayLocal, DateTimeKind.Unspecified), tz);
  var endOfDayUtc = startOfDayUtc.AddDays(1);

  var lastClosure = await _context.DailyClosures
      .AsNoTracking()
      .OrderByDescending(dc => dc.ClosureDate)
      .FirstOrDefaultAsync();

  var effectiveStartTime = (lastClosure != null && lastClosure.ClosureDate > startOfDayUtc)
      ? lastClosure.ClosureDate
      : startOfDayUtc;
  ```
  **Escenario Crítico:**
  Si un turno nocturno abre a las 20:00 del día 1, el último cierre fue a las 23:00 del día 1, y el cajero cierra su turno a las 00:15 del día 2:
  * `dateUtc` corresponde al día 2.
  * `startOfDayUtc` corresponde a las 00:00 (hora local de Venezuela) del día 2.
  * La condición `lastClosure.ClosureDate > startOfDayUtc` resulta **FALSA** (porque las 23:00 del día 1 es anterior a las 00:00 del día 2).
  * Por consiguiente, `effectiveStartTime` se fuerza a `startOfDayUtc` (00:00 del día 2).
  * **Consecuencia:** Todas las ventas y pagos efectuados entre las 23:00:00 y las 23:59:59 del día 1 **SON EXCLUIDAS DEL ARQUEO**. Se crea una brecha contable donde el dinero ingresó pero el arqueo esperado lo omite.
  * Además, las cláusulas de consulta usan operadores estrictos:
    `sp.Sale.Date > effectiveStartTime && sp.Sale.Date < endOfDayUtc`
    lo que omite ventas que coincidan exactamente con la estampa de tiempo.
* **Impacto:**
  Descuadre en arqueos de caja para negocios nocturnos o cierres realizados después de medianoche.
* **Recomendación:**
  El arqueo de turno debe basarse en el inicio de la sesión física actual de caja (`CashDrawerSession.OpenedAt`) o en `lastClosure.ClosureDate` real, sin truncar arbitrariamente al límite del día calendario. Usar ventanas cerradas/abiertas consistentes (`>= effectiveStartTime && < endTime`).

---

### 3.2. Omisión de `.AsNoTracking()` en Endpoints de Alta Frecuencia
* **Severidad:** MEDIA (Rendimiento)
* **Archivos Afectados:**
  * [`Backend.API/Controllers/ExchangeRateController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ExchangeRateController.cs#L47-L57)
  * [`Backend.API/Controllers/ShiftsController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ShiftsController.cs#L223-L227)

* **Diagnóstico:**
  * En `ExchangeRateController.cs` (`GetToday()`), el endpoint más consultado por polling y sockets:
    ```csharp
    var record = await _context.ExchangeRateHistory
        .FirstOrDefaultAsync(r => r.Date == today);
    ```
    Cuando hay cache-miss, se ejecuta sin `.AsNoTracking()`, cargando la entidad en el Change Tracker de EF Core.
  * En `ExchangeRateController.GetConfiguredTimeZoneAsync()`:
    ```csharp
    var tzId = await _context.SystemSettings
        .Where(s => s.Key == "SelectedTimeZoneId")
        .Select(s => s.Value)
        .FirstOrDefaultAsync();
    ```
  * En `ShiftsController.cs` (`GetCurrentReport()`):
    ```csharp
    var latestClosure = await _salesContext.DailyClosures
        .Include(c => c.Details)
        .OrderByDescending(c => c.Id)
        .FirstOrDefaultAsync();
    ```
* **Impacto:**
  Sobrecarga de memoria en el Change Tracker, asignaciones innecesarias de memoria en el heap y penalización en concurrencia.
* **Recomendación:**
  Aplicar `.AsNoTracking()` a todas las consultas de solo lectura en cumplimiento de la directriz `docs/coding-guidelines-backend.md`.

---

### 3.3. Paginación en Cliente en Movimientos de Caja (Ineficiencia de Red)
* **Severidad:** MEDIA (Rendimiento y Red)
* **Archivos Afectados:**
  * [`Backend.API/Controllers/CashDrawerController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/CashDrawerController.cs#L81)
  * [`Web.Frontend/src/pages/RegisterPage.jsx`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Web.Frontend/src/pages/RegisterPage.jsx#L52-L72)

* **Diagnóstico:**
  `CashDrawerController.GetHistory` descarga hasta 300 transacciones en bloque:
  ```csharp
  [HttpGet("history")]
  public async Task<ActionResult<IEnumerable<CashTransactionDto>>> GetHistory([FromQuery] int limit = 300)
  ```
  Y en el cliente web (`RegisterPage.jsx`), se reciben las 300 transacciones para luego realizar paginación en memoria del navegador de 25 en 25:
  ```javascript
  const ITEMS_PER_PAGE = 25;
  ```
* **Impacto:**
  Transferencia innecesaria de payload JSON sobre la red local/Wi-Fi en cada refresco de la pantalla de caja, consumiendo CPU en el cliente móvil y memoria en el navegador.
* **Recomendación:**
  Implementar el contrato de paginación estándar del sistema (`page`, `pageSize`, `PagedResultDto`) con `.Skip((page - 1) * pageSize).Take(pageSize)` y cabecera `X-Total-Count` en el backend.

---

### 3.4. Bucle N+1 Potencial en `PopulateItemsMetadataAsync`
* **Severidad:** BAJA / MEDIA (Rendimiento)
* **Archivo Afectado:**
  * [`Sales.Module/Services/SalesService.Checkout.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Sales.Module/Services/SalesService.Checkout.cs#L434-L446)

* **Diagnóstico:**
  ```csharp
  var fetched = await _inventoryService.GetProductsByIdsAsync(productIds);
  if (fetched != null && fetched.Count > 0)
  {
      productsDict = fetched.ToDictionary(p => p.Id);
  }
  else
  {
      foreach (var id in productIds)
      {
          var p = await _inventoryService.GetProductByIdAsync(id);
          if (p != null) productsDict[p.Id] = p;
      }
  }
  ```
  Si la consulta por lote retorna vacía o falla, el bloque fallback dispara una consulta individual por cada producto de la venta.
* **Impacto:**
  Latencia multiplicada en ventas con numerosos ítems.
* **Recomendación:**
  Eliminar el bucle N+1 en el bloque `else`. Si la consulta por lote no arroja resultados para ciertos IDs, registrar advertencia o consultar únicamente los IDs faltantes en una sola sentencia SQL con operador `IN`.

---

## 4. Integridad Financiera y Cálculos Multimoneda

### 4.1. Conversión de Divisas sin Redondeo Formal en `ShiftsController`
* **Severidad:** MEDIA (Integridad Financiera)
* **Archivo Afectado:**
  * [`Backend.API/Controllers/ShiftsController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ShiftsController.cs#L115-L118)

* **Diagnóstico:**
  En la evaluación de métodos de pago en USD:
  ```csharp
  if (declared.Currency == "USD")
  {
      expectedSystemAmount = exchangeRate > 0 ? (expected.ExpectedAmountBsS / exchangeRate) : 0m;
  }
  ```
  Se realiza una división directa de punto decimal sin utilizar la función estandarizada `PricingHelper.ToUSD(amountBsS, exchangeRate)` ni especificar el redondeo bancario a 4 decimales (`MidpointRounding.AwayFromZero`).
  Posteriormente se compara:
  ```csharp
  decimal diff = declared.Amount - expectedSystemAmount;
  string status = Math.Abs(diff) < 0.05m ? "Balanced" : (diff > 0 ? "Surplus" : "Shortage");
  ```
  Utilizando una constante mágica hardcodeada (`0.05m`).
* **Impacto:**
  Deriva en decimales flotantes residuales que pueden marcar un turno como desbalanceado ("Surplus" o "Shortage") por fracciones mínimas no redondeadas.
* **Recomendación:**
  Usar `PricingHelper.ToUSD(expected.ExpectedAmountBsS, exchangeRate)` y centralizar el umbral de tolerancia de redondeo en una constante de configuración (`FinancialConstants.RoundingToleranceUsd`).

---

### 4.2. Conversión Implícita de `decimal` a `double` en DataAnnotations
* **Severidad:** BAJA / MEDIA (Precisión)
* **Archivos Afectados:**
  * [`Desktop.Client.Core/ViewModels/ProductDialogViewModel.Pricing.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/ProductDialogViewModel.Pricing.cs#L11-L43)
  * [`Desktop.Client.Core/ViewModels/AddProductViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/AddProductViewModel.cs#L30-L53)

* **Diagnóstico:**
  Se aplican atributos de validación `[Range(0, double.MaxValue)]` sobre propiedades de tipo `decimal`:
  ```csharp
  [ObservableProperty]
  [Range(0, double.MaxValue, ErrorMessage = "Price must be non-negative")]
  private decimal _price;
  ```
  `RangeAttribute(double, double)` convierte internamente el valor `decimal` de 128 bits a un `double` IEEE 754 de 64 bits.
* **Impacto:**
  Pérdida de precisión en validación y violación técnica de la regla en `docs/coding-guidelines-core.md` que prohíbe el uso de `float` o `double` para valores monetarios.
* **Recomendación:**
  Reemplazar por la sobrecarga tipada para decimales de DataAnnotations:
  `[Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "...")]`.

---

## 5. Seguridad, Control de Acceso (RBAC) y Red (Zero-Trust)

### 5.1. Cookie `pos_jwt` con `Secure = true` Incondicional sobre HTTP en LAN
* **Severidad:** ALTA (Operatividad y Seguridad)
* **Archivo Afectado:**
  * [`Backend.API/Controllers/AuthController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/AuthController.cs#L162-L175)

* **Diagnóstico:**
  En el flujo de login para el cliente Web:
  ```csharp
  if (isWeb && Response?.Cookies != null)
  {
      var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
      {
          HttpOnly = true,
          Secure = true,
          SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
          Path = "/",
          Expires = DateTimeOffset.UtcNow.AddMinutes(_tokenService.ExpiryMinutes)
      };
      Response.Cookies.Append("pos_jwt", token, cookieOptions);
  }
  ```
  Y en la línea 188:
  ```csharp
  return Ok(new LoginResultDto
  {
      User = dto,
      Token = isWeb ? null : token
  });
  ```
  **Vulnerabilidad Operativa:**
  Según el estándar RFC 6265bis, los navegadores (Chrome, Safari, Firefox) **rechazan y descartan silenciosamente cualquier cookie marcada con `Secure` recibida a través de HTTP sin TLS**, excepto en orígenes loopback (`localhost`, `127.0.0.1`).
  Si un cajero utiliza una tablet conectada a la red Wi-Fi del establecimiento (`http://192.168.1.100:5000`):
  1. El navegador rechaza la cookie `pos_jwt` por venir sobre HTTP en una IP no loopback.
  2. Como `Token` en el cuerpo JSON es `null`, el cliente web no tiene forma de almacenar el token.
  3. En la siguiente llamada a la API, la petición viaja sin credenciales y el usuario queda bloqueado con error 401.
* **Recomendación:**
  * Establecer la directiva de cookie dinámica según la conexión: `Secure = Request.IsHttps`.
  * O bien, exigir HTTPS de manera estricta en el servidor para todas las interfaces LAN mediante redirección HTTP $\rightarrow$ HTTPS.

---

### 5.2. Omisión de `CancellationToken` en Interfaces Públicas de Servicios
* **Severidad:** MEDIA (Resiliencia y Denegación de Servicio)
* **Archivos Afectados:**
  * [`Core/Interfaces/IInventoryService.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Core/Interfaces/IInventoryService.cs)
  * [`Sales.Module/Interfaces/ISalesService.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Sales.Module/Interfaces/ISalesService.cs)
  * [`Backend.API/Controllers/UsersController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/UsersController.cs)
  * [`Backend.API/Controllers/ExchangeRateController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ExchangeRateController.cs)

* **Diagnóstico:**
  Más del 70% de los métodos asíncronos en `ISalesService` e `IInventoryService` no aceptan un parámetro `CancellationToken`.
  Ejemplos:
  * `ISalesService.GetSalesHistoryAsync(int page, int pageSize, ...)`
  * `ISalesService.RecalculateOnHoldSalesAsync(decimal newExchangeRate)`
  * `IInventoryService.GetProductsByIdsAsync(IEnumerable<int> productIds)`
  * `IInventoryService.UpdateStockBatchAsync(...)`
  * `UsersController.GetUsers()`
* **Impacto:**
  Si un cliente cancela una solicitud de red (por ejemplo, el usuario cambia de página o cierra la pestaña mientras se consulta un historial extenso), el servidor continúa procesando la consulta en PostgreSQL, bloqueando conexiones del pool y consumiendo CPU inútilmente.
* **Recomendación:**
  Propagar `CancellationToken cancellationToken = default` en todos los métodos asíncronos en cumplimiento de la directriz `clean-architecture`.

---

### 5.3. Exposición de Mensajes de Excepción Internos en `GlobalExceptionHandlerMiddleware`
* **Severidad:** MEDIA (Fuga de Información)
* **Archivo Afectado:**
  * [`Backend.API/Middleware/GlobalExceptionHandlerMiddleware.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Middleware/GlobalExceptionHandlerMiddleware.cs#L263-L278)

* **Diagnóstico:**
  ```csharp
  if (exception is InvalidOperationException)
  {
      bool isDomain = IsDomainException(exception);
      string msg = isDomain ? exception.Message : "Operación inválida debido al estado actual del sistema.";
      await WriteProblemDetailsAsync(..., msg, msg, ...);
  }
  ```
  Y en `IsDomainException`:
  ```csharp
  private static bool IsDomainException(Exception ex)
  {
      var source = ex.TargetSite?.DeclaringType?.Assembly.GetName().Name ?? "";
      return source.StartsWith("Sales") || source.StartsWith("Inventory") || ...;
  }
  ```
  * `ex.TargetSite` es una propiedad de reflexión costosa y desaconsejada en .NET Core / .NET 10, pues puede arrojar `NotSupportedException` en tiempo de ejecución ante métodos dinámicos o compilación AOT.
  * Si un servicio de dominio lanza `InvalidOperationException` con detalles de infraestructura (rutas locales, fragmentos SQL o nombres de campos internos), `ex.Message` se expone íntegro en la respuesta HTTP al cliente.
* **Recomendación:**
  Crear una clase base `DomainException` tipada y controlada. Solo las excepciones que hereden explícitamente de `DomainException` deben exponer su mensaje sanitario al cliente; cualquier `InvalidOperationException` estándar debe recibir un mensaje genérico RFC 7807.

---

### 5.4. Endpoints Mutantes sin Validación Inmediata de Security Stamp
* **Severidad:** MEDIA
* **Archivos Afectados:**
  * [`Backend.API/Controllers/UsersController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/UsersController.cs)
  * [`Backend.API/Controllers/ProductsController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ProductsController.cs)
  * [`Backend.API/Controllers/ExchangeRateController.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Backend.API/Controllers/ExchangeRateController.cs)

* **Diagnóstico:**
  `SecurityStampValidationMiddleware` emplea una ventana deslizante de micro-caché de 10 segundos para no saturar la base de datos, salvo que el endpoint esté decorado con `[RequireSecurityStampValidation]`.
  Endpoints de alto impacto administrativo como la creación/edición de usuarios (`UsersController.CreateUser`, `UpdateUser`), desactivación de productos (`ProductsController.Delete`) o fijación manual de tasa (`ExchangeRateController.UpsertRate`) **no tienen el atributo `[RequireSecurityStampValidation]`**.
* **Impacto:**
  Un usuario cuyo rol fue revocado o cuya contraseña fue modificada dispone de una ventana de hasta 10 segundos para realizar modificaciones administrativas en el catálogo o usuarios.
* **Recomendación:**
  Agregar el atributo `[RequireSecurityStampValidation]` a todos los endpoints mutantes (`POST`, `PUT`, `DELETE`) de administración.

---

## 6. Gestión de Recursos y Ciclo de Vida (Fugas de Memoria en Clientes)

### 6.1. ViewModels de WPF sin Implementar `IDisposable` (Fugas de Memoria)
* **Severidad:** ALTA (Estabilidad Desktop)
* **Archivos Afectados:**
  * [`Desktop.Client.Core/ViewModels/CashDrawerViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/CashDrawerViewModel.cs#L143-L157)
  * [`Desktop.Client.Core/ViewModels/PendingOrdersViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/PendingOrdersViewModel.cs#L88-L97)
  * [`Desktop.Client.Core/ViewModels/ExchangeRateViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/ExchangeRateViewModel.cs#L98)
  * [`Desktop.Client.Core/ViewModels/CustomerManagementViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/CustomerManagementViewModel.cs#L21)
  * [`Desktop.Client.Core/ViewModels/CustomerPickerViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/CustomerPickerViewModel.cs#L18)

* **Diagnóstico:**
  La directriz `docs/coding-guidelines-wpf.md` estipula:
  > `IDisposable`: Cualquier ViewModel con timers, `CancellationTokenSource` o `WeakReferenceMessenger` debe implementar `IDisposable`.
  * `CashDrawerViewModel` se registra a 3 mensajes en `WeakReferenceMessenger`:
    `TimeZoneChangedMessage`, `CurrencyRateChangedMessage` y `ShiftClosedMessage`, pero **no implementa `IDisposable` ni invoca `UnregisterAll`**.
  * `PendingOrdersViewModel` y `ExchangeRateViewModel` se registran a mensajes de actualización de tasas y ventas en espera sin implementar `IDisposable`.
  * `CustomerManagementViewModel` y `CustomerPickerViewModel` crean instancias de `CancellationTokenSource` para el debounce de búsqueda (`_searchCts`) pero no implementan `IDisposable`.
* **Impacto:**
  Fugas graduales de manejadores de eventos y retención de tokens en el Garbage Collector.
* **Recomendación:**
  Hacer que todos estos ViewModels implementen `IDisposable` (o hereden de `BaseViewModel`) y liberen sus suscripciones y tokens en el método `Dispose()`.

---

### 6.2. Uso de `async void` en `MainWindow.xaml.cs`
* **Severidad:** MEDIA (Estabilidad)
* **Archivo Afectado:**
  * [`Desktop.Client/MainWindow.xaml.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client/MainWindow.xaml.cs#L21-L94)

* **Diagnóstico:**
  ```csharp
  protected override async void OnClosing(CancelEventArgs e)
  ```
  En WPF, sobrescribir `OnClosing` con `async void` es un anti-patrón reconocido. Aunque el código cancela temporalmente el evento (`e.Cancel = true`) para ejecutar `await app.StopServicesAsync()` y luego llama a `Close()`, cualquier excepción no capturada durante `StopServicesAsync` derriba el proceso sin posibilidad de recuperación a través de `AppDomain.UnhandledException`.
* **Recomendación:**
  Encapsular la parada asíncrona dentro de una rutina síncrona controlada o ejecutarla en el evento `App.OnExit` donde el ciclo de vida del proceso lo permite de forma segura.

---

### 6.3. Servicio de Logística en Memoria sin Persistencia (`Logistics.Module`)
* **Severidad:** ALTA (Pérdida de Datos)
* **Archivo Afectado:**
  * [`Logistics.Module/Services/DeliveryService.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Logistics.Module/Services/DeliveryService.cs#L16-L30)

* **Diagnóstico:**
  `DeliveryService` almacena las órdenes de despacho en un `ConcurrentDictionary<int, DeliveryOrderDto> _deliveries`:
  ```csharp
  // [NO PRODUCTIVO / EXPERIMENTAL] Servicio de gestión de entregas en memoria.
  // Opera exclusivamente con ConcurrentDictionary sin persistencia en base de datos PostgreSQL ([8L-CR3]).
  ```
  Además, en la línea 89:
  ```csharp
  order.DriverId = driverId;
  order.Status = OrderStatus.OutForDelivery;
  ```
  Se mutan directamente las propiedades del DTO compartido por referencia en memoria, lo que genera condiciones de carrera (Race Conditions) si varios despachadores o conductores actualizan estados concurrentemente.
* **Impacto:**
  Cualquier reinicio del servidor, actualización automática por `UpdaterService` o corte de energía destruye el 100% de los envíos pendientes, estados y asignaciones de conductores.
* **Recomendación:**
  Modelar las entidades `DeliveryOrder` y `DeliveryRoute` en PostgreSQL dentro de `SalesDbContext` o un `LogisticsDbContext` con migraciones EF Core formales.

---

## 7. Calidad de Código y Anti-God Objects (> 500 Líneas)

El estándar del proyecto en `docs/coding-guidelines-core.md` fija como límite objetivo **300 a 500 líneas por archivo**. Se identificaron los siguientes componentes que sobrepasan dicho umbral:

| Archivo | Líneas | Dominio / Rol | Problema Detectado |
|---|---|---|---|
| [`Web.Frontend/src/pages/HistoryPage.jsx`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Web.Frontend/src/pages/HistoryPage.jsx) | **780** | Web Frontend | Monolito: acumula lógica de fechas, debounce, flyout de filtros secundarios, dropdown con captura de teclado, tabla responsiva, acordeón de ítems, métodos de pago y modal de ticket. |
| [`Web.Frontend/src/components/pos/BarcodeScannerModal.jsx`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Web.Frontend/src/components/pos/BarcodeScannerModal.jsx) | **710** | Web Frontend | Acumula 24 hooks (`useState`/`useRef`), manipulación directa de stream WebRTC, algoritmos Sauvola de visión artificial, throttle de frames y controles de interfaz. |
| [`Desktop.Client.Core/ViewModels/SalesHistoryViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/SalesHistoryViewModel.cs) | **664** | Desktop WPF | Acumula lógica de paginación, filtros de fecha, detalle de ítems, impresión de comprobantes térmicos y exportación PDF con propiedades manuales sin generadores source. |
| [`Sales.Module/Services/CashDrawerService.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Sales.Module/Services/CashDrawerService.cs) | **617** | Backend Sales | Acumula apertura de caja, cierre, cálculo de saldo en tiempo real, arqueo, rollover de sesión, comisiones y adelantos de efectivo con Service Locator. |
| [`Web.Frontend/src/pages/RegisterPage.jsx`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Web.Frontend/src/pages/RegisterPage.jsx) | **591** | Web Frontend | Monolito de arqueo que maneja modales de entrada/salida de efectivo, estados de sesión y paginación en memoria de 300 transacciones. |
| [`Web.Frontend/src/context/CartContext.jsx`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Web.Frontend/src/context/CartContext.jsx) | **572** | Web Frontend | Maneja sincronización con backend, caché en `sessionStorage`, detección de pestañas concurrentes con `BroadcastChannel` y recuperación ante fallos. |
| [`Web.Frontend/src/pages/RegisterClosePage.jsx`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Web.Frontend/src/pages/RegisterClosePage.jsx) | **564** | Web Frontend | Acumula desglose de métodos de pago, teclado numérico ATM, confirmación en cero, reporte Z post-cierre y emisión de comprobantes. |
| [`Web.Frontend/src/pages/CatalogPage.jsx`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Web.Frontend/src/pages/CatalogPage.jsx) | **521** | Web Frontend | Monolito de catálogo con búsqueda, filtros de estado, ordenamiento, modales de producto y operaciones CRUD. |
| [`Desktop.Client.Core/ViewModels/ImportProductsViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/ImportProductsViewModel.cs) | **508** | Desktop WPF | Acumula parseo de Excel/CSV, mapeo de columnas, validación reactiva y progreso de importación. |
| [`Desktop.Client.Core/ViewModels/SettingsViewModel.cs`](file:///c:/Users/Lenovo%20IdeaPad%203/Desktop/Proyecto_POS_Estable/V0.1/Desktop.Client.Core/ViewModels/SettingsViewModel.cs) | **508** | Desktop WPF | Acumula configuración de impresoras, respaldos, red, temas y reinicio de servicios. |

* **Recomendación de Modularización:**
  * En Frontend: Extraer `HistoryTable`, `HistoryFiltersFlyout` y `HistoryDetailAccordion` fuera de `HistoryPage.jsx`.
  * Extraer el motor de escaneo de cámara a un Custom Hook `useBarcodeScanner` independiente de `BarcodeScannerModal.jsx`.
  * En Desktop: Dividir `SalesHistoryViewModel.cs` en clases parciales cohesivas (`SalesHistoryViewModel.Export.cs`, `SalesHistoryViewModel.Paging.cs`).
  * En Backend: Extraer el flujo de adelanto de efectivo de `CashDrawerService.cs` a un servicio dedicado `CashAdvanceService.cs`.

---

## 8. Matriz de Priorización y Plan de Remediación

| ID | Hallazgo | Severidad | Esfuerzo | Impacto | Fase Sugerida |
|---|---|---|---|---|---|
| **H-01** | Brecha contable de medianoche en Cierre Z (`DailyClosureService`) | **CRÍTICA** | Medio | Alto | Fase Inmediata |
| **H-02** | Dependencia circular y Service Locator precario en `CashDrawerService` | **CRÍTICA** | Medio | Alto | Fase Inmediata |
| **H-03** | Fuga de `DbContext` a 8 controladores de API (Arquitectura) | **ALTA** | Alto | Alto | Fase 1 |
| **H-04** | Evasión de DTOs en `CashDrawerController`, `IInventoryService` y `PaymentMethodService` | **ALTA** | Medio | Medio | Fase 1 |
| **H-05** | Cookie `pos_jwt` con `Secure=true` incondicional que bloquea tablets LAN sobre HTTP | **ALTA** | Bajo | Alto | Fase 1 |
| **H-06** | ViewModels WPF sin `IDisposable` y fugas de `WeakReferenceMessenger`/`CTS` | **ALTA** | Medio | Medio | Fase 2 |
| **H-07** | Almacenamiento en memoria no persistente en `Logistics.Module` | **ALTA** | Alto | Medio | Fase 3 |
| **H-08** | Paginación ineficiente y carga masiva de transacciones en cliente (`RegisterPage`) | **MEDIA** | Medio | Medio | Fase 2 |
| **H-09** | Omisión masiva de `CancellationToken` en interfaces públicas | **MEDIA** | Medio | Medio | Fase 2 |
| **H-10** | Duplicación de lógica de cierre en `DailyClosureController` y `ShiftsController` | **MEDIA** | Medio | Medio | Fase 2 |
| **H-11** | Exposición de `ex.Message` y uso de `TargetSite` en `GlobalExceptionHandler` | **MEDIA** | Bajo | Medio | Fase 1 |
| **H-12** | Modularización de componentes monolíticos > 500 líneas (HistoryPage, BarcodeScanner) | **MEDIA** | Alto | Medio | Fase 3 |
| **H-13** | Validación de `double.MaxValue` en DataAnnotations sobre `decimal` | **BAJA** | Bajo | Bajo | Fase 3 |
| **H-14** | `async void OnClosing` en `MainWindow.xaml.cs` | **MEDIA** | Bajo | Bajo | Fase 2 |

---

## 9. Conclusión

El sistema demuestra un alto nivel de madurez técnica en sus políticas de redondeo BCV y robustez en la suite de pruebas. Las oportunidades de mejora detectadas no corresponden a fallos superficiales de sintaxis, sino a **refactorizaciones estructurales indispensables para garantizar que la solución pueda escalar hacia múltiples cajas concurrentes, operar de forma ininterrumpida a través de medianoche y soportar terminales móviles y tabletas sin fricciones de red ni fugas de memoria**.
