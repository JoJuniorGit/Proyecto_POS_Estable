using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// P0 (T2/S1/S2): el alta de un medio de pago puede incluir su comision. La regla se crea en la
/// misma operacion logica y, si falla, el metodo recien creado se elimina. Efectivo y 0% no
/// generan regla; la ausencia de regla significa costo cero.
/// </summary>
public class PaymentMethodCommissionCreationTests
{
    private const int ActingUserId = 42;

    [Fact]
    public async Task CreateMethodAsync_WithPercentageCommission_CreatesTheRuleForTheNewMethod()
    {
        await using var context = TestDatabaseFactory.CreateSalesDbContext();
        var controller = CreateController(context);
        var effectiveFromUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await controller.CreateMethodAsync(new CreatePaymentMethodDto
        {
            Name = "Tarjeta de Débito (TDD)",
            RequiresReference = true,
            Currency = "Bs.S",
            Commission = new PaymentMethodCommissionDto
            {
                FeeType = PaymentMethodFeeType.Percentage,
                Percentage = 0.75m,
                CurrencyCode = "USD",
                EffectiveFromUtc = effectiveFromUtc,
                IsActive = true
            }
        });

        Assert.IsType<CreatedAtActionResult>(result);

        var method = await context.PaymentMethods.SingleAsync(entity => entity.Name == "Tarjeta de Débito (TDD)");
        var rule = await context.PaymentMethodFeeRules.SingleAsync();
        Assert.Equal(method.Id, rule.PaymentMethodId);
        Assert.Equal(PaymentMethodFeeType.Percentage, rule.FeeType);
        Assert.Equal(0.75m, rule.Percentage);
        Assert.Null(rule.FixedAmount);
        Assert.Equal("USD", rule.CurrencyCode);
        Assert.Equal(effectiveFromUtc, rule.EffectiveFromUtc);
        Assert.True(rule.IsActive);
        Assert.Equal(ActingUserId, rule.CreatedByUserId);
    }

    [Fact]
    public async Task CreateMethodAsync_WithFixedCommission_CreatesTheFixedRule()
    {
        await using var context = TestDatabaseFactory.CreateSalesDbContext();
        var controller = CreateController(context);

        var result = await controller.CreateMethodAsync(new CreatePaymentMethodDto
        {
            Name = "BIOpago",
            RequiresReference = true,
            Commission = new PaymentMethodCommissionDto
            {
                FeeType = PaymentMethodFeeType.Fixed,
                FixedAmount = 0.25m,
                CurrencyCode = "USD"
            }
        });

        Assert.IsType<CreatedAtActionResult>(result);

        var rule = await context.PaymentMethodFeeRules.SingleAsync();
        Assert.Equal(PaymentMethodFeeType.Fixed, rule.FeeType);
        Assert.Equal(0.25m, rule.FixedAmount);
        // The request omitted the effective date: the rule starts today at 00:00 UTC.
        Assert.Equal(TimeSpan.Zero, rule.EffectiveFromUtc.TimeOfDay);
        Assert.Equal(DateTime.UtcNow.Date, rule.EffectiveFromUtc.Date);
    }

    [Fact]
    public async Task CreateMethodAsync_ZeroCommission_DoesNotCreateTheRule()
    {
        await using var context = TestDatabaseFactory.CreateSalesDbContext();
        var controller = CreateController(context);

        var result = await controller.CreateMethodAsync(new CreatePaymentMethodDto
        {
            Name = "Transferencia",
            RequiresReference = true,
            Commission = new PaymentMethodCommissionDto
            {
                FeeType = PaymentMethodFeeType.Percentage,
                Percentage = 0m,
                FixedAmount = 0m,
                CurrencyCode = "USD"
            }
        });

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Single(context.PaymentMethods);
        Assert.Empty(context.PaymentMethodFeeRules);
    }

    [Fact]
    public async Task CreateMethodAsync_WithoutCommission_KeepsTheExistingBehavior()
    {
        await using var context = TestDatabaseFactory.CreateSalesDbContext();
        var controller = CreateController(context);

        var result = await controller.CreateMethodAsync(new CreatePaymentMethodDto
        {
            Name = "Transferencia",
            RequiresReference = true
        });

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Single(context.PaymentMethods);
        Assert.Empty(context.PaymentMethodFeeRules);
    }

    [Fact]
    public async Task CreateMethodAsync_CashWithPositiveCommission_Returns400AndCreatesNothing()
    {
        await using var context = TestDatabaseFactory.CreateSalesDbContext();
        var controller = CreateController(context);

        var result = await controller.CreateMethodAsync(new CreatePaymentMethodDto
        {
            Name = "Efectivo USD",
            IsCash = true,
            Commission = new PaymentMethodCommissionDto
            {
                FeeType = PaymentMethodFeeType.Percentage,
                Percentage = 1m,
                CurrencyCode = "USD"
            }
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(400, problem.Status);
        Assert.Empty(context.PaymentMethods);
        Assert.Empty(context.PaymentMethodFeeRules);
    }

    [Fact]
    public async Task CreateMethodAsync_CashWithZeroCommission_CreatesTheMethodWithoutRule()
    {
        await using var context = TestDatabaseFactory.CreateSalesDbContext();
        var controller = CreateController(context);

        var result = await controller.CreateMethodAsync(new CreatePaymentMethodDto
        {
            Name = "Efectivo USD",
            IsCash = true,
            Commission = new PaymentMethodCommissionDto
            {
                FeeType = PaymentMethodFeeType.Percentage,
                Percentage = 0m,
                CurrencyCode = "USD"
            }
        });

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Single(context.PaymentMethods);
        Assert.Empty(context.PaymentMethodFeeRules);
    }

    [Fact]
    public async Task CreateMethodAsync_RuleCreationFails_RollsBackTheCreatedMethod()
    {
        await using var context = TestDatabaseFactory.CreateSalesDbContext();
        var controller = CreateController(context);

        // EUR is outside the active USD/local regime: the fee-rule service rejects it after the
        // method row was created, so the controller must compensate and leave no orphan method.
        var result = await controller.CreateMethodAsync(new CreatePaymentMethodDto
        {
            Name = "BIOpago",
            RequiresReference = true,
            Commission = new PaymentMethodCommissionDto
            {
                FeeType = PaymentMethodFeeType.Percentage,
                Percentage = 2.70m,
                CurrencyCode = "EUR"
            }
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(context.PaymentMethods);
        Assert.Empty(context.PaymentMethodFeeRules);
    }

    private static PaymentMethodsController CreateController(SalesDbContext context)
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "VES",
                SecondaryCurrencySymbol = "Bs.S"
            });

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(ActingUserId.ToString());

        return new PaymentMethodsController(
            new PaymentMethodService(context),
            new PaymentMethodFeeRuleService(context, profile.Object),
            currentUser.Object);
    }
}
