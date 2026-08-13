using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Desktop.Client.Messages;

/// <summary>
/// Message broadcasted when the system exchange rate changes.
/// </summary>
public class ExchangeRateChangedMessage : ValueChangedMessage<decimal>
{
    public ExchangeRateChangedMessage(decimal value) : base(value)
    {
    }
}
