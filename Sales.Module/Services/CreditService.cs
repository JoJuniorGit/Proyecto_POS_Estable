using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// F2a (Paso 11, S1/S2/S4): perfil crediticio y solvencia del cliente. Fuente única del cupo
/// disponible (Límite − Saldo deudor, acotado a ≥ 0: la deuda nunca produce disponible negativo),
/// la utilización, la mora dinámica (vencimiento + gracia vs. día corriente de Venezuela, sin
/// depender del job materializador de T3) y los bloqueos de elegibilidad en español. Sin
/// sobregiro: exceder el disponible bloquea (T3 autoriza); con el cupo por debajo de la deuda el
/// disponible es 0.00, la utilización supera 100% y ninguna compra financiada nueva es elegible,
/// sin afectar el cobro de las obligaciones existentes.
/// </summary>
public partial class CreditService : ICreditService
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

    // T3a (F2b, S6): tasa efectiva del día para proyectar el saldo en Bs.S del estado de cuenta.
    // Opcional para preservar la construcción directa histórica de tests; sin ella la tasa del
    // día se reporta 0 y las proyecciones Bs.S quedan en cero.
    private readonly ITodayExchangeRateProvider? _rateProvider;

    // T4-prep (S1): traza de la escritura administrativa del perfil crediticio. Opcional para no
    // romper la construcción directa histórica de tests.
    private readonly ILogger<CreditService>? _logger;

    public CreditService(
        SalesDbContext context,
        ITodayExchangeRateProvider? rateProvider = null,
        ILogger<CreditService>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _rateProvider = rateProvider;
        _logger = logger;
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
        result.CustomerName = customer.Name;

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

    /// <inheritdoc />
    public async Task<CreditStatementDto> GetCreditStatementAsync(int customerId, CancellationToken cancellationToken = default)
    {
        var profile = await GetCreditProfileAsync(customerId, cancellationToken);
        decimal todayRate = _rateProvider != null
            ? await _rateProvider.GetEffectiveTodayRateAsync(cancellationToken)
            : 0m;
        var venezuelaToday = TimeZoneHelper.GetVenezuelaDate();

        // Cartera completa (abierta y cerrada) en orden de vencimiento: el Bs.S del saldo es una
        // proyección a la tasa del día, nunca una revaluación del principal emitido.
        var receivableEntities = await _context.Receivables
            .AsNoTracking()
            .Include(receivable => receivable.Sale)
            .Where(receivable => receivable.CustomerId == customerId)
            .OrderBy(receivable => receivable.DueDateUtc)
            .ThenBy(receivable => receivable.IssuedAtUtc)
            .ThenBy(receivable => receivable.Id)
            .ToListAsync(cancellationToken);

        var receivables = receivableEntities.Select(receivable => new CreditStatementReceivableDto
        {
            ReceivableId = receivable.Id,
            SaleId = receivable.SaleId,
            InvoiceNumber = receivable.Sale?.InvoiceNumber,
            OriginalUSD = receivable.OriginalUSD,
            BalanceUSD = receivable.BalanceUSD,
            OriginalBsS = receivable.OriginalBsS,
            HistoricalRate = receivable.HistoricalRate,
            BalanceTodayBsS = PricingCalculator.ToBsS(receivable.BalanceUSD, todayRate),
            IssuedAtUtc = receivable.IssuedAtUtc,
            DueDateUtc = receivable.DueDateUtc,
            Status = receivable.Status.ToString(),
            IsOverdue = receivable.BalanceUSD > BalanceToleranceUsd
                && OpenStatuses.Contains(receivable.Status)
                && IsOverdueBeyondGrace(receivable.DueDateUtc, profile.CreditGraceDays, venezuelaToday)
        }).ToList();

        var paymentEntities = await _context.ReceivablePayments
            .AsNoTracking()
            .Include(payment => payment.PaymentMethod)
            .Where(payment => payment.CustomerId == customerId)
            .OrderByDescending(payment => payment.ReceivedAtUtc)
            .ThenByDescending(payment => payment.Id)
            .ToListAsync(cancellationToken);

        var payments = paymentEntities.Select(payment => new CreditStatementPaymentDto
        {
            PaymentId = payment.Id,
            AmountUSD = payment.AmountUSD,
            AmountBsS = payment.AmountBsS,
            PaymentRate = payment.PaymentRate,
            FxDifferenceBsS = payment.FxDifferenceBsS,
            ReceivedAtUtc = payment.ReceivedAtUtc,
            PaymentMethodId = payment.PaymentMethodId,
            PaymentMethodName = payment.PaymentMethod?.Name,
            ReferenceNumber = payment.ReferenceNumber,
            ReceivedByUserId = payment.ReceivedByUserId,
            CashTransactionId = payment.CashTransactionId
        }).ToList();

        return new CreditStatementDto
        {
            Profile = profile,
            TodayRate = todayRate,
            CurrentDebtTodayBsS = PricingCalculator.ToBsS(profile.CurrentDebtUSD, todayRate),
            Receivables = receivables,
            Payments = payments
        };
    }

    /// <inheritdoc />
    public async Task<CreditProfileDto> UpdateCustomerCreditProfileAsync(
        int customerId,
        CustomerCreditProfileUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var customer = await _context.Customers
            .FirstOrDefaultAsync(candidate => candidate.Id == customerId, cancellationToken)
            ?? throw new KeyNotFoundException($"Cliente {customerId} no encontrado.");

        ValidateProfileUpdate(request);

        var previousStatus = customer.CreditStatus;
        var previousLimit = customer.CreditLimitUSD;
        var trimmedNotes = string.IsNullOrWhiteSpace(request.CreditNotes) ? null : request.CreditNotes.Trim();

        // Entidad rastreada (no AsNoTracking): SaveChanges conserva el token xmin de Customer y
        // una edición concurrente de otro supervisor falla cerrada en vez de pisar el perfil.
        customer.IsCreditEnabled = request.IsCreditEnabled;
        customer.CreditLimitUSD = request.CreditLimitUSD;
        customer.PaymentTermDays = request.PaymentTermDays;
        customer.CreditGraceDays = request.CreditGraceDays;
        customer.CreditNotes = trimmedNotes;
        customer.CreditStatus = request.CreditStatus;
        customer.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger?.LogInformation(
            "Perfil crediticio actualizado: cliente {CustomerId}; habilitado {IsCreditEnabled}; cupo {PreviousLimit}->{CreditLimitUSD}; plazo {PaymentTermDays} días; gracia {CreditGraceDays} días; estado {PreviousStatus}->{CreditStatus}.",
            customer.Id,
            customer.IsCreditEnabled,
            previousLimit,
            customer.CreditLimitUSD,
            customer.PaymentTermDays,
            customer.CreditGraceDays,
            previousStatus,
            customer.CreditStatus);

        return await GetCreditProfileAsync(customerId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CreditReceivablesListDto> GetCreditReceivablesAsync(
        CreditReceivablesFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        filter ??= new CreditReceivablesFilterDto();
        ReceivableStatus? statusFilter = ParseStatusFilter(filter.Status);
        string? search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();
        int? page = filter.Page.HasValue ? Math.Max(1, filter.Page.Value) : null;
        int? pageSize = filter.PageSize.HasValue ? Math.Clamp(filter.PageSize.Value, 1, 500) : null;
        var venezuelaToday = TimeZoneHelper.GetVenezuelaDate();
        decimal todayRate = _rateProvider != null
            ? await _rateProvider.GetEffectiveTodayRateAsync(cancellationToken)
            : 0m;

        // 8.202-H4 (S7): PostgreSQL resuelve filtros, mora, totales y página en SQL. Los
        // proveedores de test (SQLite/InMemory) conservan el camino en memoria para no depender
        // de traducciones que no soportan (DateOnly/AddDays con columna).
        return _context.Database.IsNpgsql()
            ? await GetCreditReceivablesFromPostgresAsync(
                filter, statusFilter, search, page, pageSize, venezuelaToday, todayRate, cancellationToken)
            : await GetCreditReceivablesFromMemoryAsync(
                filter, statusFilter, search, page, pageSize, venezuelaToday, todayRate, cancellationToken);
    }

    private async Task<CreditReceivablesListDto> GetCreditReceivablesFromPostgresAsync(
        CreditReceivablesFilterDto filter,
        ReceivableStatus? statusFilter,
        string? search,
        int? page,
        int? pageSize,
        DateOnly venezuelaToday,
        decimal todayRate,
        CancellationToken cancellationToken)
    {
        var query = _context.Receivables.AsNoTracking().AsQueryable();

        if (filter.CustomerId.HasValue)
        {
            int customerId = filter.CustomerId.Value;
            query = query.Where(receivable => receivable.CustomerId == customerId);
        }

        if (statusFilter.HasValue)
        {
            ReceivableStatus status = statusFilter.Value;
            query = query.Where(receivable => receivable.Status == status);
        }

        if (search is not null)
        {
            string pattern = ToLikePattern(search);
            bool isNumericTerm = int.TryParse(search, out int invoiceNumber);
            query = query.Where(receivable =>
                EF.Functions.ILike(receivable.Customer!.Name, pattern)
                || EF.Functions.ILike(receivable.Customer!.CedulaOrRif, pattern)
                || (receivable.Sale != null
                    && receivable.Sale.InvoiceNumber != null
                    && receivable.Sale.InvoiceNumber.Value.ToString().Contains(search))
                || (isNumericTerm
                    && receivable.Sale != null
                    && receivable.Sale.InvoiceNumber == invoiceNumber));
        }

        // Mora dinámica: dueDate (fecha UTC) + gracia < hoy Venezuela equivale a
        // dueDate + gracia < hoy a las 00:00 UTC. La gracia es una columna por cliente.
        DateTime overdueCutoffUtc = venezuelaToday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (filter.OverdueOnly)
        {
            query = query.Where(receivable =>
                (receivable.Status == ReceivableStatus.Vigente
                    || receivable.Status == ReceivableStatus.Parcial
                    || receivable.Status == ReceivableStatus.Vencida)
                && receivable.BalanceUSD > BalanceToleranceUsd
                && receivable.DueDateUtc.AddDays(receivable.Customer!.CreditGraceDays) < overdueCutoffUtc);
        }

        int totalCount = await query.CountAsync(cancellationToken);

        var totals = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                OpenCount = group.Count(receivable =>
                    (receivable.Status == ReceivableStatus.Vigente
                        || receivable.Status == ReceivableStatus.Parcial
                        || receivable.Status == ReceivableStatus.Vencida)
                    && receivable.BalanceUSD > BalanceToleranceUsd),
                OpenTotalUsd = group.Sum(receivable =>
                    (receivable.Status == ReceivableStatus.Vigente
                        || receivable.Status == ReceivableStatus.Parcial
                        || receivable.Status == ReceivableStatus.Vencida)
                    && receivable.BalanceUSD > BalanceToleranceUsd
                        ? receivable.BalanceUSD
                        : 0m),
                OverdueTotalUsd = group.Sum(receivable =>
                    (receivable.Status == ReceivableStatus.Vigente
                        || receivable.Status == ReceivableStatus.Parcial
                        || receivable.Status == ReceivableStatus.Vencida)
                    && receivable.BalanceUSD > BalanceToleranceUsd
                    && receivable.DueDateUtc.AddDays(receivable.Customer!.CreditGraceDays) < overdueCutoffUtc
                        ? receivable.BalanceUSD
                        : 0m)
            })
            .FirstOrDefaultAsync(cancellationToken);

        IQueryable<Receivable> ordered = query
            .OrderBy(receivable => receivable.DueDateUtc)
            .ThenBy(receivable => receivable.IssuedAtUtc)
            .ThenBy(receivable => receivable.Id);

        if (pageSize.HasValue)
        {
            int effectivePage = page ?? 1;
            ordered = ordered
                .Skip((effectivePage - 1) * pageSize.Value)
                .Take(pageSize.Value);
        }

        var rows = await ordered
            .Select(receivable => new
            {
                receivable.Id,
                receivable.CustomerId,
                CustomerName = receivable.Customer!.Name,
                CustomerCedulaOrRif = receivable.Customer!.CedulaOrRif,
                receivable.SaleId,
                InvoiceNumber = receivable.Sale != null ? receivable.Sale.InvoiceNumber : null,
                receivable.OriginalUSD,
                receivable.BalanceUSD,
                receivable.OriginalBsS,
                receivable.HistoricalRate,
                receivable.IssuedAtUtc,
                receivable.DueDateUtc,
                receivable.Status,
                GraceDays = receivable.Customer!.CreditGraceDays
            })
            .ToListAsync(cancellationToken);

        var result = new CreditReceivablesListDto
        {
            TodayRate = todayRate,
            TotalCount = totalCount,
            Page = pageSize.HasValue ? page ?? 1 : null,
            PageSize = pageSize,
            HasMore = pageSize.HasValue && (page ?? 1) * pageSize.Value < totalCount,
            OpenCount = totals?.OpenCount ?? 0,
            TotalOpenUSD = RoundUsd(totals?.OpenTotalUsd ?? 0m),
            TotalOverdueUSD = RoundUsd(totals?.OverdueTotalUsd ?? 0m)
        };

        foreach (var row in rows)
        {
            bool isOverdue = row.BalanceUSD > BalanceToleranceUsd
                && OpenStatuses.Contains(row.Status)
                && IsOverdueBeyondGrace(row.DueDateUtc, row.GraceDays, venezuelaToday);

            result.Items.Add(new CreditReceivableListItemDto
            {
                ReceivableId = row.Id,
                CustomerId = row.CustomerId,
                CustomerName = row.CustomerName,
                CustomerCedulaOrRif = row.CustomerCedulaOrRif,
                SaleId = row.SaleId,
                InvoiceNumber = row.InvoiceNumber,
                OriginalUSD = row.OriginalUSD,
                BalanceUSD = row.BalanceUSD,
                OriginalBsS = row.OriginalBsS,
                HistoricalRate = row.HistoricalRate,
                BalanceTodayBsS = PricingCalculator.ToBsS(row.BalanceUSD, todayRate),
                IssuedAtUtc = row.IssuedAtUtc,
                DueDateUtc = row.DueDateUtc,
                Status = row.Status.ToString(),
                IsOverdue = isOverdue
            });
        }

        return result;
    }

    private async Task<CreditReceivablesListDto> GetCreditReceivablesFromMemoryAsync(
        CreditReceivablesFilterDto filter,
        ReceivableStatus? statusFilter,
        string? search,
        int? page,
        int? pageSize,
        DateOnly venezuelaToday,
        decimal todayRate,
        CancellationToken cancellationToken)
    {
        var query = _context.Receivables
            .AsNoTracking()
            .AsSplitQuery()
            .Include(receivable => receivable.Sale)
            .Include(receivable => receivable.Customer)
            .AsQueryable();

        if (filter.CustomerId.HasValue)
        {
            int customerId = filter.CustomerId.Value;
            query = query.Where(receivable => receivable.CustomerId == customerId);
        }

        if (statusFilter.HasValue)
        {
            ReceivableStatus status = statusFilter.Value;
            query = query.Where(receivable => receivable.Status == status);
        }

        var entities = await query
            .OrderBy(receivable => receivable.DueDateUtc)
            .ThenBy(receivable => receivable.IssuedAtUtc)
            .ThenBy(receivable => receivable.Id)
            .ToListAsync(cancellationToken);

        var filtered = new List<Receivable>(entities.Count);
        decimal openTotalUsd = 0m;
        decimal overdueTotalUsd = 0m;
        int openCount = 0;

        foreach (var receivable in entities)
        {
            bool isOverdue = receivable.BalanceUSD > BalanceToleranceUsd
                && OpenStatuses.Contains(receivable.Status)
                && IsOverdueBeyondGrace(
                    receivable.DueDateUtc,
                    receivable.Customer?.CreditGraceDays ?? 0,
                    venezuelaToday);

            if (filter.OverdueOnly && !isOverdue)
            {
                continue;
            }

            if (!MatchesSearch(receivable, search))
            {
                continue;
            }

            filtered.Add(receivable);

            if (receivable.BalanceUSD > BalanceToleranceUsd && OpenStatuses.Contains(receivable.Status))
            {
                openCount++;
                openTotalUsd += receivable.BalanceUSD;
                if (isOverdue)
                {
                    overdueTotalUsd += receivable.BalanceUSD;
                }
            }
        }

        int totalCount = filtered.Count;
        IEnumerable<Receivable> pageRows = filtered;
        if (pageSize.HasValue)
        {
            int effectivePage = page ?? 1;
            pageRows = pageRows
                .Skip((effectivePage - 1) * pageSize.Value)
                .Take(pageSize.Value);
        }

        var result = new CreditReceivablesListDto
        {
            TodayRate = todayRate,
            TotalCount = totalCount,
            Page = pageSize.HasValue ? page ?? 1 : null,
            PageSize = pageSize,
            HasMore = pageSize.HasValue && (page ?? 1) * pageSize.Value < totalCount,
            OpenCount = openCount,
            TotalOpenUSD = RoundUsd(openTotalUsd),
            TotalOverdueUSD = RoundUsd(overdueTotalUsd)
        };

        foreach (var receivable in pageRows)
        {
            result.Items.Add(MapReceivable(receivable, todayRate, venezuelaToday));
        }

        return result;
    }

    private static CreditReceivableListItemDto MapReceivable(
        Receivable receivable,
        decimal todayRate,
        DateOnly venezuelaToday)
    {
        bool isOverdue = receivable.BalanceUSD > BalanceToleranceUsd
            && OpenStatuses.Contains(receivable.Status)
            && IsOverdueBeyondGrace(
                receivable.DueDateUtc,
                receivable.Customer?.CreditGraceDays ?? 0,
                venezuelaToday);

        return new CreditReceivableListItemDto
        {
            ReceivableId = receivable.Id,
            CustomerId = receivable.CustomerId,
            CustomerName = receivable.Customer?.Name ?? string.Empty,
            CustomerCedulaOrRif = receivable.Customer?.CedulaOrRif ?? string.Empty,
            SaleId = receivable.SaleId,
            InvoiceNumber = receivable.Sale?.InvoiceNumber,
            OriginalUSD = receivable.OriginalUSD,
            BalanceUSD = receivable.BalanceUSD,
            OriginalBsS = receivable.OriginalBsS,
            HistoricalRate = receivable.HistoricalRate,
            BalanceTodayBsS = PricingCalculator.ToBsS(receivable.BalanceUSD, todayRate),
            IssuedAtUtc = receivable.IssuedAtUtc,
            DueDateUtc = receivable.DueDateUtc,
            Status = receivable.Status.ToString(),
            IsOverdue = isOverdue
        };
    }

    /// <summary>
    /// Escapa los comodines de LIKE/ILIKE para que la búsqueda sea "contiene texto plano".
    /// PostgreSQL usa la barra invertida como escape por defecto.
    /// </summary>
    private static string ToLikePattern(string term) =>
        "%" + term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static void ValidateProfileUpdate(CustomerCreditProfileUpdateDto request)
    {
        if (request.CreditLimitUSD < 0m)
        {
            throw new ArgumentException("El límite de crédito no puede ser negativo.", nameof(request));
        }

        if (request.PaymentTermDays < 1 || request.PaymentTermDays > 365)
        {
            throw new ArgumentException("El plazo de pago debe estar entre 1 y 365 días continuos.", nameof(request));
        }

        if (request.CreditGraceDays < 0)
        {
            throw new ArgumentException("Los días de gracia no pueden ser negativos.", nameof(request));
        }

        if (request.CreditNotes is not null && request.CreditNotes.Trim().Length > 1000)
        {
            throw new ArgumentException("Las notas crediticias no pueden exceder 1000 caracteres.", nameof(request));
        }

        if (request.CreditStatus == CreditStatus.SuspendedByMora)
        {
            throw new ArgumentException(
                "El estado Suspendido por Mora es administrado por el sistema y no puede asignarse manualmente.",
                nameof(request));
        }

        if (!Enum.IsDefined(request.CreditStatus))
        {
            throw new ArgumentException("El estado crediticio informado no es válido.", nameof(request));
        }
    }

    private static ReceivableStatus? ParseStatusFilter(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var normalized = status.Trim();
        if (Enum.TryParse<ReceivableStatus>(normalized, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"Estado de cuenta por cobrar inválido: '{normalized}'. Valores válidos: Vigente, Parcial, Vencida, Pagada, Anulada.",
            nameof(status));
    }

    private static bool MatchesSearch(Receivable receivable, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string term = search.Trim();
        return (receivable.Customer?.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (receivable.Customer?.CedulaOrRif?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (receivable.Sale?.InvoiceNumber?.ToString(CultureInfo.InvariantCulture)
                .Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static decimal RoundUsd(decimal amount) =>
        Math.Round(amount, 4, MidpointRounding.AwayFromZero);

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
    /// Mora dinámica (S1/S2/S7): vencida cuando la fecha de vencimiento de la obligación más los
    /// días de gracia del cliente queda estrictamente antes del día corriente en Venezuela. Es la
    /// regla compartida por el perfil, la elegibilidad, la transición de estados del abono y el
    /// estado de cuenta.
    /// </summary>
    public static bool IsOverdueBeyondGrace(DateTime dueDateUtc, int graceDays, DateOnly venezuelaToday)
    {
        var dueDate = DateOnly.FromDateTime(dueDateUtc);
        return dueDate.AddDays(graceDays) < venezuelaToday;
    }

    private static decimal SumBalances(List<OpenBalance> openBalances) =>
        Math.Round(openBalances.Sum(balance => balance.BalanceUSD), 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Cupo disponible para nuevas compras financiadas: máximo entre cero y (límite − deuda).
    /// Con la deuda por encima del cupo el disponible es 0.00 —nunca negativo— y la utilización
    /// puede superar 100%; la elegibilidad bloquea así cualquier monto nuevo sin tocar el cobro
    /// de las obligaciones ya emitidas.
    /// </summary>
    private static decimal ComputeAvailableCredit(decimal creditLimitUSD, decimal debtUsd) =>
        Math.Max(0m, Math.Round(creditLimitUSD - debtUsd, 4, MidpointRounding.AwayFromZero));

    // Sin límite positivo la utilización no es definible (división por cero); se reporta 0.
    private static decimal ComputeUtilizationPercent(decimal creditLimitUSD, decimal debtUsd) =>
        creditLimitUSD > 0m
            ? Math.Round(debtUsd / creditLimitUSD * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

    private readonly record struct OpenBalance(decimal BalanceUSD, DateTime DueDateUtc);
}
