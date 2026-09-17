using System;
using System.Collections.Generic;

namespace Sales.Module.Interfaces;

/// <summary>
/// Command object for creating a daily closure. The controller builds this from the
/// close-request declarations; the service classifies currency via PaymentMethodCurrencyResolver.
/// </summary>
public sealed record DeclaredPaymentAmount(int PaymentMethodId, decimal Amount);

public sealed record CreateClosureCommand(
    DateTime ClosureDateUtc,
    string? UserId,
    string? Observation,
    IReadOnlyList<DeclaredPaymentAmount> Declarations);
