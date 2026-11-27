using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T2a (S3/S4): endpoints GET/PUT de tasas manuales por par. GET queda disponible para
// Admin/Manager (política de clase); PUT exige Admin y el guard CanMutateSettings. F2a no emite
// SignalR ni toca flujos financieros.
public class CurrencyRateSettingsTests
{
    private const string SameCodeMessage = "El par de monedas debe tener códigos distintos.";
    private const string BcvPairMessage = "El par USD/VES se alimenta de la tasa BCV y no admite carga manual.";
    private const string InvalidRateMessage = "La tasa debe ser mayor a cero y menor o igual a 1.000.000.";
    private const string FutureDateMessage = "La fecha de la tasa no puede ser futura.";
    private const string MissingBodyMessage = "La tasa manual de moneda es obligatoria.";

    private static readonly DateOnly RateDate = new(2026, 10, 20);

    private static InventoryDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private static SettingsController CreateController(InventoryDbContext db, bool canMutateSettings)
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(canMutateSettings);
        mockUser.Setup(u => u.UserId).Returns("42");
        return ControllerFactory.CreateSettingsController(db, mockUser.Object, new SystemSettingsService(db));
    }

    private static ManualCurrencyRateRequest ValidRequest() => new()
    {
        FromCurrencyCode = "eur",
        ToCurrencyCode = "cop",
        RateDate = RateDate,
        Rate = 1234.123456789m
    };

    private static string GetProblemMessage(ActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        return Assert.IsType<string>(problem.Extensions["message"]);
    }

    // ---------------------------------------------------------------------------------------------
    // GET.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetManualCurrencyRates_WithoutRates_ReturnsEmptyList()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.GetManualCurrencyRatesAsync(RateDate);

        var ok = Assert.IsType<OkObjectResult>(result);
        var rates = Assert.IsAssignableFrom<IReadOnlyList<ManualCurrencyRateDto>>(ok.Value);
        Assert.Empty(rates);
    }

    [Fact]
    public async Task GetManualCurrencyRates_DoesNotRequireCanMutateSettings()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: false);

        var result = await controller.GetManualCurrencyRatesAsync(RateDate);

        Assert.IsType<OkObjectResult>(result);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT válido + round-trip.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetManualCurrencyRate_ValidPayload_ReturnsOkPersistsAndUsesCurrentUser()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.SetManualCurrencyRateAsync(ValidRequest());

        var ok = Assert.IsType<OkObjectResult>(result);
        var quote = Assert.IsType<CurrencyRateQuote>(ok.Value);
        Assert.Equal(1234.12345679m, quote.Rate);
        Assert.Equal(CurrencyRateSource.Manual, quote.Source);
        Assert.Equal(RateDate, quote.RateDate);

        var row = await db.CurrencyPairRates.AsNoTracking().SingleAsync();
        Assert.Equal("EUR", row.FromCurrencyCode);
        Assert.Equal("COP", row.ToCurrencyCode);
        Assert.Equal(1234.12345679m, row.Rate);
        Assert.Equal(42, row.UpdatedByUserId);
    }

    [Fact]
    public async Task SetManualCurrencyRate_ThenGet_RoundTripsTheStoredRate()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);

        await controller.SetManualCurrencyRateAsync(ValidRequest());
        var result = await controller.GetManualCurrencyRatesAsync(RateDate);

        var ok = Assert.IsType<OkObjectResult>(result);
        var rates = Assert.IsAssignableFrom<IReadOnlyList<ManualCurrencyRateDto>>(ok.Value);
        var rate = Assert.Single(rates);
        Assert.Equal("EUR", rate.FromCurrencyCode);
        Assert.Equal("COP", rate.ToCurrencyCode);
        Assert.Equal(1234.12345679m, rate.Rate);
        Assert.Equal(RateDate, rate.RateDate);
        Assert.Equal(42, rate.UpdatedByUserId);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT inválido -> 400 sin persistir.
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string, string, decimal, bool, string> InvalidRequests => new()
    {
        { "usd", "USD", 1m, false, SameCodeMessage },
        { "USD", "VES", 36m, false, BcvPairMessage },
        { "VES", "USD", 0.03m, false, BcvPairMessage },
        { "EUR", "COP", 0m, false, InvalidRateMessage },
        { "EUR", "COP", -5m, false, InvalidRateMessage },
        { "EUR", "COP", 1_000_001m, false, InvalidRateMessage },
        { "EUR", "COP", 100m, true, FutureDateMessage }
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task SetManualCurrencyRate_InvalidPayload_ReturnsBadRequestAndPersistsNothing(
        string from,
        string to,
        decimal rate,
        bool futureDate,
        string expectedMessage)
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);
        var request = new ManualCurrencyRateRequest
        {
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = rate,
            RateDate = futureDate ? TimeZoneHelper.GetVenezuelaDate().AddDays(1) : RateDate
        };

        var result = await controller.SetManualCurrencyRateAsync(request);

        Assert.Equal(expectedMessage, GetProblemMessage(result));
        Assert.Equal(0, await db.CurrencyPairRates.CountAsync());
    }

    [Fact]
    public async Task SetManualCurrencyRate_NullBody_ReturnsBadRequestAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.SetManualCurrencyRateAsync(null);

        Assert.Equal(MissingBodyMessage, GetProblemMessage(result));
        Assert.Equal(0, await db.CurrencyPairRates.CountAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // RBAC.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetManualCurrencyRate_WithoutCanMutateSettings_ReturnsForbiddenAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: false);

        var result = await controller.SetManualCurrencyRateAsync(ValidRequest());

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(0, await db.CurrencyPairRates.CountAsync());
    }
}
