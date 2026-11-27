using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// P0 (T1/S3): contrato de esquema del gasto operativo mensual recurrente. Verifica la entidad,
/// la precision monetaria, los indices de vigencia y la relacion opcional con el medio de pago.
/// </summary>
public class OperatingExpenseSchemaTests
{
    private const string EntityName = "Sales.Module.Entities.OperatingExpense";

    [Fact]
    public void OperatingExpense_HasTheMonthlyContractProperties()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(EntityName);
        Assert.NotNull(entity);

        foreach (var propertyName in new[]
                 {
                     "Id",
                     "Description",
                     "MonthlyAmountUsd",
                     "MonthlyAmountLocal",
                     "LocalCurrencyCode",
                     "PaymentMethodId",
                     "EffectiveFromDate",
                     "EffectiveToDate",
                     "IsActive",
                     "CreatedAtUtc",
                     "UpdatedAtUtc"
                 })
        {
            Assert.NotNull(entity!.FindProperty(propertyName));
        }

        Assert.Equal("decimal(18,2)", entity!.FindProperty("MonthlyAmountUsd")!.GetColumnType());
        Assert.Equal("decimal(18,2)", entity.FindProperty("MonthlyAmountLocal")!.GetColumnType());
        Assert.Equal(100, entity.FindProperty("Description")!.GetMaxLength());
        Assert.Equal(10, entity.FindProperty("LocalCurrencyCode")!.GetMaxLength());
    }

    [Fact]
    public void OperatingExpense_HasActiveDateAndPaymentMethodIndexes()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(EntityName);
        Assert.NotNull(entity);

        Assert.Contains(entity!.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(new[] { "EffectiveFromDate", "IsActive" }, StringComparer.Ordinal));

        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Count == 1 && index.Properties[0].Name == "PaymentMethodId");
    }

    [Fact]
    public void OperatingExpense_PaymentMethodForeignKeyIsOptionalAndRestrict()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(EntityName);
        Assert.NotNull(entity);

        var foreignKey = Assert.Single(entity!.GetForeignKeys());
        Assert.False(foreignKey.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal("PaymentMethods", foreignKey.PrincipalEntityType.GetTableName());
    }

    private static SalesDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        return new SalesDbContext(options);
    }
}
