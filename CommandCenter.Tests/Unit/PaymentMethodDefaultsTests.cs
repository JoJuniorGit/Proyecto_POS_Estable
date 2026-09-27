using System.Linq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PaymentMethodDefaultsTests
{
    [Fact]
    public void CreateDefault_ReturnsFourStandardMethods()
    {
        var methods = Sales.Module.PaymentMethodDefaults.CreateDefault();

        Assert.Equal(4, methods.Count);
        Assert.All(methods, m => Assert.True(m.IsActive));
        Assert.DoesNotContain(methods, m => m.IsDeleted);
    }

    [Theory]
    [InlineData(0, "Efectivo", true, false, 1)]
    [InlineData(1, "Tarjeta (Punto de Venta)", false, true, 2)]
    [InlineData(2, "Transferencia / Pago Móvil", false, true, 3)]
    [InlineData(3, "Divisas (USD)", true, false, 4)]
    public void CreateDefault_EachMethod_MatchesRequirement(
        int index, string expectedName, bool expectedIsCash, bool expectedRequiresReference, int expectedOrder)
    {
        var method = Sales.Module.PaymentMethodDefaults.CreateDefault()[index];

        Assert.Equal(expectedName, method.Name);
        Assert.Equal(expectedIsCash, method.IsCash);
        Assert.Equal(expectedRequiresReference, method.RequiresReference);
        Assert.Equal(expectedOrder, method.DisplayOrder);
    }

    [Fact]
    public void CreateDefault_CurrencyResolution_ClassifiesUsdMethodAsUsd()
    {
        var methods = Sales.Module.PaymentMethodDefaults.CreateDefault();

        var divisas = methods.Single(m => m.Name.Contains("USD"));
        var efectivo = methods.Single(m => m.Name == "Efectivo");

        Assert.Equal("USD", divisas.Currency);
        Assert.Equal("Bs.S", efectivo.Currency);
        Assert.Equal("Bs.S", methods.Single(m => m.Name.Contains("Tarjeta")).Currency);
        Assert.Equal("Bs.S", methods.Single(m => m.Name.Contains("Transferencia")).Currency);
    }
}