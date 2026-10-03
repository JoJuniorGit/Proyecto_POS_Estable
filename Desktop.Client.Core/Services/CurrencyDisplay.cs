using Core.Helpers;

namespace Desktop.Client.Services;

/// <summary>
/// 8.143: estado de display del formato de moneda activo en el cliente WPF.
/// El ajuste vive server-side (clave "CurrencyFormat"); se sincroniza al arrancar la app
/// y al cambiarlo desde Settings, y por defecto es Venezuelan.
/// </summary>
public static class CurrencyDisplay
{
    public static MoneyDisplayFormat Current { get; set; } = MoneyDisplayFormat.Venezuelan;

    public static void SetFromSetting(string? key) => Current = MoneyFormat.ParseFormat(key);

    public static string Number(decimal value, int decimals = 2) => MoneyFormat.Number(value, Current, decimals);
}
