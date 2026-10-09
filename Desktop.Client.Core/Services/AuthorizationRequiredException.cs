using System;

namespace Desktop.Client.Services;

/// <summary>
/// 8.150 (T10, design D5/D7): condicion detectable del gate de acciones protegidas. Se lanza
/// cuando el backend rechaza la operacion con un 403 ProblemDetails que incluye la extension
/// <c>authorizationRequired</c>, de modo que el POS arranca el flujo de espera sin inspeccionar
/// strings del mensaje.
/// </summary>
public sealed class AuthorizationRequiredException : Exception
{
    public AuthorizationRequiredException(string? message, string? authorizationAction)
        : base(string.IsNullOrWhiteSpace(message) ? "Se requiere autorización remota." : message)
    {
        AuthorizationAction = authorizationAction;
    }

    /// <summary>Tipo de permiso declarado por el backend (p. ej. ManualPriceOverride).</summary>
    public string? AuthorizationAction { get; }
}
