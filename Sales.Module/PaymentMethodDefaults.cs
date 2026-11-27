using Sales.Module.Entities;

namespace Sales.Module;

public static class PaymentMethodDefaults
{
    public static IReadOnlyList<PaymentMethod> CreateDefault()
    {
        return new[]
        {
            new PaymentMethod { Name = "Efectivo", IsActive = true, IsCash = true, RequiresReference = false, DisplayOrder = 1, Currency = PaymentMethodCurrencyResolver.LocalCurrency },
            new PaymentMethod { Name = "Tarjeta (Punto de Venta)", IsActive = true, IsCash = false, RequiresReference = true, DisplayOrder = 2, Currency = PaymentMethodCurrencyResolver.LocalCurrency },
            new PaymentMethod { Name = "Transferencia / Pago Móvil", IsActive = true, IsCash = false, RequiresReference = true, DisplayOrder = 3, Currency = PaymentMethodCurrencyResolver.LocalCurrency },
            new PaymentMethod { Name = "Divisas (USD)", IsActive = true, IsCash = true, RequiresReference = false, DisplayOrder = 4, Currency = PaymentMethodCurrencyResolver.Usd }
        };
    }
}