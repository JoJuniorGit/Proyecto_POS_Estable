namespace Desktop.Client.Messages;

/// <summary>
/// 8.211-T4c1 (S9): notifica un cambio en caliente del perfil monetario de presentación para que
/// las pantallas lo re-apliquen sin reiniciar. El payload no lleva datos: el estado activo vive en
/// <c>CurrencyDisplay</c> y la migración de las vistas corresponde a T4c2.
/// </summary>
public sealed class MonetaryProfileChangedMessage
{
}
