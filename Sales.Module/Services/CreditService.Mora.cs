using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// H1 (T6, S3/S4): barrido diario de cartera. Materializa el estado Vencida de las obligaciones
/// abiertas que superaron vencimiento + gracia y aplica la suspensión/reactivación automática por
/// morosidad (rehabilitación al regularizar), sin tocar los bloqueos manuales. Corre en UNA
/// transacción bajo advisory lock de cartera (clase 4) y es idempotente: repetir la corrida no
/// genera cambios ni efectos monetarios.
/// </summary>
public partial class CreditService
{
    // Esquema de advisory locks del POS: 2 = venta, 3 = cartera por cliente. El barrido global usa
    // la clase 4 con clave fija para serializarse contra sí mismo (multi-instancia) sin bloquear
    // los abonos ni los cierres por cliente.
    private const int PortfolioAdvisoryLockClass = 4;
    private const int PortfolioAdvisoryLockKey = 0;

    /// <inheritdoc />
    public async Task<CreditMoraEvaluationResultDto> EvaluateOverdueAndMoraAsync(CancellationToken cancellationToken = default)
    {
        int retryAttempt = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reintento transitorio: el tracker se limpia para re-leer el estado fresco (mismo
            // contrato del cierre de venta a crédito y del registro de abonos).
            if (retryAttempt++ > 0 || _context.Database.IsRelational())
            {
                _context.ChangeTracker.Clear();
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            try
            {
                // Advisory lock de cartera: serializa el barrido contra otra instancia del backend.
                // Provider-safe: en proveedores no-Npgsql (tests InMemory/SQLite) se omite.
                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_xact_lock({0}, {1})",
                        new object[] { PortfolioAdvisoryLockClass, PortfolioAdvisoryLockKey },
                        cancellationToken);
                }

                var venezuelaToday = TimeZoneHelper.GetVenezuelaDate();
                var result = new CreditMoraEvaluationResultDto();

                // (a) Materialización de Vencida: solo obligaciones abiertas (Vigente/Parcial) con
                // saldo por encima del polvo decimal y vencidas más allá de la gracia del cliente.
                // Las ya materializadas (Vencida), las Pagada/Anulada y el polvo quedan intactas,
                // lo que hace idempotente a la corrida. Entidades rastreadas: xmin vigente.
                var openReceivables = await _context.Receivables
                    .Include(receivable => receivable.Customer)
                    .Where(receivable => (receivable.Status == ReceivableStatus.Vigente
                                       || receivable.Status == ReceivableStatus.Parcial
                                       || receivable.Status == ReceivableStatus.Vencida)
                                      && receivable.BalanceUSD > BalanceToleranceUsd)
                    .ToListAsync(cancellationToken);

                // Clientes con al menos una obligación abierta vencida-beyond-grace: la mora se
                // resuelve con la regla compartida (vencimiento + gracia vs. día Venezuela), nunca
                // con el estado persistido como sustituto.
                var customersWithOverdue = new HashSet<int>();
                foreach (var receivable in openReceivables)
                {
                    int graceDays = receivable.Customer?.CreditGraceDays ?? 0;
                    if (!IsOverdueBeyondGrace(receivable.DueDateUtc, graceDays, venezuelaToday))
                    {
                        continue;
                    }

                    customersWithOverdue.Add(receivable.CustomerId);
                    if (receivable.Status != ReceivableStatus.Vencida)
                    {
                        receivable.Status = ReceivableStatus.Vencida;
                        result.OverdueMaterializedCount++;
                    }
                }

                // (b) Suspensión / reactivación: solo clientes con línea de crédito habilitada.
                // ManuallyBlocked nunca se toca (bloqueo administrativo con historial); el estado
                // SuspendedByMora es administrado por el sistema y se rehabilita automáticamente
                // al desaparecer las vencidas abiertas (regularización).
                var creditCustomers = await _context.Customers
                    .Where(customer => customer.IsCreditEnabled)
                    .ToListAsync(cancellationToken);

                result.EvaluatedCustomersCount = creditCustomers.Count;

                foreach (var customer in creditCustomers)
                {
                    bool hasOverdue = customersWithOverdue.Contains(customer.Id);

                    if (hasOverdue && customer.CreditStatus == CreditStatus.Active)
                    {
                        customer.CreditStatus = CreditStatus.SuspendedByMora;
                        customer.UpdatedAt = DateTime.UtcNow;
                        result.SuspendedCount++;
                    }
                    else if (!hasOverdue && customer.CreditStatus == CreditStatus.SuspendedByMora)
                    {
                        customer.CreditStatus = CreditStatus.Active;
                        customer.UpdatedAt = DateTime.UtcNow;
                        result.ReactivatedCount++;
                    }
                }

                bool changed = result.OverdueMaterializedCount > 0
                    || result.SuspendedCount > 0
                    || result.ReactivatedCount > 0;

                if (changed)
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return result;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Conflicto de concurrencia (xmin): otra edición ganó la carrera (p. ej. un abono
                // o un cierre simultáneo). Se descarta el barrido completo (rollback implícito en
                // el dispose de la transacción) y el próximo ciclo reintenta, sin tumbarse. Nunca
                // hay cambios parciales: existe un único SaveChanges al final de la corrida.
                _logger?.LogWarning(ex, "[CREDIT_MORA] Conflicto de concurrencia durante el barrido de cartera; el próximo ciclo reintentará.");
                return new CreditMoraEvaluationResultDto();
            }
        });
    }
}
