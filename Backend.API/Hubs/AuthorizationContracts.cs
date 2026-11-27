using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Core.Entities;

namespace Backend.API.Hubs;

/// <summary>
/// 8.150 (T3, design D4): contrato cliente-facing del hub de autorizaciones (T4). La accion v1
/// es ManualPriceOverride (comportamiento historico: sin ActionType explicito se asume esa accion);
/// T3b1 (F2b, S4) agrega CreditOverLimit, cuyo contexto lleva cliente, monto a financiar y credito
/// disponible. T2a (Paso 13, S4) agrega PettyCashExpense, cuyo contexto lleva sesion, moneda,
/// montos bimonetarios y categoria (sin venta asociada). La identidad del solicitante la completa
/// el hub desde el JWT.
/// </summary>
public sealed class RequestAuthorizationContract : IValidatableObject
{
    // Nullable desde T2a: las acciones sin venta (caja chica) viajan sin SaleId. La venta sigue
    // siendo obligatoria para las acciones historicas (Validate la exige explicitamente).
    [Range(1, int.MaxValue)]
    public int? SaleId { get; set; }

    // T3b1: nullable para que el contrato de sobregiro (sin producto) pase la validacion MVC;
    // IValidatableObject exige el producto cuando la accion es ManualPriceOverride.
    [Range(1, int.MaxValue)]
    public int? ProductId { get; set; }

    [StringLength(200)]
    public string? ProductName { get; set; }

    public decimal Quantity { get; set; }

    public decimal? CustomUnitPriceUsd { get; set; }

    public decimal? CustomUnitPriceLocal { get; set; }

    /// <summary>
    /// Accion solicitada. El transporte del hub/REST usa el valor entero del enum (convencion del
    /// repo: sin JsonStringEnumConverter); su ausencia equivale a ManualPriceOverride para no
    /// romper a los clientes historicos.
    /// </summary>
    public AuthorizationActionType ActionType { get; set; } = AuthorizationActionType.ManualPriceOverride;

    // Contexto CreditOverLimit (T3b1): cliente, monto efectivo a financiar y credito disponible.
    [Range(1, int.MaxValue)]
    public int? CustomerId { get; set; }

    [StringLength(200)]
    public string? CustomerName { get; set; }

    public decimal? FinancedUSD { get; set; }

    public decimal? AvailableCreditUSD { get; set; }

    // Contexto PettyCashExpense (T2a): sesion de caja, divisa fisica, montos bimonetarios y
    // categoria operativa. Lo consumen la solicitud del hub y el reintento con token.
    [Range(1, int.MaxValue)]
    public int? PettyCashSessionId { get; set; }

    public Sales.Module.Entities.PettyCashCurrency? PettyCashCurrency { get; set; }

    public decimal? PettyCashAmountBsS { get; set; }

    public decimal? PettyCashAmountUsd { get; set; }

    public Sales.Module.Entities.PettyCashCategory? PettyCashCategory { get; set; }

    [StringLength(100)]
    public string? Terminal { get; set; }

    /// <summary>
    /// Validacion condicional por accion para el par REST ([ApiController]); el hub aplica la misma
    /// regla en <c>AuthorizationCoordinator</c> porque SignalR no ejecuta data annotations.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ActionType == AuthorizationActionType.ManualPriceOverride)
        {
            if (SaleId is null)
            {
                yield return new ValidationResult(
                    "La venta es obligatoria para la autorización de precio manual.",
                    new[] { nameof(SaleId) });
            }

            if (ProductId is null)
            {
                yield return new ValidationResult(
                    "El producto es obligatorio para la autorización de precio manual.",
                    new[] { nameof(ProductId) });
            }

            yield break;
        }

        if (ActionType == AuthorizationActionType.CreditOverLimit)
        {
            if (SaleId is null)
            {
                yield return new ValidationResult(
                    "La venta es obligatoria para la autorización de sobregiro de crédito.",
                    new[] { nameof(SaleId) });
            }

            if (CustomerId is null)
            {
                yield return new ValidationResult(
                    "El cliente es obligatorio para la autorización de sobregiro de crédito.",
                    new[] { nameof(CustomerId) });
            }

            if (FinancedUSD is null || FinancedUSD <= 0m)
            {
                yield return new ValidationResult(
                    "El monto a financiar es obligatorio (mayor a cero) para la autorización de sobregiro.",
                    new[] { nameof(FinancedUSD) });
            }

            if (AvailableCreditUSD is null)
            {
                yield return new ValidationResult(
                    "El crédito disponible es obligatorio para la autorización de sobregiro.",
                    new[] { nameof(AvailableCreditUSD) });
            }

            yield break;
        }

        if (ActionType == AuthorizationActionType.PettyCashExpense)
        {
            if (PettyCashSessionId is null)
            {
                yield return new ValidationResult(
                    "La sesión de caja es obligatoria para la autorización del gasto de caja chica.",
                    new[] { nameof(PettyCashSessionId) });
            }

            if (PettyCashCurrency is null)
            {
                yield return new ValidationResult(
                    "La moneda del gasto de caja chica es obligatoria para la autorización.",
                    new[] { nameof(PettyCashCurrency) });
            }

            if (PettyCashAmountBsS is null or <= 0m)
            {
                yield return new ValidationResult(
                    "El monto Bs.S del gasto de caja chica es obligatorio (mayor a cero).",
                    new[] { nameof(PettyCashAmountBsS) });
            }

            if (PettyCashAmountUsd is null or <= 0m)
            {
                yield return new ValidationResult(
                    "El monto USD del gasto de caja chica es obligatorio (mayor a cero).",
                    new[] { nameof(PettyCashAmountUsd) });
            }

            if (PettyCashCategory is null)
            {
                yield return new ValidationResult(
                    "La categoría del gasto de caja chica es obligatoria para la autorización.",
                    new[] { nameof(PettyCashCategory) });
            }

            yield break;
        }

        yield return new ValidationResult(
            "La acción de autorización no está registrada.",
            new[] { nameof(ActionType) });
    }
}

/// <summary>
/// 8.150 (T3, design D4): contrato cliente-facing de resolucion (aprobar/rechazar con motivo).
/// </summary>
public sealed class ResolveAuthorizationContract
{
    [Range(1, int.MaxValue)]
    public int RequestId { get; set; }

    public bool Approved { get; set; }

    [StringLength(500)]
    public string? Reason { get; set; }
}
