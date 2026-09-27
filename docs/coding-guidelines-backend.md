# Coding Guidelines — Backend (.NET 10 / C# / EF Core / PostgreSQL)

> Sección de backend. Aplica a cambios en `Backend.API`, `Core`, `Sales.Module`, `Inventory.Module`.

---

## 2.3. Persistencia de Datos con EF Core y PostgreSQL

* **Consultas de Solo Lectura:** Incluir `.AsNoTracking()` explícitamente.
  ```csharp
  return await _context.CashTransactions
      .AsNoTracking()
      .Include(t => t.Sale)
      .OrderByDescending(t => t.CreatedAt)
      .Take(limit)
      .Select(t => t.ToDto())
      .ToListAsync(ct);
  ```
* **Prevención de Explosión Cartesiana:** Usar `.AsSplitQuery()` en múltiples colecciones dependientes.
* **Concurrencia Optimista:** Mapear tokens de concurrencia mediante columna oculta `xmin` de PostgreSQL.
* **Transacciones Coordinadas:** Enrolar transacción ADO.NET vía `rawDbTx.UseTransaction()` (no transacciones distribuidas).
* **Prohibición de DDL Manual:** Migraciones de EF Core son la única fuente de verdad.

## 2.5. Modelo de Excepciones y Respuestas HTTP (RFC 7807)

* **Prohibición de Capturas Genéricas:** No filtrar `ex.Message` al cliente.
* **Centralización en `GlobalExceptionHandlerMiddleware`:**
  * `KeyNotFoundException` → **404 Not Found**
  * `ArgumentException` / `ValidationException` → **400 Bad Request**
  * `InvalidOperationException` / `DbUpdateConcurrencyException` → **409 Conflict**
  * PostgreSQL `23505` → **409 Conflict** (clave única)
  * PostgreSQL `23502` → **400 Bad Request** (NOT NULL)
  * Excepciones inesperadas → **500** sanitizado con `traceId`
