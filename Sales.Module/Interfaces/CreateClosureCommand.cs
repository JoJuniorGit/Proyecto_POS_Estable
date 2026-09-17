using System;
using System.Collections.Generic;

namespace Sales.Module.Interfaces;

public sealed record DeclaredPaymentAmount(int PaymentMethodId, decimal Amount);

public sealed record CreateClosureCommand(
    DateTime ClosureDateUtc,
    string? UserId,
    string? Observation,
    decimal ExchangeRate,
    IReadOnlyList<DeclaredPaymentAmount> Declarations);
