using System;
using System.Collections.Generic;

namespace Sales.Module.Interfaces;

public sealed record DeclaredPaymentAmount(int PaymentMethodId, decimal Amount);

public sealed record CreateClosureCommand(
    DateTime ClosureDateUtc,
    string? UserId,
    string? Observation,
    IReadOnlyList<DeclaredPaymentAmount> Declarations,
    // Residual 8.187-8.188: conteo fisico USD opcional del cierre diario. null conserva el
    // contrato previo (no se persiste arqueo USD).
    decimal? ActualClosingBalanceUsd = null);
