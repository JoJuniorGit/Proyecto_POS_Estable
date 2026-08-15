using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Desktop.Client.Messages;

public class ShiftClosedMessage : ValueChangedMessage<bool>
{
    public ShiftClosedMessage() : base(true)
    {
    }
}
