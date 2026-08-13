using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Sent via <see cref="CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger"/>
/// whenever a payment method is created, updated, or deleted in Settings.
/// </summary>
public sealed class PaymentMethodsChangedMessage : ValueChangedMessage<bool>
{
    public PaymentMethodsChangedMessage() : base(true) { }
}
