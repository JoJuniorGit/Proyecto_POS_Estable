namespace Desktop.Client.Messages;

public class CurrencyRateChangedMessage
{
    public decimal NewRate { get; }

    public CurrencyRateChangedMessage(decimal newRate)
    {
        NewRate = newRate;
    }
}
