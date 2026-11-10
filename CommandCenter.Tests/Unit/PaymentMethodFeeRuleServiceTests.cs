using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F3-WU1, F3-S1): CRUD y validaciones de las reglas de comisión de medios de pago.
/// La comisión es configuración histórica: la baja es lógica para preservar los snapshots de los
/// cobros y el método de pago es inmutable luego de crear la regla.
/// </summary>
public class PaymentMethodFeeRuleServiceTests
{
    private const int CashMethodId = 1;
    private const int CardMethodId = 2;
    private const int TransferMethodId = 3;
    private const int DeletedMethodId = 4;
    private const int ActingUserId = 42;
    private const int SecondActingUserId = 77;

    private static readonly DateTime ValidFromUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ValidToUtc = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAsync_PercentageRule_PersistsNormalizedRuleAndAuditFields()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);
        var before = DateTime.UtcNow;

        var created = await service.CreateAsync(
            CreateRequest(PaymentMethodFeeType.Percentage, percentage: 1.25m),
            ActingUserId);

        var after = DateTime.UtcNow;
        Assert.True(created.Id > 0);
        Assert.Equal(CardMethodId, created.PaymentMethodId);
        Assert.Equal("Tarjeta (Punto de Venta)", created.PaymentMethodName);
        Assert.Equal(PaymentMethodFeeType.Percentage, created.FeeType);
        Assert.Equal(1.25m, created.Percentage);
        Assert.Null(created.FixedAmount);
        Assert.Equal("USD", created.CurrencyCode);
        Assert.Equal(ValidFromUtc, created.EffectiveFromUtc);
        Assert.True(created.IsActive);
        Assert.Equal(DateTimeKind.Utc, created.CreatedAtUtc.Kind);
        Assert.InRange(created.CreatedAtUtc, before, after);
        Assert.Equal(created.CreatedAtUtc, created.UpdatedAtUtc);

        var entity = await context.PaymentMethodFeeRules.SingleAsync();
        Assert.Equal(ActingUserId, entity.CreatedByUserId);
        Assert.Equal(ActingUserId, entity.UpdatedByUserId);
    }

    [Fact]
    public async Task CreateAsync_FixedRule_PersistsOnlyTheFixedComponent()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        var created = await service.CreateAsync(
            CreateRequest(PaymentMethodFeeType.Fixed, percentage: null, fixedAmount: 3.50m),
            ActingUserId);

        Assert.Equal(PaymentMethodFeeType.Fixed, created.FeeType);
        Assert.Null(created.Percentage);
        Assert.Equal(3.50m, created.FixedAmount);
    }

    [Fact]
    public async Task CreateAsync_PercentagePlusFixedRule_PersistsBothComponents()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        var created = await service.CreateAsync(
            CreateRequest(PaymentMethodFeeType.PercentagePlusFixed, percentage: 1.5m, fixedAmount: 0.25m),
            ActingUserId);

        Assert.Equal(PaymentMethodFeeType.PercentagePlusFixed, created.FeeType);
        Assert.Equal(1.5m, created.Percentage);
        Assert.Equal(0.25m, created.FixedAmount);
    }

    [Fact]
    public async Task CreateAsync_AcceptsTheLocalCurrencyAliasOfTheDualRegime()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        var created = await service.CreateAsync(
            CreateRequest(PaymentMethodFeeType.Percentage, currencyCode: "Bs.S"),
            ActingUserId);

        Assert.Equal("VES", created.CurrencyCode);
    }

    [Fact]
    public async Task GetAllAsync_FiltersByPaymentMethodAndActiveState()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);
        var activeCard = await service.CreateAsync(CreateRequest(), ActingUserId);
        var activeTransfer = await service.CreateAsync(
            CreateRequest(effectiveFromUtc: ValidFromUtc, paymentMethodId: TransferMethodId),
            ActingUserId);
        await service.DeleteAsync(activeTransfer.Id);

        var cardOnly = await service.GetAllAsync(CardMethodId, null);
        Assert.Single(cardOnly);
        Assert.Equal(activeCard.Id, cardOnly[0].Id);

        var activeOnly = await service.GetAllAsync(null, true);
        Assert.Single(activeOnly);
        Assert.Equal(activeCard.Id, activeOnly[0].Id);

        var all = await service.GetAllAsync(null, null);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, rule => rule.Id == activeTransfer.Id && !rule.IsActive);

        var inactiveOnly = await service.GetAllAsync(null, false);
        Assert.Equal(2, inactiveOnly.Count);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsTheRuleAndThrowsForMissingIds()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);
        var created = await service.CreateAsync(CreateRequest(), ActingUserId);

        var fetched = await service.GetByIdAsync(created.Id);
        Assert.Equal(created.Id, fetched.Id);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetByIdAsync(9999));
    }

    [Fact]
    public async Task UpdateAsync_ChangesAmountsAndAudit_ButKeepsCreationAudit()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);
        var created = await service.CreateAsync(CreateRequest(), ActingUserId);

        var updated = await service.UpdateAsync(
            created.Id,
            new UpdatePaymentMethodFeeRuleRequest
            {
                FeeType = PaymentMethodFeeType.PercentagePlusFixed,
                Percentage = 1.75m,
                FixedAmount = 0.40m,
                CurrencyCode = "USD",
                EffectiveFromUtc = ValidFromUtc,
                EffectiveToUtc = ValidToUtc,
                IsActive = true
            },
            SecondActingUserId);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(CardMethodId, updated.PaymentMethodId);
        Assert.Equal(PaymentMethodFeeType.PercentagePlusFixed, updated.FeeType);
        Assert.Equal(1.75m, updated.Percentage);
        Assert.Equal(0.40m, updated.FixedAmount);
        Assert.Equal(ValidToUtc, updated.EffectiveToUtc);
        Assert.True(updated.UpdatedAtUtc >= updated.CreatedAtUtc);

        var entity = await context.PaymentMethodFeeRules.SingleAsync(rule => rule.Id == created.Id);
        Assert.Equal(ActingUserId, entity.CreatedByUserId);
        Assert.Equal(SecondActingUserId, entity.UpdatedByUserId);
    }

    [Fact]
    public async Task UpdateAsync_OverlappingAnotherActiveRule_Throws()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);
        await service.CreateAsync(
            CreateRequest(effectiveFromUtc: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            ActingUserId);
        var earlier = await service.CreateAsync(
            CreateRequest(effectiveFromUtc: ValidFromUtc, effectiveToUtc: ValidToUtc),
            ActingUserId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(
            earlier.Id,
            new UpdatePaymentMethodFeeRuleRequest
            {
                FeeType = PaymentMethodFeeType.Percentage,
                Percentage = 1m,
                CurrencyCode = "USD",
                EffectiveFromUtc = ValidFromUtc,
                EffectiveToUtc = null,
                IsActive = true
            },
            SecondActingUserId));

        Assert.Contains("solapa", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_PerformsASoftDeleteAndKeepsTheRow()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);
        var created = await service.CreateAsync(CreateRequest(), ActingUserId);

        await service.DeleteAsync(created.Id);

        var entity = await context.PaymentMethodFeeRules.SingleAsync(rule => rule.Id == created.Id);
        Assert.False(entity.IsActive);
        Assert.True(entity.UpdatedAtUtc >= created.CreatedAtUtc);

        var all = await service.GetAllAsync(null, null);
        Assert.Single(all);
        Assert.False(all[0].IsActive);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync(9999));
    }

    [Fact]
    public async Task CreateAsync_RejectsCashMethod()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            CreateRequest(paymentMethodId: CashMethodId),
            ActingUserId));

        Assert.Contains("efectivo", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.PaymentMethodFeeRules);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingOrDeletedMethod()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateAsync(
            CreateRequest(paymentMethodId: 9999),
            ActingUserId));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateAsync(
            CreateRequest(paymentMethodId: DeletedMethodId),
            ActingUserId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    public async Task CreateAsync_RejectsPercentageOutsideZeroHundred(decimal percentage)
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            CreateRequest(PaymentMethodFeeType.Percentage, percentage: percentage),
            ActingUserId));

        Assert.Contains("porcentaje", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsNegativeFixedAmount()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            CreateRequest(PaymentMethodFeeType.Fixed, percentage: null, fixedAmount: -1m),
            ActingUserId));

        Assert.Contains("monto fijo", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsIncoherentEffectiveRange()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            CreateRequest(effectiveFromUtc: ValidToUtc, effectiveToUtc: ValidFromUtc),
            ActingUserId));

        Assert.Contains("vigencia", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsCurrencyOutsideUsdAndLocal()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            CreateRequest(currencyCode: "EUR"),
            ActingUserId));

        Assert.Contains("moneda", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsOverlappingActiveRule()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);
        await service.CreateAsync(
            CreateRequest(effectiveFromUtc: ValidFromUtc),
            ActingUserId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            CreateRequest(effectiveFromUtc: new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)),
            ActingUserId));

        Assert.Contains("solapa", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_AllowsAdjacentWindows()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context);
        await service.CreateAsync(
            CreateRequest(effectiveFromUtc: ValidFromUtc, effectiveToUtc: ValidToUtc),
            ActingUserId);

        var adjacent = await service.CreateAsync(
            CreateRequest(effectiveFromUtc: ValidToUtc),
            ActingUserId);

        Assert.True(adjacent.IsActive);
        Assert.Equal(2, context.PaymentMethodFeeRules.Count());
    }

    private static CreatePaymentMethodFeeRuleRequest CreateRequest(
        PaymentMethodFeeType feeType = PaymentMethodFeeType.Percentage,
        decimal? percentage = 1m,
        decimal? fixedAmount = null,
        string currencyCode = "USD",
        DateTime? effectiveFromUtc = null,
        DateTime? effectiveToUtc = null,
        int paymentMethodId = CardMethodId) => new()
    {
        PaymentMethodId = paymentMethodId,
        FeeType = feeType,
        Percentage = percentage,
        FixedAmount = fixedAmount,
        CurrencyCode = currencyCode,
        EffectiveFromUtc = effectiveFromUtc ?? ValidFromUtc,
        EffectiveToUtc = effectiveToUtc,
        IsActive = true
    };

    private static PaymentMethodFeeRuleService CreateService(SalesDbContext context) =>
        new(context, CreateMonetaryProfile().Object);

    private static Mock<IMonetaryProfileService> CreateMonetaryProfile()
    {
        var profileService = new Mock<IMonetaryProfileService>();
        profileService
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "VES",
                SecondaryCurrencySymbol = "Bs.S"
            });
        return profileService;
    }

    private static async Task<SalesDbContext> CreateContextAsync()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = CashMethodId, Name = "Efectivo", IsCash = true, IsActive = true },
            new PaymentMethod { Id = CardMethodId, Name = "Tarjeta (Punto de Venta)", IsCash = false, IsActive = true },
            new PaymentMethod { Id = TransferMethodId, Name = "Transferencia", IsCash = false, IsActive = true },
            new PaymentMethod { Id = DeletedMethodId, Name = "Eliminado", IsCash = false, IsActive = false, IsDeleted = true });
        await context.SaveChangesAsync();
        return context;
    }
}
