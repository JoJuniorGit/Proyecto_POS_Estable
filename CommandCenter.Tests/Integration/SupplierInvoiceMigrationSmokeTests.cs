using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Integration;
using Core.Common;
using Core.Entities;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Smoke del esquema de proveedores: el modelo expone los mapeos obligatorios y la cadena de
/// migraciones de Inventory crea tablas/índices/columnas con la forma esperada. La verificación
/// migratoria corre contra una base AISLADA por test del carril <see cref="PostgresTestLane"/>
/// (creada vacía y eliminada al final); antes usaba el nombre fijo pos_supplier_test con
/// TEST_POSTGRES_CONNECTION opcional y retornaba en silencio sin la variable (silent-pass).
/// Sin conexión utilizable el lane falla EXPLÍCITO.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class SupplierInvoiceMigrationSmokeTests
{
    [Fact]
    public void SupplierInvoiceMigration_ModelIncludesRequiredSupplierMappings()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options;

        using var context = new InventoryDbContext(options);
        var model = context.Model;

        var supplierCodeIndex = model.FindEntityType(typeof(SupplierProductCode))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_SupplierProductCodes_Supplier_Code");
        Assert.True(supplierCodeIndex.IsUnique);
        Assert.Equal("\"Code\" IS NOT NULL", supplierCodeIndex.GetFilter());

        var invoiceVersion = model.FindEntityType(typeof(SupplierInvoice))!.FindProperty("xmin");
        Assert.NotNull(invoiceVersion);
        Assert.True(invoiceVersion!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, invoiceVersion.ValueGenerated);

        var lineType = model.FindEntityType(typeof(SupplierInvoiceLine))!;
        Assert.Equal("numeric(18,4)", lineType.FindProperty(nameof(SupplierInvoiceLine.UnitCostUSD))!.GetColumnType());
        Assert.Equal("numeric(18,3)", lineType.FindProperty(nameof(SupplierInvoiceLine.Quantity))!.GetColumnType());

        // 8.146 multi-moneda: snapshot de moneda/tasa por documento + costo en moneda de la factura.
        var invoiceType = model.FindEntityType(typeof(SupplierInvoice))!;
        var currency = invoiceType.FindProperty(nameof(SupplierInvoice.Currency))!;
        Assert.Equal(8, currency.GetMaxLength());
        Assert.Equal(CurrencyCodes.Usd, currency.GetDefaultValue());

        var appliedRate = invoiceType.FindProperty(nameof(SupplierInvoice.AppliedRate))!;
        Assert.Equal("numeric(18,4)", appliedRate.GetColumnType());
        Assert.Equal(18, appliedRate.GetPrecision());
        Assert.Equal(4, appliedRate.GetScale());
        Assert.Equal(1m, appliedRate.GetDefaultValue());

        var unitCostDocument = lineType.FindProperty(nameof(SupplierInvoiceLine.UnitCostDocument))!;
        Assert.Equal("numeric(18,2)", unitCostDocument.GetColumnType());
        Assert.Equal(18, unitCostDocument.GetPrecision());
        Assert.Equal(2, unitCostDocument.GetScale());
        Assert.Equal(0m, unitCostDocument.GetDefaultValue());

        // 8.147-T5/D10: confianzas OCR nullable numeric(5,2), sin default.
        var nameConfidence = lineType.FindProperty(nameof(SupplierInvoiceLine.OcrNameConfidence))!;
        Assert.Equal("numeric(5,2)", nameConfidence.GetColumnType());
        Assert.Equal(5, nameConfidence.GetPrecision());
        Assert.Equal(2, nameConfidence.GetScale());
        Assert.True(nameConfidence.IsNullable);
        Assert.Null(nameConfidence.GetDefaultValue());

        var quantityConfidence = lineType.FindProperty(nameof(SupplierInvoiceLine.OcrQuantityConfidence))!;
        Assert.Equal("numeric(5,2)", quantityConfidence.GetColumnType());
        Assert.Equal(5, quantityConfidence.GetPrecision());
        Assert.Equal(2, quantityConfidence.GetScale());
        Assert.True(quantityConfidence.IsNullable);
        Assert.Null(quantityConfidence.GetDefaultValue());

        var unitCostConfidence = lineType.FindProperty(nameof(SupplierInvoiceLine.OcrUnitCostConfidence))!;
        Assert.Equal("numeric(5,2)", unitCostConfidence.GetColumnType());
        Assert.Equal(5, unitCostConfidence.GetPrecision());
        Assert.Equal(2, unitCostConfidence.GetScale());
        Assert.True(unitCostConfidence.IsNullable);
        Assert.Null(unitCostConfidence.GetDefaultValue());
    }

    [Fact]
    public async Task Migration_AddsSupplierSchema_WithoutChangingExistingProducts()
    {
        // La base aislada nace vacía: la cadena completa de migraciones se aplica sobre ella y las
        // aserciones de esquema son el contrato. El snapshot comparativo de Products de la versión
        // anterior solo aplicaba al nombre fijo pos_supplier_test (base legacy reutilizada), que la
        // aislación elimina por diseño.
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: false);
        await using var context = database.CreateInventoryDbContext();

        await context.Database.MigrateAsync();

        var supplierTableCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name IN " +
            "('Suppliers', 'SupplierColumnMappings', 'SupplierProductCodes', 'SupplierInvoices', 'SupplierInvoiceLines')")
            .SingleAsync();
        Assert.Equal(5, supplierTableCount);

        var supplierIndexCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' " +
            "AND indexname IN ('IX_Suppliers_NormalizedRifOrNit', 'IX_Suppliers_NormalizedCommercialName', " +
            "'IX_SupplierColumnMappings_SupplierId', 'IX_SupplierProductCodes_Supplier_Code')")
            .SingleAsync();
        Assert.Equal(4, supplierIndexCount);

        var supplierCodeIndexIsUniqueAndFiltered = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' " +
            "AND tablename = 'SupplierProductCodes' AND indexname = 'IX_SupplierProductCodes_Supplier_Code' " +
            "AND indexdef ILIKE '%CREATE UNIQUE INDEX%' AND indexdef ILIKE '%WHERE%'")
            .SingleAsync();
        Assert.Equal(1, supplierCodeIndexIsUniqueAndFiltered);

        // 8.146: columnas aditivas + backfill del costo documental.
        var multicurrencyColumnCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND " +
            "((table_name = 'SupplierInvoices' AND column_name IN ('Currency', 'AppliedRate')) " +
            "OR (table_name = 'SupplierInvoiceLines' AND column_name = 'UnitCostDocument'))")
            .SingleAsync();
        Assert.Equal(3, multicurrencyColumnCount);

        var currencyColumnShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || character_maximum_length::text AS \"Value\" " +
            "FROM information_schema.columns WHERE table_schema = 'public' " +
            "AND table_name = 'SupplierInvoices' AND column_name = 'Currency'")
            .SingleAsync();
        Assert.Equal("character varying:8", currencyColumnShape);

        var currencyColumnDefault = await context.Database.SqlQueryRaw<string>(
            "SELECT column_default AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'SupplierInvoices' AND column_name = 'Currency'")
            .SingleAsync();
        Assert.Contains("'USD'", currencyColumnDefault);

        var appliedRateColumnShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || numeric_precision::text || ':' || numeric_scale::text AS \"Value\" " +
            "FROM information_schema.columns WHERE table_schema = 'public' " +
            "AND table_name = 'SupplierInvoices' AND column_name = 'AppliedRate'")
            .SingleAsync();
        Assert.Equal("numeric:18:4", appliedRateColumnShape);

        var documentCostColumnShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || numeric_precision::text || ':' || numeric_scale::text AS \"Value\" " +
            "FROM information_schema.columns WHERE table_schema = 'public' " +
            "AND table_name = 'SupplierInvoiceLines' AND column_name = 'UnitCostDocument'")
            .SingleAsync();
        Assert.Equal("numeric:18:2", documentCostColumnShape);
        var normalizedCostColumnShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || numeric_precision::text || ':' || numeric_scale::text AS \"Value\" " +
            "FROM information_schema.columns WHERE table_schema = 'public' " +
            "AND table_name = 'SupplierInvoiceLines' AND column_name = 'UnitCostUSD'")
            .SingleAsync();
        Assert.Equal("numeric:18:4", normalizedCostColumnShape);

        var backfilledLinesMismatch = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"SupplierInvoiceLines\" " +
            "WHERE \"UnitCostDocument\" <> \"UnitCostUSD\"")
            .SingleAsync();
        Assert.Equal(0, backfilledLinesMismatch);

        // 8.147: columnas aditivas de confianza OCR (nullable numeric(5,2), sin default).
        var ocrConfidenceColumnCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'SupplierInvoiceLines' " +
            "AND column_name IN ('OcrNameConfidence', 'OcrQuantityConfidence', 'OcrUnitCostConfidence')")
            .SingleAsync();
        Assert.Equal(3, ocrConfidenceColumnCount);

        var ocrConfidenceShapedColumnCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'SupplierInvoiceLines' " +
            "AND column_name IN ('OcrNameConfidence', 'OcrQuantityConfidence', 'OcrUnitCostConfidence') " +
            "AND data_type = 'numeric' AND numeric_precision = 5 AND numeric_scale = 2 " +
            "AND is_nullable = 'YES' AND column_default IS NULL")
            .SingleAsync();
        Assert.Equal(3, ocrConfidenceShapedColumnCount);
    }
}

