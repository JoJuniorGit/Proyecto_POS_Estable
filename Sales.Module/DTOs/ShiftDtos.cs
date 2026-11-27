using System;
using System.Collections.Generic;
using Sales.Module.Services;

namespace Sales.Module.DTOs;

public class CloseShiftRequest
{
    public List<DeclaredAmountDto> DeclaredAmounts { get; set; } = new();

    /// <summary>
    /// Residual 8.187-8.188: conteo fisico USD opcional del cierre. Ausente/null conserva el
    /// comportamiento previo (el cierre no persiste arqueo USD).
    /// </summary>
    public decimal? ActualClosingBalanceUsd { get; set; }
}

public class DeclaredAmountDto
{
    public int PaymentMethodId { get; set; }
    public decimal Amount { get; set; }
}

public class ShiftReportDto
{
    public int ShiftId { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public string CashierCedula { get; set; } = string.Empty;
    public DateTime ClosedAt { get; set; }
    public decimal ExchangeRate { get; set; }

    /// <summary>
    /// Residual 8.187-8.188: arqueo USD del turno. Esperado y diferencia solo viajan a roles
    /// supervisores (blind 8.153); el contado se informa siempre que exista.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ExpectedClosingBalanceUsd { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ActualClosingBalanceUsd { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public decimal? DifferenceUsd { get; set; }

    public List<ShiftReportDetailDto> Details { get; set; } = new();
}