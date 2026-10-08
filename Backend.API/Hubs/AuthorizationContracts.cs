using System.ComponentModel.DataAnnotations;

namespace Backend.API.Hubs;

/// <summary>
/// 8.150 (T3, design D4): contrato cliente-facing del hub de autorizaciones (T4). El contexto
/// tipado corresponde a la accion v1 ManualPriceOverride; la identidad del solicitante la
/// completa el hub desde el JWT.
/// </summary>
public sealed class RequestAuthorizationContract
{
    [Range(1, int.MaxValue)]
    public int SaleId { get; set; }

    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [StringLength(200)]
    public string? ProductName { get; set; }

    public decimal Quantity { get; set; }

    public decimal? CustomUnitPriceUsd { get; set; }

    public decimal? CustomUnitPriceLocal { get; set; }

    [StringLength(100)]
    public string? Terminal { get; set; }
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
