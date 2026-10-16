using Core.DTOs;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public interface ISettingsService
{
    Task<string> GetTimeZoneAsync();
    Task SetTimeZoneAsync(string timeZoneId);
    Task<string> GetCurrencyFormatAsync();
    Task SetCurrencyFormatAsync(string format);
    Task<bool> GetAllowNegativeStockAsync();
    Task SetAllowNegativeStockAsync(bool allowed);

    /// <summary>
    /// 8.184-T3 (S4): políticas globales de precios y redondeo (regla USD, regla de efectivo Bs.S y
    /// toggle de protección de margen) para precargar los selectores del modal de producto. El
    /// fallback silencioso ante error es el default del sistema: ExactMath / ExactMath / piso activo.
    /// </summary>
    Task<PricingPoliciesDto> GetPricingPoliciesAsync();

    /// <summary>
    /// 8.184-T4 (S2): persiste las políticas globales de precios y redondeo. Los TRES campos viajan
    /// juntos (el endpoint reemplaza el bloque completo); un fallo propaga la excepción para que la
    /// UI revierta al último estado aplicado (molde SetAllowNegativeStockAsync).
    /// </summary>
    Task SetPricingPoliciesAsync(PricingPoliciesDto policies);

    Task RestartSystemAsync();
}
