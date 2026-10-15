using System.Text;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.177-T2 (S1/S2/S3): motor de consulta del Kardex — unión activa + archivo, filtros
/// combinables, paginación server-side, KPIs, derivación histórica de clasificación y
/// exportación CSV/XLSX/PDF con encabezado legal.
/// </summary>
public sealed class StockKardexServiceTests
{
    private const string CompanyName = "MI NEGOCIO C.A.";
    private const string ExportUser = "admin-kardex";

    private static StockKardexService CreateService(InventoryDbContext db, string? companyName = CompanyName, string? userId = ExportUser)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync("CompanyLegalName")).ReturnsAsync(companyName);
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(u => u.UserId).Returns(userId);
        return new StockKardexService(db, settings.Object, user.Object);
    }

    private static Product NewProduct(int id, string sku, string name) => new()
    {
        Id = id,
        SKU = sku,
        Name = name,
        CostPriceUSD = 1m,
        PriceUSD = 2m,
        IsActive = true
    };

    private static StockMovement NewMovement(
        int id,
        int productId,
        DateTime dateUtc,
        decimal delta,
        decimal newLevel,
        string reason,
        string? userId = null,
        StockMovementOriginType? origin = null,
        StockMovementReason? reasonCode = null,
        string? authorizedBy = null,
        decimal? unitCost = null,
        int? saleId = null,
        int? supplierInvoiceId = null,
        string? observation = null) => new()
    {
        Id = id,
        ProductId = productId,
        MovementDate = dateUtc,
        QuantityChange = delta,
        NewStockLevel = newLevel,
        Reason = reason,
        UserId = userId,
        OriginType = origin,
        ReasonCode = reasonCode,
        AuthorizedByUserId = authorizedBy,
        UnitCostUSD = unitCost,
        SaleId = saleId,
        SupplierInvoiceId = supplierInvoiceId,
        Observation = observation
    };

    private static StockMovementArchive NewArchive(
        int id,
        int originalMovementId,
        int productId,
        DateTime dateUtc,
        decimal delta,
        decimal newLevel,
        string reason,
        string? userId = null,
        StockMovementOriginType? origin = null,
        StockMovementReason? reasonCode = null,
        string? authorizedBy = null,
        decimal? unitCost = null,
        int? saleId = null,
        int? supplierInvoiceId = null,
        string? observation = null) => new()
    {
        Id = id,
        OriginalMovementId = originalMovementId,
        ProductId = productId,
        MovementDate = dateUtc,
        QuantityChange = delta,
        NewStockLevel = newLevel,
        Reason = reason,
        UserId = userId,
        OriginType = origin,
        ReasonCode = reasonCode,
        AuthorizedByUserId = authorizedBy,
        UnitCostUSD = unitCost,
        SaleId = saleId,
        SupplierInvoiceId = supplierInvoiceId,
        Observation = observation
    };

    private static DateTime Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetMovementsAsync_UnionActiveAndArchive_OrdersByDateDescendingWithSourceLabels()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), 5m, 15m, "Carga inicial", origin: StockMovementOriginType.System, reasonCode: StockMovementReason.InitialLoad),
            NewMovement(2, 1, Utc(2026, 10, 3), -2m, 13m, "Sale #10", saleId: 10, origin: StockMovementOriginType.System, reasonCode: StockMovementReason.Sale));
        db.StockMovements_Archive.Add(
            NewArchive(10, 5, 1, Utc(2026, 10, 2), -1m, 14m, "Ajuste Manual: Merma", origin: StockMovementOriginType.Manual, reasonCode: StockMovementReason.Damage));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetMovementsAsync(new StockKardexFilterDto(), 1, 25);

        Assert.Equal(3, result.TotalCount);
        Assert.False(result.HasMore);
        var rows = result.Items.ToList();

        Assert.Equal(new[] { "Active", "Archived", "Active" }, rows.Select(r => r.Source));
        Assert.Equal(new[] { 2, 5, 1 }, rows.Select(r => r.MovementId));
        Assert.Equal(10, rows[1].ArchivedId);
        Assert.Equal(5, rows[1].OriginalMovementId);
        Assert.Equal("SKU-1", rows[0].SKU);
        Assert.Equal("Producto Uno", rows[0].ProductName);
        Assert.Equal(10m, rows[2].StockBefore);
        Assert.Equal(5m, rows[2].QuantityChange);
        Assert.Equal(15m, rows[2].NewStockLevel);
        Assert.Equal(15m, rows[1].StockBefore);
    }

    [Fact]
    public async Task GetMovementsAsync_DateRange_IsInclusiveOnBothEnds()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), 1m, 1m, "Mov 1"),
            NewMovement(2, 1, Utc(2026, 10, 5), 1m, 2m, "Mov 2"),
            NewMovement(3, 1, Utc(2026, 10, 10), 1m, 3m, "Mov 3"));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetMovementsAsync(new StockKardexFilterDto
        {
            DateFromUtc = Utc(2026, 10, 5),
            DateToUtc = Utc(2026, 10, 10)
        }, 1, 25);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { 3, 2 }, result.Items.Select(r => r.MovementId));
    }

    [Fact]
    public async Task GetMovementsAsync_UserIdFilter_MatchesExecutorOrAuthorizer()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), -1m, 9m, "Mov ejecutor", userId: "cashier-1"),
            NewMovement(2, 1, Utc(2026, 10, 2), -1m, 8m, "Mov autorizador", userId: "cashier-2", authorizedBy: "supervisor-9"),
            NewMovement(3, 1, Utc(2026, 10, 3), -1m, 7m, "Mov ajeno", userId: "cashier-3"));
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var byExecutor = await service.GetMovementsAsync(new StockKardexFilterDto { UserId = "cashier-1" }, 1, 25);
        Assert.Equal(new[] { 1 }, byExecutor.Items.Select(r => r.MovementId));

        var byAuthorizer = await service.GetMovementsAsync(new StockKardexFilterDto { UserId = "supervisor-9" }, 1, 25);
        Assert.Equal(new[] { 2 }, byAuthorizer.Items.Select(r => r.MovementId));
    }

    [Fact]
    public async Task GetMovementsAsync_ProductAndDirectionFilters_Combine()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.AddRange(NewProduct(1, "SKU-1", "Producto Uno"), NewProduct(2, "SKU-2", "Producto Dos"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), 5m, 5m, "Entrada"),
            NewMovement(2, 1, Utc(2026, 10, 2), -3m, 2m, "Salida"),
            NewMovement(3, 2, Utc(2026, 10, 3), -1m, 9m, "Salida otro"));
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var outOnly = await service.GetMovementsAsync(new StockKardexFilterDto
        {
            ProductId = 1,
            Direction = StockKardexDirection.Out
        }, 1, 25);
        Assert.Equal(new[] { 2 }, outOnly.Items.Select(r => r.MovementId));

        var inOnly = await service.GetMovementsAsync(new StockKardexFilterDto
        {
            ProductId = 1,
            Direction = StockKardexDirection.In
        }, 1, 25);
        Assert.Equal(new[] { 1 }, inOnly.Items.Select(r => r.MovementId));
    }

    [Fact]
    public async Task GetMovementsAsync_ReasonFilter_UsesDerivedClassificationForLegacyRows()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), -1m, 9m, "Ajuste Manual: Merma"),
            NewMovement(2, 1, Utc(2026, 10, 2), -1m, 8m, "Texto desconocido"),
            NewMovement(3, 1, Utc(2026, 10, 3), -1m, 7m, "Otro texto", origin: StockMovementOriginType.Manual, reasonCode: StockMovementReason.Damage));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetMovementsAsync(new StockKardexFilterDto
        {
            ReasonCodes = new[] { StockMovementReason.Damage }
        }, 1, 25);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { 3, 1 }, result.Items.Select(r => r.MovementId));
        Assert.All(result.Items, r => Assert.Equal(StockMovementReason.Damage, r.ReasonCode));
    }

    [Fact]
    public async Task GetMovementsAsync_OriginFilter_UsesDerivedOrigin()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), -1m, 9m, "Ajuste Manual: Conteo físico"),
            NewMovement(2, 1, Utc(2026, 10, 2), 4m, 13m, "Carga inicial"),
            NewMovement(3, 1, Utc(2026, 10, 3), 2m, 15m, "Importación masiva IMP-1", origin: StockMovementOriginType.Import, reasonCode: StockMovementReason.ImportAdjustment));
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var manual = await service.GetMovementsAsync(new StockKardexFilterDto
        {
            OriginTypes = new[] { StockMovementOriginType.Manual }
        }, 1, 25);
        Assert.Equal(new[] { 1 }, manual.Items.Select(r => r.MovementId));
        Assert.Equal(StockMovementReason.PhysicalCountCorrection, manual.Items.Single().ReasonCode);

        var systemAndImport = await service.GetMovementsAsync(new StockKardexFilterDto
        {
            OriginTypes = new[] { StockMovementOriginType.System, StockMovementOriginType.Import }
        }, 1, 25);
        Assert.Equal(2, systemAndImport.TotalCount);
    }

    [Fact]
    public async Task GetMovementsAsync_Pagination_DefaultAndMaxClamp_AreStable()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        for (int i = 1; i <= 201; i++)
        {
            db.StockMovements.Add(NewMovement(i, 1, Utc(2026, 1, 1).AddMinutes(i), 1m, i, $"Mov {i}"));
        }
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // pageSize 0 → default 25.
        var defaultSize = await service.GetMovementsAsync(new StockKardexFilterDto(), 1, 0);
        Assert.Equal(201, defaultSize.TotalCount);
        Assert.Equal(25, defaultSize.Items.Count());
        Assert.True(defaultSize.HasMore);

        // pageSize 5000 → clamp 200; page 0 → página 1.
        var firstPage = await service.GetMovementsAsync(new StockKardexFilterDto(), 0, 5000);
        Assert.Equal(200, firstPage.Items.Count());
        Assert.True(firstPage.HasMore);

        var secondPage = await service.GetMovementsAsync(new StockKardexFilterDto(), 2, 5000);
        Assert.Single(secondPage.Items);
        Assert.Equal(1, secondPage.Items.Single().MovementId);
        Assert.False(secondPage.HasMore);
    }

    [Fact]
    public async Task GetSummaryAsync_ComputesEntriesLossesNetAndUnvalued()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), 10m, 10m, "Entrada valorizada", unitCost: 2.50m),
            NewMovement(2, 1, Utc(2026, 10, 2), -4m, 6m, "Salida valorizada", unitCost: 2.50m),
            NewMovement(3, 1, Utc(2026, 10, 3), -2m, 4m, "Salida sin costo"),
            NewMovement(4, 1, Utc(2026, 10, 4), 5m, 9m, "Entrada sin costo"),
            NewMovement(5, 1, Utc(2026, 10, 5), 3m, 12m, "Entrada valorizada 2", unitCost: 1.00m));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var summary = await service.GetSummaryAsync(new StockKardexFilterDto());

        Assert.Equal(5, summary.TotalMovements);
        Assert.Equal(12m, summary.NetVolume);
        Assert.Equal(28.00m, summary.EntriesValueUsd);
        Assert.Equal(10.00m, summary.LossesValueUsd);
        Assert.Equal(18.00m, summary.NetValueUsd);
        Assert.Equal(2, summary.UnvaluedMovements);
    }

    [Fact]
    public async Task GetSummaryAsync_WithDirectionAndReasonFilters_OnlyCountsMatchingRows()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), -4m, 6m, "Sale #20", saleId: 20, unitCost: 2.50m),
            NewMovement(2, 1, Utc(2026, 10, 2), -2m, 4m, "Ajuste Manual: Merma"),
            NewMovement(3, 1, Utc(2026, 10, 3), 5m, 9m, "Carga inicial", unitCost: 1.00m));
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var outOnly = await service.GetSummaryAsync(new StockKardexFilterDto
        {
            Direction = StockKardexDirection.Out
        });
        Assert.Equal(2, outOnly.TotalMovements);
        Assert.Equal(-6m, outOnly.NetVolume);
        Assert.Equal(0m, outOnly.EntriesValueUsd);
        Assert.Equal(10.00m, outOnly.LossesValueUsd);
        Assert.Equal(-10.00m, outOnly.NetValueUsd);
        Assert.Equal(1, outOnly.UnvaluedMovements);

        var damageOnly = await service.GetSummaryAsync(new StockKardexFilterDto
        {
            Direction = StockKardexDirection.Out,
            ReasonCodes = new[] { StockMovementReason.Damage }
        });
        Assert.Equal(1, damageOnly.TotalMovements);
        Assert.Equal(-2m, damageOnly.NetVolume);
        Assert.Equal(0m, damageOnly.LossesValueUsd);
        Assert.Equal(1, damageOnly.UnvaluedMovements);
    }

    [Fact]
    public void Classifier_SaleIdWithoutReturnKeywords_ClassifiesSystemSale()
    {
        var (origin, reason) = StockMovementKardexClassifier.Derive("Sale #77", 77);

        Assert.Equal(StockMovementOriginType.System, origin);
        Assert.Equal(StockMovementReason.Sale, reason);
    }

    [Fact]
    public void Classifier_SaleIdWithAnulacion_ClassifiesSystemReturn()
    {
        var (origin, reason) = StockMovementKardexClassifier.Derive("Devolución de venta #77", 77);

        Assert.Equal(StockMovementOriginType.System, origin);
        Assert.Equal(StockMovementReason.Return, reason);
    }

    [Theory]
    [InlineData("Merma", StockMovementReason.Damage)]
    [InlineData("Daño en empaque", StockMovementReason.Damage)]
    [InlineData("Producto deteriorado", StockMovementReason.Damage)]
    [InlineData("Vencimiento", StockMovementReason.Expiry)]
    [InlineData("Conteo físico", StockMovementReason.PhysicalCountCorrection)]
    [InlineData("Ajuste de inventario", StockMovementReason.PhysicalCountCorrection)]
    [InlineData("Devolución cliente", StockMovementReason.Return)]
    [InlineData("Compra mostrador", StockMovementReason.PurchaseEntry)]
    public void Classifier_ManualAdjustmentSuffix_MapsKeywords(string suffix, StockMovementReason expected)
    {
        var (origin, reason) = StockMovementKardexClassifier.Derive($"Ajuste Manual: {suffix}", null);

        Assert.Equal(StockMovementOriginType.Manual, origin);
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void Classifier_ManualAdjustmentWithoutKnownKeyword_KeepsOriginOnly()
    {
        var (origin, reason) = StockMovementKardexClassifier.Derive("Ajuste Manual: motivo libre", null);

        Assert.Equal(StockMovementOriginType.Manual, origin);
        Assert.Null(reason);
    }

    [Fact]
    public void Classifier_CargaInicial_ClassifiesSystemInitialLoad()
    {
        var (origin, reason) = StockMovementKardexClassifier.Derive("Carga inicial", null);

        Assert.Equal(StockMovementOriginType.System, origin);
        Assert.Equal(StockMovementReason.InitialLoad, reason);
    }

    [Theory]
    [InlineData("Variante: Harina 5Kg (SKU: HAR-5, Factor: 5) | Sale #123", StockMovementOriginType.System, StockMovementReason.Sale)]
    [InlineData("Variante: Harina 5Kg (SKU: HAR-5, Factor: 5) | Venta al detal", StockMovementOriginType.System, StockMovementReason.Sale)]
    [InlineData("Variante: Harina 5Kg (SKU: HAR-5, Factor: 5) | Ajuste Manual: Merma", StockMovementOriginType.Manual, StockMovementReason.Damage)]
    public void Classifier_VariantPrefix_ClassifiesPosteriorReason(
        string reason,
        StockMovementOriginType expectedOrigin,
        StockMovementReason expectedReason)
    {
        var (origin, derivedReason) = StockMovementKardexClassifier.Derive(reason, null);

        Assert.Equal(expectedOrigin, origin);
        Assert.Equal(expectedReason, derivedReason);
    }

    [Fact]
    public void Classifier_ImportReason_ClassifiesImportAdjustment()
    {
        var (origin, reason) = StockMovementKardexClassifier.Derive("Importación masiva IMP-20261013-120000", null);

        Assert.Equal(StockMovementOriginType.Import, origin);
        Assert.Equal(StockMovementReason.ImportAdjustment, reason);
    }

    [Fact]
    public void Classifier_UnknownReason_ReturnsUnclassified()
    {
        var (origin, reason) = StockMovementKardexClassifier.Derive("Confirmed Reservation 5: reserva", null);

        Assert.Null(origin);
        Assert.Null(reason);
    }

    [Fact]
    public void Classifier_PersistedValues_WinOverDerivation()
    {
        var (origin, reason) = StockMovementKardexClassifier.Resolve(
            "Sale #77", 77, StockMovementOriginType.Manual, StockMovementReason.Damage);

        Assert.Equal(StockMovementOriginType.Manual, origin);
        Assert.Equal(StockMovementReason.Damage, reason);
    }

    [Fact]
    public void Classifier_PersistedOriginOnly_DerivesMissingReason()
    {
        var (origin, reason) = StockMovementKardexClassifier.Resolve(
            "Carga inicial", null, StockMovementOriginType.System, null);

        Assert.Equal(StockMovementOriginType.System, origin);
        Assert.Equal(StockMovementReason.InitialLoad, reason);
    }

    [Fact]
    public void Classifier_ManualPrefixWinsOverGenericKeywords()
    {
        // Fix verificación independiente 8.177-T2: "Ajuste Manual: merma importante" clasificaba
        // como Import/ImportAdjustment por el substring "importa"; el prefijo manual es la señal
        // más específica y debe evaluarse antes de los keywords genéricos.
        var (origin, reason) = StockMovementKardexClassifier.Derive("Ajuste Manual: merma importante", null);
        Assert.Equal(StockMovementOriginType.Manual, origin);
        Assert.Equal(StockMovementReason.Damage, reason);

        (origin, reason) = StockMovementKardexClassifier.Derive("Ajuste Manual: se devolvió al comprador", null);
        Assert.Equal(StockMovementOriginType.Manual, origin);
        Assert.Equal(StockMovementReason.Return, reason);

        (origin, reason) = StockMovementKardexClassifier.Derive("Ajuste Manual: compra de emergencia", null);
        Assert.Equal(StockMovementOriginType.Manual, origin);
        Assert.Equal(StockMovementReason.PurchaseEntry, reason);
    }

    [Fact]
    public void Classifier_GenericKeywords_DoNotMatchInsideUnrelatedWords()
    {
        var (origin, reason) = StockMovementKardexClassifier.Derive("Lote importante sin factura", null);
        Assert.Null(origin);
        Assert.Null(reason);

        // "compra" es palabra completa: "comprador" no debe clasificar como Entrada por Compra.
        (origin, reason) = StockMovementKardexClassifier.Derive("Ajuste Manual: comprador habitual", null);
        Assert.Equal(StockMovementOriginType.Manual, origin);
        Assert.Null(reason);
    }

    [Fact]
    public async Task GetMovementsAsync_OnRelationalProvider_TranslatesUnionFiltersAndPagination()
    {
        // Fix verificación independiente 8.177-T2: smoke relacional (SQLite) de la traducción del
        // Concat activa+archivo con filtros y paginación — cubre el riesgo CI de que el UNION ALL
        // no traduzca en un proveedor SQL real (más cercano a Npgsql que InMemory).
        var (db, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using var _ = db;
        using var __ = connection;

        db.Products.Add(NewProduct(1, "SQL-1", "Producto SQLite"));
        db.StockMovements.Add(NewMovement(1, 1, Utc(2026, 10, 10), -2m, 8m, "Sale #91", saleId: 91));
        db.StockMovements.Add(NewMovement(2, 1, Utc(2026, 10, 12), -1m, 7m, "Sale #92", saleId: 92));
        db.StockMovements_Archive.Add(NewArchive(1, 50, 1, Utc(2026, 9, 1), 3m, 10m, "Carga inicial"));
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var page = await service.GetMovementsAsync(new StockKardexFilterDto(), page: 1, pageSize: 2);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count());
        Assert.Equal(new[] { "Active", "Active" }, page.Items.Select(row => row.Source).ToArray());

        var filtered = await service.GetMovementsAsync(
            new StockKardexFilterDto { DateToUtc = Utc(2026, 9, 30) },
            page: 1,
            pageSize: 10);

        var archived = Assert.Single(filtered.Items);
        Assert.Equal("Archived", archived.Source);
        Assert.Equal(50, archived.MovementId);
    }

    [Fact]
    public async Task ExportAsync_Csv_ContainsLegalHeaderFilterSummaryAndRows()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), -1m, 9m, "Ajuste Manual: Merma", userId: "cashier-1"),
            NewMovement(2, 1, Utc(2026, 10, 2), -2m, 7m, "Sale #30", saleId: 30, userId: "cashier-1"));
        db.StockMovements_Archive.Add(
            NewArchive(10, 5, 1, Utc(2026, 9, 30), -1m, 10m, "Sale #29", saleId: 29, userId: "cashier-1"));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var bytes = await service.ExportAsync(new StockKardexFilterDto
        {
            ProductId = 1,
            Direction = StockKardexDirection.Out
        }, "csv");

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("KARDEX OFICIAL DE MOVIMIENTOS DE INVENTARIO", text);
        Assert.Contains(CompanyName, text);
        Assert.Contains(ExportUser, text);
        Assert.Contains("Producto ID: 1", text);
        Assert.Contains("Sentido: Out", text);
        Assert.Contains("SKU-1", text);
        Assert.Contains("Ajuste Manual: Merma", text);
        Assert.Contains("Activo", text);
        Assert.Contains("Archivo", text);
    }

    [Fact]
    public async Task ExportAsync_Csv_WithoutCompanySetting_FallsBackToDefaultLegalName()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.Add(NewMovement(1, 1, Utc(2026, 10, 1), 1m, 1m, "Carga inicial"));
        await db.SaveChangesAsync();

        var service = CreateService(db, companyName: null);
        var bytes = await service.ExportAsync(new StockKardexFilterDto(), "csv");

        Assert.Contains("COMMANDCENTER POS", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task ExportAsync_Xlsx_ContainsLegalHeaderAndNumericRows()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.Add(NewMovement(1, 1, Utc(2026, 10, 1), -3m, 7m, "Sale #31", saleId: 31, unitCost: 1.2345m));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var bytes = await service.ExportAsync(new StockKardexFilterDto(), "xlsx");

        using var stream = new MemoryStream(bytes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var worksheet = workbook.Worksheet("Kardex");

        Assert.Equal("KARDEX OFICIAL DE MOVIMIENTOS DE INVENTARIO", worksheet.Cell(1, 1).GetString());
        Assert.Equal(CompanyName, worksheet.Cell(2, 2).GetString());
        Assert.Equal("Fecha UTC", worksheet.Cell(8, 1).GetString());
        Assert.Equal("SKU-1", worksheet.Cell(9, 5).GetString());
        Assert.Equal(-3m, worksheet.Cell(9, 8).GetValue<decimal>());
        Assert.Equal(-3.70m, worksheet.Cell(9, 14).GetValue<decimal>());
    }

    [Fact]
    public async Task ExportAsync_Pdf_StartsWithPdfMagicAndContainsTitleCompanyAndRows()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        db.StockMovements.AddRange(
            NewMovement(1, 1, Utc(2026, 10, 1), -1m, 9m, "Ajuste Manual: Merma"),
            NewMovement(2, 1, Utc(2026, 10, 2), -2m, 7m, "Sale #32", saleId: 32));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var bytes = await service.ExportAsync(new StockKardexFilterDto(), "pdf");

        var text = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("%PDF", text);
        Assert.Contains("KARDEX OFICIAL DE MOVIMIENTOS DE INVENTARIO", text);
        Assert.Contains(CompanyName, text);
        Assert.Contains("SKU-1", text);
        Assert.Contains("gina 1 de 1", text);

        // Estructura: startxref apunta a la tabla xref y cada entrada a su objeto real.
        int startXrefKeyword = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var startXrefValue = long.Parse(
            text[(startXrefKeyword + "startxref".Length)..].Trim().Split('\n')[0].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("xref", text.Substring((int)startXrefValue, 4));

        var xrefLines = text[(int)startXrefValue..].Split('\n');
        int totalEntries = int.Parse(
            xrefLines[1].Trim().Split(' ')[1],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(totalEntries > 1);
        for (int objectNumber = 1; objectNumber < totalEntries; objectNumber++)
        {
            var entry = xrefLines[2 + objectNumber].TrimEnd('\r');
            var objectOffset = long.Parse(
                entry[..10],
                System.Globalization.CultureInfo.InvariantCulture);
            var expectedHeader = $"{objectNumber} 0 obj";
            Assert.Equal(expectedHeader, text.Substring((int)objectOffset, expectedHeader.Length));
        }
    }

    [Fact]
    public async Task ExportAsync_Pdf_WithManyRows_PaginatesAndNumbersPages()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        db.Products.Add(NewProduct(1, "SKU-1", "Producto Uno"));
        for (int i = 1; i <= 30; i++)
        {
            db.StockMovements.Add(NewMovement(i, 1, Utc(2026, 1, 1).AddMinutes(i), 1m, i, $"Mov {i}"));
        }
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var bytes = await service.ExportAsync(new StockKardexFilterDto(), "pdf");

        var text = Encoding.ASCII.GetString(bytes);
        Assert.Contains("gina 1 de 2", text);
        Assert.Contains("gina 2 de 2", text);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportAsync_UnsupportedFormat_ThrowsArgumentException(string format)
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(new StockKardexFilterDto(), format));
    }
}
