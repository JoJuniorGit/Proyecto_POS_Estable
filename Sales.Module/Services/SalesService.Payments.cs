using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sales.Module.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{


    public async Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null)
    {
        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");

        var input = await ComputePaymentInputsAsync(sale, request, sale.Payments.Sum(p => p.Amount), "AddPaymentToHoldSale");

        // 8.9-B4: envolver en execution strategy (reintento completo ante fallos transitorios).
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
        IDbContextTransaction? dbTransaction = null;
        if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            dbTransaction = await _context.Database.BeginTransactionAsync();
        }

        try
        {
            var paymentEntity = new SalePayment
            {
                SaleId = sale.Id,
                PaymentMethodId = request.PaymentMethodId,
                Amount = Math.Round(input.AmountUsd, 2, MidpointRounding.AwayFromZero),
                AmountBsS = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                ExchangeRate = input.Rate,
                ReferenceNumber = request.ReferenceNumber,
                CreatedAt = DateTime.UtcNow
            };

            _context.SalePayments.Add(paymentEntity);

            // Si es efectivo y monto positivo, registrar en sesión activa de caja
            if (input.Method != null && input.Method.IsCash && input.AmountUsd > 0 && _cashDrawerService != null)
            {
                var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(input.Rate);
                var cashTx = new CashTransaction
                {
                    SessionId = activeSession.Id,
                    Type = CashTransactionType.Income,
                    Source = CashTransactionSource.SalePayment,
                    AmountUsd = input.AmountUsd,
                    ExchangeRate = input.Rate,
                    AmountLocal = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                    IsPhysicalCash = true,
                    Description = $"Abono Venta #{saleId}",
                    TransactionTime = DateTime.UtcNow,
                    SaleId = sale.Id,
                    PaymentMethodId = request.PaymentMethodId
                };
                _context.CashTransactions.Add(cashTx);
            }

            RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/payments", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)));

            await _context.SaveChangesAsync();

            if (dbTransaction != null)
            {
                await dbTransaction.CommitAsync();
            }

            return MapToDto(sale);
        }
        catch (Exception ex)
        {
            if (dbTransaction != null)
            {
                await dbTransaction.RollbackAsync();
            }
            _logger?.LogError(ex, "[SalesService] Error al registrar abono en venta #{SaleId}. Transacción revertida.", saleId);
            throw;
        }
        finally
        {
            dbTransaction?.Dispose();
        }
        });
    }

    // 8.29-A05: abonos atómicos por lote. Todas las validaciones ocurren ANTES de abrir la
    // transacción; si alguna falla, se lanza sin persistir NADA. La persistencia de todos los
    // abonos del lote comparte UNA sola transacción (rollback conjunto) y UNA sola
    // SaveChanges. Idempotency: un único Idempotency-Key por lote.
    public async Task<SaleDto> AddPaymentsBatchToHoldSaleAsync(int saleId, List<AddPaymentRequestDto> payments, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null)
    {
        if (payments == null || payments.Count == 0)
            throw new ArgumentException("Debe enviar al menos un abono.");

        if (payments.Count > 50)
            throw new ArgumentException("El lote de abonos supera el máximo permitido (50).");

        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");

        var computedInputs = new List<(AddPaymentRequestDto Request, ComputedPaymentInput Input)>(payments.Count);
        decimal runningPaidUsd = sale.Payments.Sum(p => p.Amount);
        foreach (var request in payments)
        {
            var input = await ComputePaymentInputsAsync(sale, request, runningPaidUsd, "AddPaymentsBatchToHoldSale");
            runningPaidUsd += input.AmountUsd;
            computedInputs.Add((request, input));
        }

        // 8.9-B4: execution strategy para reintentos completos del lote ante fallos transitorios.
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            IDbContextTransaction? dbTransaction = null;
            if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
            {
                dbTransaction = await _context.Database.BeginTransactionAsync();
            }

            try
            {
                foreach (var (request, input) in computedInputs)
                {
                    var paymentEntity = new SalePayment
                    {
                        SaleId = sale.Id,
                        PaymentMethodId = request.PaymentMethodId,
                        Amount = Math.Round(input.AmountUsd, 2, MidpointRounding.AwayFromZero),
                        AmountBsS = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                        ExchangeRate = input.Rate,
                        ReferenceNumber = request.ReferenceNumber,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.SalePayments.Add(paymentEntity);

                    if (input.Method != null && input.Method.IsCash && input.AmountUsd > 0 && _cashDrawerService != null)
                    {
                        var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(input.Rate);
                        var cashTx = new CashTransaction
                        {
                            SessionId = activeSession.Id,
                            Type = CashTransactionType.Income,
                            Source = CashTransactionSource.SalePayment,
                            AmountUsd = input.AmountUsd,
                            ExchangeRate = input.Rate,
                            AmountLocal = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                            IsPhysicalCash = true,
                            Description = $"Abono Venta #{saleId}",
                            TransactionTime = DateTime.UtcNow,
                            SaleId = sale.Id,
                            PaymentMethodId = request.PaymentMethodId
                        };
                        _context.CashTransactions.Add(cashTx);
                    }
                }

                RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/payments/batch", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)));

                await _context.SaveChangesAsync();

                if (dbTransaction != null)
                {
                    await dbTransaction.CommitAsync();
                }

                return MapToDto(sale);
            }
            catch (Exception ex)
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.RollbackAsync();
                }
                _logger?.LogError(ex, "[SalesService] Error al registrar abonos por lote en venta #{SaleId}. Transacción revertida.", saleId);
                throw;
            }
            finally
            {
                dbTransaction?.Dispose();
            }
        });
    }

    // Cálculo y validación compartidos de un abono individual (tasa anclada BCV, montos,
    // límite acumulado y regla de efectivo a montos enteros). Se usa tanto en el flujo de
    // abono simple como en el batch; `runningPaidUsd` es lo ya abonado + abonos del lote.
    private async Task<ComputedPaymentInput> ComputePaymentInputsAsync(Sale sale, AddPaymentRequestDto request, decimal runningPaidUsd, string contextLabel)
    {
        // 8.6-B3/8.5-A5: Tasa del abono anclada a la BCV del día cuando el cliente la envía.
        decimal rate = request.ExchangeRate > 0
            ? await ResolveAnchoredRateAsync(request.ExchangeRate, contextLabel: contextLabel, referenceId: sale.Id)
            : sale.AppliedRate;
        decimal amountUsd = request.AmountUSD > 0
            ? Math.Round(request.AmountUSD, 2, MidpointRounding.AwayFromZero)
            : (rate > 0 ? Math.Round(request.AmountBsS / rate, 2, MidpointRounding.AwayFromZero) : 0m);

        if (amountUsd <= 0 && request.AmountBsS <= 0)
        {
            throw new ArgumentException("El monto del abono debe ser mayor a cero.");
        }

        if (runningPaidUsd + amountUsd > sale.TotalUSD + 0.05m)
        {
            throw new ArgumentException("El monto del abono excede el total pendiente de la venta.");
        }

        // Validación de integridad: el efectivo solo acepta montos enteros (sin centavos).
        var method = await _context.PaymentMethods.FindAsync(request.PaymentMethodId);
        if (method != null && method.IsCash && request.AmountBsS % 1 != 0)
        {
            throw new ArgumentException("El método de pago en efectivo solo acepta montos enteros.");
        }

        return new ComputedPaymentInput(rate, amountUsd, request.AmountBsS, method);
    }

    private sealed record ComputedPaymentInput(decimal Rate, decimal AmountUsd, decimal AmountBsS, PaymentMethod? Method);
}
