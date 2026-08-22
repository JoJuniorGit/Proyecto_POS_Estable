using CommunityToolkit.Mvvm.Messaging.Messages;
using Core.DTOs;

namespace Desktop.Client.Messages;

/// <summary>
/// Message broadcast when the active sales transaction changes or is updated by the SalesService.
/// </summary>
public class CurrentSaleChangedMessage : ValueChangedMessage<SaleDto?>
{
    public CurrentSaleChangedMessage(SaleDto? value) : base(value)
    {
    }
}
