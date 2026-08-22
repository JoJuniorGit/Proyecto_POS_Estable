using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Desktop.Client.Messages;

/// <summary>
/// Message broadcasted when OnHold sales are updated/recalculated on the server.
/// </summary>
public class OnHoldSalesRefreshMessage : ValueChangedMessage<bool>
{
    public OnHoldSalesRefreshMessage() : base(true)
    {
    }
}
