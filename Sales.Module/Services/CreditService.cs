using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// F2a (Paso 11, S1/S2/S4): perfil crediticio y solvencia del cliente. Fuente única del cupo
/// disponible (Límite − Saldo deudor), la utilización, la mora dinámica (vencimiento + gracia
/// vs. día corriente de Venezuela, sin depender del job materializador de T3) y los bloqueos
/// de elegibilidad en español. Sin sobregiro: exceder el disponible bloquea (T3 autoriza).
/// </summary>
public class CreditService : ICreditService
{
    /// <summary>Tolerancia de polvo decimal del saldo (S7): saldos ≤ 0.005 USD no computan deuda.</summary>
    public const decimal BalanceToleranceUsd = 0.005m;

    // Estados que representan deuda viva (S3): Pagada y Anulada quedan fuera del cupo.
    private static readonly ReceivableStatus[] OpenStatuses =
    {
        ReceivableStatus.Vigente,
        ReceivableStatus.Parcial,
        ReceivableStatus.Vencida
    };

    private readonly SalesDbContext _context;

    public CreditService(SalesDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<CreditProfileDto> GetCreditProfileAsync(int customerId, CancellationToken cancellationToken = default)
    {
        var customer = await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == customerId, cancellationToken)
            ?? throw new KeyNotFoundException($"Cliente {customerId} no encontrado.");

        var openBalances = await LoadOpenBalancesAsync(customerId, cancellationToken);
        decimal debtUsd = SumBalances(openBalances);
        decimal overdueUsd = SumBalances(SelectOverdue(openBalances, customer.CreditGraceDays));

        return new CreditProfileDto
        {
            CustomerId = customer.Id,
            CustomerName = customer.Name,
            IsActive = customer.IsActive,
            IsCreditEnabled = customer.IsCreditEnabled,
            CreditStatus = customer.CreditStatus,
            CreditNotes = customer.CreditNotes,
            CreditLimitUSD = customer.CreditLimitUSD,
            CurrentDebtUSD = debtUsd,
            AvailableCreditUSD = ComputeAvailableCredit(customer.CreditLimitUSD, debtUsd),
            UtilizationPercent = ComputeUtilizationPercent(customer.CreditLimitUSD, debtUsd),
            PaymentTermDays = customer.PaymentTermDays,
            CreditGraceDays = customer.CreditGraceDays,
            OverdueUSD = overdueUsd,
            OverdueCount = SelectOverdue(openBalances, customer.CreditGraceDays).Count
        };
    }

    /// <inheritdoc />
    public async Task<CreditEligibilityResultDto> EvaluateEligibilityAsync(int customerId, decimal amountUSD, CancellationToken cancellationToken = default)
    {
        var result = new CreditEligibilityResultDto { RequestedAmountUSD = amountUSD };

        var customer = await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == customerId, cancellationToken);

        if (customer == null)
        {
            result.Blockers.Add("El cliente no existe o no está registrado en el sistema.");
            result.IsEligible = false;
            return result;
        }

        result.CreditLimitUSD = customer.CreditLimitUSD;

        if (!customer.IsActive)
        {
            result.Blockers.Add("El cliente está inactivo y no puede operar bajo modalidad de crédito.");
        }

        if (!customer.IsCreditEnabled)
        {
            result.Blockers.Add("El cliente no tiene habilitada la línea de crédito.");
        }

        switch (customer.CreditStatus)
        {
            case CreditStatus.ManuallyBlocked:
                result.Blockers.Add("El crédito del cliente está bloqueado manualmente por la administración.");
                break;
            case CreditStatus.SuspendedByMora:
                result.Blockers.Add("El cliente está suspendido por morosidad.");
                break;
        }

        if (customer.PaymentTermDays <= 0)
        {
            result.Blockers.Add("El plazo de pago configurado del cliente es inválido (debe ser mayor a cero días continuos).");
        }

        var openBalances = await LoadOpenBalancesAsync(customerId, cancellationToken);
        decimal debtUsd = SumBalances(openBalances);
        decimal availableUsd = ComputeAvailableCredit(customer.CreditLimitUSD, debtUsd);

        result.CurrentDebtUSD = debtUsd;
        result.AvailableCreditUSD = availableUsd;

        if (SelectOverdue(openBalances, customer.CreditGraceDays).Count > 0)
        {
            result.Blockers.Add("El cliente posee cuentas vencidas fuera del período de gracia (mora).");
        }

        if (amountUSD > availableUsd)
        {
            result.Blockers.Add($"Crédito insuficiente: disponible ${availableUsd:F2} y el monto a financiar es ${amountUSD:F2}.");
        }

        result.IsEligible = result.Blockers.Count == 0;
        return result;
    }

    /// <summary>
    /// Saldos vivos del cliente (AsNoTracking, proyectados): estados abiertos con saldo por
    /// encima de la tolerancia de polvo decimal.
    /// </summary>
    private async Task<List<OpenBalance>> LoadOpenBalancesAsync(int customerId, CancellationToken cancellationToken)
    {
        return await _context.Receivables
            .AsNoTracking()
            .Where(receivable => receivable.CustomerId == customerId
                              && OpenStatuses.Contains(receivable.Status)
                              && receivable.BalanceUSD > BalanceToleranceUsd)
            .Select(receivable => new OpenBalance(receivable.BalanceUSD, receivable.DueDateUtc))
            .ToListAsync(cancellationToken);
    }

    private static List<OpenBalance> SelectOverdue(List<OpenBalance> openBalances, int graceDays)
    {
        var today = TimeZoneHelper.GetVenezuelaDate();
        return openBalances.Where(balance => IsOverdueBeyondGrace(balance.DueDateUtc, graceDays, today)).ToList();
    }

    /// <summary>
    /// Mora dinámica (S1/S2): vencida cuando la fecha de vencimiento de la obligación más los
    /// días de gracia del cliente queda estrictamente antes del día corriente en Venezuela.
    /// </summary>
    private static bool IsOverdueBeyondGrace(DateTime dueDateUtc, int graceDays, DateOnly venezuelaToday)
    {
        var dueDate = DateOnly.FromDateTime(dueDateUtc);
        return dueDate.AddDays(graceDays) < venezuelaToday;
    }

    private static decimal SumBalances(List<OpenBalance> openBalances) =>
        Math.Round(openBalances.Sum(balance => balance.BalanceUSD), 4, MidpointRounding.AwayFromZero);

    private static decimal ComputeAvailableCredit(decimal creditLimitUSD, decimal debtUsd) =>
        Math.Round(creditLimitUSD - debtUsd, 4, MidpointRounding.AwayFromZero);

    // Sin límite positivo la utilización no es definible (división por cero); se reporta 0.
    private static decimal ComputeUtilizationPercent(decimal creditLimitUSD, decimal debtUsd) =>
        creditLimitUSD > 0m
            ? Math.Round(debtUsd / creditLimitUSD * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

    private readonly record struct OpenBalance(decimal BalanceUSD, DateTime DueDateUtc);
}
