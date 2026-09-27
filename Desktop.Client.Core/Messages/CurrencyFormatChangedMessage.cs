namespace Desktop.Client.Messages;

/// <summary>
/// 8.143: notifica un cambio en caliente del ajuste de formato de moneda
/// (Venezolano/Internacional) para que las vistas abiertas re-notifiquen sus displays.
/// El payload no lleva datos: el estado activo vive en CurrencyDisplay.
/// </summary>
public sealed class CurrencyFormatChangedMessage
{
}
