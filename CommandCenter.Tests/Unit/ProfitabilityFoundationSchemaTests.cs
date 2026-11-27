using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F1-WU1): contrato de esquema de la base de rentabilidad. Usa busquedas por string
/// (no tipos CLR) para que la guarda compile antes de que existan las entidades nuevas y falle
/// en RED si el modelo pierde el discriminador de venta, los snapshots de costo/comision o las
/// reglas de comision por metodo de pago.
/// </summary>
public class ProfitabilityFoundationSchemaTests
{
    private const string SaleEntity = "Sales.Module.Entities.Sale";
    private const string SaleItemEntity = "Sales.Module.Entities.SaleItem";
    private const string SalePaymentEntity = "Sales.Module.Entities.SalePayment";
    private const string ReceivablePaymentEntity = "Sales.Module.Entities.ReceivablePayment";
    private const string PaymentMethodFeeRuleEntity = "Sales.Module.Entities.PaymentMethodFeeRule";
    private const string CreditReturnEntity = "Sales.Module.Entities.CreditReturn";

    [Fact]
    public void Sale_HasProfitabilityFoundationProperties()
    {
        using var context = CreateContext();
        AssertHasProperties(context, SaleEntity,
            "SaleType",
            "CommissionAmountUSD",
            "CommissionAmountLocal",
            "CancelledAtUtc");
    }

    [Fact]
    public void SaleItem_HasUnitCostLocalSnapshot()
    {
        using var context = CreateContext();
        AssertHasProperties(context, SaleItemEntity, "UnitCostLocal");
    }

    [Fact]
    public void SalePayment_HasPaymentMethodFeeSnapshotProperties()
    {
        using var context = CreateContext();
        AssertHasProperties(context, SalePaymentEntity, FeeSnapshotProperties);
    }

    [Fact]
    public void ReceivablePayment_HasPaymentMethodFeeSnapshotProperties()
    {
        using var context = CreateContext();
        AssertHasProperties(context, ReceivablePaymentEntity, FeeSnapshotProperties);
    }

    [Fact]
    public void PaymentMethodFeeRule_ExistsWithConfiguredVigencyAndAmounts()
    {
        using var context = CreateContext();
        AssertHasProperties(context, PaymentMethodFeeRuleEntity,
            "PaymentMethodId",
            "FeeType",
            "Percentage",
            "FixedAmount",
            "CurrencyCode",
            "EffectiveFromUtc",
            "EffectiveToUtc",
            "IsActive");
    }

    [Fact]
    public void Sale_HasCancelledAtUtcIndex()
    {
        using var context = CreateContext();
        var sale = context.Model.FindEntityType(SaleEntity);
        Assert.NotNull(sale);
        Assert.Contains(sale!.GetIndexes(), index =>
            index.Properties.Any(property => property.Name == "CancelledAtUtc"));
    }

    [Fact]
    public void CreditReturn_HasCreatedAtUtcIndex()
    {
        using var context = CreateContext();
        var creditReturn = context.Model.FindEntityType(CreditReturnEntity);
        Assert.NotNull(creditReturn);
        Assert.Contains(creditReturn!.GetIndexes(), index =>
            index.Properties.Any(property => property.Name == "CreatedAtUtc"));
    }

    [Fact]
    public void PaymentMethodFeeRule_HasUniqueIndexOnPaymentMethodAndEffectiveFromUtc()
    {
        using var context = CreateContext();
        var rule = context.Model.FindEntityType(PaymentMethodFeeRuleEntity);
        Assert.NotNull(rule);
        var uniqueIndex = rule!.GetIndexes().SingleOrDefault(index =>
            index.IsUnique &&
            index.Properties.Count == 2 &&
            index.Properties.Any(property => property.Name == "PaymentMethodId") &&
            index.Properties.Any(property => property.Name == "EffectiveFromUtc"));
        Assert.NotNull(uniqueIndex);
    }

    [Fact]
    public void CostAndFeeSnapshots_KeepTheApprovedPrecision()
    {
        using var context = CreateContext();

        AssertColumnType(context, SaleEntity, "CommissionAmountUSD", "decimal(18,4)");
        AssertColumnType(context, SaleEntity, "CommissionAmountLocal", "decimal(18,2)");
        AssertColumnType(context, SaleItemEntity, "UnitCostLocal", "decimal(18,4)");

        foreach (var paymentEntity in new[] { SalePaymentEntity, ReceivablePaymentEntity })
        {
            AssertColumnType(context, paymentEntity, "FeePercentage", "decimal(9,4)");
            AssertColumnType(context, paymentEntity, "FeeFixedAmount", "decimal(18,2)");
            AssertColumnType(context, paymentEntity, "FeeAmountUsd", "decimal(18,4)");
            AssertColumnType(context, paymentEntity, "FeeAmountLocal", "decimal(18,2)");
            AssertColumnType(context, paymentEntity, "FeeRate", "decimal(18,4)");
        }

        AssertColumnType(context, PaymentMethodFeeRuleEntity, "Percentage", "decimal(9,4)");
        AssertColumnType(context, PaymentMethodFeeRuleEntity, "FixedAmount", "decimal(18,2)");
    }

    private static readonly string[] FeeSnapshotProperties =
    {
        "PaymentMethodFeeRuleId",
        "FeeType",
        "FeePercentage",
        "FeeFixedAmount",
        "FeeCurrencyCode",
        "FeeAmountUsd",
        "FeeAmountLocal",
        "FeeRate",
        "FeeConfigured",
        "FeeRuleEffectiveFromUtc"
    };

    private static SalesDbContext CreateContext()
    {
        // SQLite (relacional) expone GetColumnType() para validar la precision acordada; el
        // modelo es el mismo que usa PostgreSQL y no abre conexion para leer metadata.
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        return new SalesDbContext(options);
    }

    private static void AssertHasProperties(SalesDbContext context, string entityName, params string[] propertyNames)
    {
        var entity = context.Model.FindEntityType(entityName);
        Assert.True(entity is not null, $"El modelo EF no contiene la entidad '{entityName}'.");

        var missing = new List<string>();
        foreach (var propertyName in propertyNames)
        {
            if (entity!.FindProperty(propertyName) is null)
            {
                missing.Add(propertyName);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Faltan propiedades en '{entityName}': {string.Join(", ", missing)}");
    }

    private static void AssertColumnType(SalesDbContext context, string entityName, string propertyName, string expected)
    {
        var entity = context.Model.FindEntityType(entityName);
        Assert.True(entity is not null, $"El modelo EF no contiene la entidad '{entityName}'.");

        var property = entity!.FindProperty(propertyName);
        Assert.True(property is not null, $"El modelo EF no contiene '{entityName}.{propertyName}'.");
        Assert.Equal(expected, property!.GetColumnType());
    }
}
