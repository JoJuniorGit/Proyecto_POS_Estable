using System.Collections.Generic;
using Core.DTOs;

namespace Sales.Module.DTOs;

/// <summary>
/// F2a (Paso 11, S2): contrato del cierre transaccional de venta a crédito (total o mixta).
/// Espeja <see cref="CompleteSaleRequest"/> pero el remanente no cubierto por los pagos
/// iniciales se financia como Cuenta por Cobrar en lugar de exigir liquidación al 100%.
/// </summary>
public class CreditSaleRequest
{
    /// <summary>Tasa BCV informada por el cliente; el servicio la ancla con el MISMO resolver SEC-08 del checkout regular.</summary>
    public decimal ExchangeRate { get; set; }

    /// <summary>Cajero de la operación cuando el historial debe firmarse con otro usuario; el usuario autenticado prevalece.</summary>
    public int? CashierId { get; set; }

    /// <summary>Pagos iniciales (abono) aplicados al contado; solo lo efectivamente cobrado ingresa a caja.</summary>
    public IEnumerable<SalePaymentDto> Payments { get; set; } = new List<SalePaymentDto>();
}
