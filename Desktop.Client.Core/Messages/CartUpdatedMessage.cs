namespace Desktop.Client.Messages;

/// <summary>
/// Message sent when the shopping cart total is recalculated.
/// </summary>
/// <param name="NewTotal">The updated total amount in USD.</param>
public record CartUpdatedMessage(decimal NewTotal);
