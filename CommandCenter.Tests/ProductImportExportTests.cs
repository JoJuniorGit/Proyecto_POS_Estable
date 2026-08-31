using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests;

public class ProductImportExportTests
{
    private InventoryDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task BulkImport_AppliesCeilingRounding_WhenCostAndMarginProvided()
    {
        var db = CreateInMemoryDbContext();
        var service = new InventoryService(db);

        // Cost = 10.05, Margin = 33.33% => 10.05 * 1.3333 = 13.399665 => Math.Ceiling(*100)/100 = 13.40
        var importList = new List<ProductImportDto>
        {
            new ProductImportDto
            {
                SKU = "100035",
                Name = "Test Ceiling Product",
                CostPriceUSD = 10.05m,
                ProfitMarginRetail = 33.33m,
                PriceRetailUSD = 0m, // Calculated
                IsValid = true
            }
        };

        var (added, updated) = await service.BulkImportProductsAsync(importList, overwriteMerge: false);

        Assert.Equal(1, added);
        var product = await db.Products.FirstOrDefaultAsync(p => p.SKU == "100035");
        Assert.NotNull(product);
        Assert.Equal(13.40m, product!.PriceRetailUSD);
    }

    [Fact]
    public async Task BulkImport_CalculatesRetailAndWholesalePrices_FromCostAndMargin()
    {
        var db = CreateInMemoryDbContext();
        var service = new InventoryService(db);

        // Cost = 10.00, Retail Margin = 30%, Wholesale Margin = 20%
        var importList = new List<ProductImportDto>
        {
            new ProductImportDto
            {
                SKU = "100063",
                Name = "Test Calculated Price Product",
                CostPriceUSD = 10.00m,
                ProfitMarginRetail = 30.00m,
                ProfitMarginWholesale = 20.00m,
                HasWholesale = true,
                IsValid = true
            }
        };

        var (added, updated) = await service.BulkImportProductsAsync(importList, overwriteMerge: false);

        Assert.Equal(1, added);
        var product = await db.Products.FirstOrDefaultAsync(p => p.SKU == "100063");
        Assert.NotNull(product);
        Assert.Equal(13.00m, product!.PriceRetailUSD);
        Assert.Equal(12.00m, product.PriceWholesaleUSD);
    }

    [Fact]
    public async Task BulkImport_ForcesWholesaleEqualsRetail_WhenHasWholesaleIsFalse()
    {
        var db = CreateInMemoryDbContext();
        var service = new InventoryService(db);

        var importList = new List<ProductImportDto>
        {
            new ProductImportDto
            {
                SKU = "100092",
                Name = "No Wholesale Product",
                CostPriceUSD = 10.00m,
                ProfitMarginRetail = 30.00m,
                HasWholesale = false,
                PriceWholesaleUSD = 5.00m, // Should be overridden
                MinWholesaleQuantity = 10.000m, // Should be forced to 0
                IsValid = true
            }
        };

        var (added, updated) = await service.BulkImportProductsAsync(importList, overwriteMerge: false);

        Assert.Equal(1, added);
        var product = await db.Products.FirstOrDefaultAsync(p => p.SKU == "100092");
        Assert.NotNull(product);
        Assert.False(product!.HasWholesale);
        Assert.Equal(product.PriceRetailUSD, product.PriceWholesaleUSD);
        Assert.Equal(0m, product.MinWholesaleQuantity);
    }

    [Fact]
    public async Task ExportProductsAsync_ReturnsCsvWith11HeaderColumns()
    {
        var db = CreateInMemoryDbContext();
        db.Products.Add(new Product
        {
            SKU = "100119",
            Name = "Export Test Product",
            Description = "Sample Desc",
            CostPriceUSD = 5.00m,
            ProfitMarginRetail = 40.00m,
            PriceRetailUSD = 7.00m,
            ProfitMarginWholesale = 20.00m,
            PriceWholesaleUSD = 6.00m,
            MinWholesaleQuantity = 6m,
            HasWholesale = true,
            IsFractional = false,
            UnitOfMeasure = UnitOfMeasureType.Und,
            StockQuantity = 50,
            LowStockThreshold = 5,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var service = new InventoryService(db);
        var bytes = await service.ExportProductsAsync("csv", activeOnly: true);

        Assert.NotNull(bytes);
        var csvContent = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("SKU;Nombre;Descripción;CostoUSD;MargenDetal%;MargenMayor%;CantMinMayorista;HabilitarMayorista;EsFraccionable;StockActual;UmbralMinimo", csvContent);
        Assert.Contains("100119", csvContent);
    }

    [Fact]
    public async Task ExportProductsAsync_ReturnsXlsxWith11HeaderColumns()
    {
        var db = CreateInMemoryDbContext();
        db.Products.Add(new Product
        {
            SKU = "100152",
            Name = "Producto Excel Test",
            Description = "Probando exportacion XLSX",
            CostPriceUSD = 15.00m,
            ProfitMarginRetail = 30.00m,
            PriceRetailUSD = 19.50m,
            StockQuantity = 100,
            LowStockThreshold = 10,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var service = new InventoryService(db);
        var bytes = await service.ExportProductsAsync("xlsx", activeOnly: true);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        using var stream = new System.IO.MemoryStream(bytes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var worksheet = workbook.Worksheet("Productos");

        Assert.Equal("SKU", worksheet.Cell(1, 1).Value.ToString());
        Assert.Equal("Nombre", worksheet.Cell(1, 2).Value.ToString());
        Assert.Equal("Descripción", worksheet.Cell(1, 3).Value.ToString());
        Assert.Equal("UmbralMinimo", worksheet.Cell(1, 11).Value.ToString());
        Assert.Equal("100152", worksheet.Cell(2, 1).Value.ToString());
        Assert.Equal("Producto Excel Test", worksheet.Cell(2, 2).Value.ToString());
    }

    [Fact]
    public void MapHeaderToProperty_All11SpanishHeaders_MapsToValidPropertyWithoutIgnored()
    {
        var spanishHeaders = new[]
        {
            "SKU", "Nombre", "Descripción", "CostoUSD", "MargenDetal%",
            "MargenMayor%", "CantMinMayorista", "HabilitarMayorista",
            "EsFraccionable", "StockActual", "UmbralMinimo"
        };

        foreach (var header in spanishHeaders)
        {
            var mappedProperty = Desktop.Client.ViewModels.ImportProductsViewModel.MapHeaderToProperty(header);
            Assert.NotEqual("Ignore", mappedProperty);
            Assert.False(string.IsNullOrWhiteSpace(mappedProperty));
        }
    }

    [Fact]
    public async Task ParseFileWithMapping_SpanishHeaders_ParsesNameAndDataSuccessfully()
    {
        var tempFile = System.IO.Path.GetTempFileName() + ".csv";
        try
        {
            var csvLines = new[]
            {
                "SKU;Nombre;Descripción;CostoUSD;MargenDetal%;MargenMayor%;CantMinMayorista;HabilitarMayorista;EsFraccionable;StockActual;UmbralMinimo",
                "100226;Arroz Primor 1Kg;Arroz blanco de mesa;1.10;25.00;15.00;10.000;SI;SI;50;5"
            };
            await System.IO.File.WriteAllLinesAsync(tempFile, csvLines);

            var service = new Desktop.Client.Services.ProductImportService(new System.Net.Http.HttpClient());
            var mapping = new Dictionary<string, string>();
            var headers = csvLines[0].Split(';');
            foreach (var h in headers)
            {
                mapping[h] = Desktop.Client.ViewModels.ImportProductsViewModel.MapHeaderToProperty(h);
            }

            var list = await service.ParseFileWithMappingAsync(tempFile, mapping, 1.0m);

            Assert.Single(list);
            var item = list.First();
            Assert.True(item.IsValid, $"Validation error: {item.ErrorMessage}");
            Assert.Equal("100226", item.SKU);
            Assert.Equal("Arroz Primor 1Kg", item.Name);
            Assert.Equal("Arroz blanco de mesa", item.Description);
            Assert.Equal(1.10m, item.CostPriceUSD);
            Assert.Equal(1.38m, item.PriceRetailUSD); // Math.Ceiling(1.10 * 1.25 * 100) / 100 = 1.38
            Assert.Equal("Kg", item.UnitOfMeasure);
            Assert.Equal(50, item.StockQuantity);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseFileWithMapping_DerivesUnitOfMeasureFromFractional()
    {
        var tempFile = System.IO.Path.GetTempFileName() + ".csv";
        try
        {
            var csvLines = new[]
            {
                "SKU;Nombre;EsFraccionable",
                "7591001;Queso Molido;SI",
                "7591002;Jabon en Polvo;NO"
            };
            await System.IO.File.WriteAllLinesAsync(tempFile, csvLines);

            var service = new Desktop.Client.Services.ProductImportService(new System.Net.Http.HttpClient());
            var mapping = new Dictionary<string, string>
            {
                { "SKU", "SKU" },
                { "Nombre", "Name" },
                { "EsFraccionable", "IsFractional" }
            };

            var list = await service.ParseFileWithMappingAsync(tempFile, mapping, 1.0m);

            Assert.Equal(2, list.Count);
            Assert.Equal("Kg", list[0].UnitOfMeasure);
            Assert.Equal("Und", list[1].UnitOfMeasure);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseCsvWithMapping_ValidCsv_ExtractsRowsAndColumns()
    {
        var tempFile = System.IO.Path.GetTempFileName() + ".csv";
        try
        {
            var csvLines = new[]
            {
                "SKU;Name;Cost (USD);Retail Margin (%);Current Stock;Enable Wholesale",
                "7591003;Producto CSV Test;10.00;30.00;100;1"
            };
            await System.IO.File.WriteAllLinesAsync(tempFile, csvLines);

            var service = new Desktop.Client.Services.ProductImportService(new System.Net.Http.HttpClient());
            var mapping = new Dictionary<string, string>
            {
                { "SKU", "SKU" },
                { "Name", "Name" },
                { "Cost (USD)", "Cost (USD)" },
                { "Retail Margin (%)", "Retail Margin (%)" },
                { "Current Stock", "Current Stock" },
                { "Enable Wholesale", "Enable Wholesale" }
            };

            var list = await service.ParseFileWithMappingAsync(tempFile, mapping, 1.0m);

            Assert.Single(list);
            var item = list.First();
            Assert.True(item.IsValid);
            Assert.Equal("7591003", item.SKU);
            Assert.Equal("Producto CSV Test", item.Name);
            Assert.Equal(10.00m, item.CostPriceUSD);
            Assert.Equal(13.00m, item.PriceRetailUSD); // Ceiling rounding
            Assert.Equal(100, item.StockQuantity);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseCsvWithMapping_DuplicateHeaders_HandlesSafely()
    {
        var tempFile = System.IO.Path.GetTempFileName() + ".csv";
        try
        {
            var csvLines = new[]
            {
                "SKU;Name;SKU;Current Stock",
                "7591004;Producto Dup;7591004;50"
            };
            await System.IO.File.WriteAllLinesAsync(tempFile, csvLines);

            var service = new Desktop.Client.Services.ProductImportService(new System.Net.Http.HttpClient());
            var mapping = new Dictionary<string, string>
            {
                { "SKU", "SKU" },
                { "Name", "Name" },
                { "Current Stock", "Current Stock" }
            };

            var list = await service.ParseFileWithMappingAsync(tempFile, mapping, 1.0m);

            Assert.Single(list);
            Assert.Equal("7591004", list.First().SKU);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseCsvWithMapping_EmptyOrMalformedCsv_ReturnsControlledError()
    {
        var tempFile = System.IO.Path.GetTempFileName() + ".csv";
        try
        {
            await System.IO.File.WriteAllTextAsync(tempFile, string.Empty);

            var service = new Desktop.Client.Services.ProductImportService(new System.Net.Http.HttpClient());
            var mapping = new Dictionary<string, string>();

            var list = await service.ParseFileWithMappingAsync(tempFile, mapping, 1.0m);

            Assert.Empty(list);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ImportProductsViewModel_OnParsingError_CallsDialogServiceShowErrorAndResetsIsBusy()
    {
        var mockImportService = new MockProductImportServiceWithReadHeaderError();
        var mockExchange = new MockExchangeRateService();
        var userSession = new Desktop.Client.Services.UserSession
        {
            CurrentUser = new UserDto { Cedula = "V-12345678", Name = "Admin", Role = UserRole.Admin, IsActive = true }
        };
        var stubDialogService = new StubDialogService();

        var viewModel = new Desktop.Client.ViewModels.ImportProductsViewModel(mockImportService, mockExchange, userSession, stubDialogService);

        await viewModel.ProcessFileAsync("non_existent_invalid_file.csv");

        Assert.False(viewModel.IsBusy);
        Assert.True(stubDialogService.ShowErrorCalled);
        Assert.Equal("Error de Lectura", stubDialogService.LastTitle);
    }

    [Fact]
    public async Task ParseCsv_LongSku25Digits_IsValidTrue()
    {
        var tempFile = System.IO.Path.GetTempFileName() + ".csv";
        try
        {
            var longSku25 = "1234567890123456789012345";
            var csvLines = new[]
            {
                "SKU;Name;Cost (USD);Retail Margin (%)",
                $"{longSku25};Producto Largo 25 Digitos;10.00;30.00"
            };
            await System.IO.File.WriteAllLinesAsync(tempFile, csvLines);

            var service = new Desktop.Client.Services.ProductImportService(new System.Net.Http.HttpClient());
            var mapping = new Dictionary<string, string>
            {
                { "SKU", "SKU" },
                { "Name", "Name" },
                { "Cost (USD)", "Cost (USD)" },
                { "Retail Margin (%)", "Retail Margin (%)" }
            };

            var list = await service.ParseFileWithMappingAsync(tempFile, mapping, 1.0m);

            Assert.Single(list);
            var item = list.First();
            Assert.True(item.IsValid, $"Expected valid item for 25-digit SKU, but got error: {item.ErrorMessage}");
            Assert.Equal(longSku25, item.SKU);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseCsv_NonNumericSku_ReturnsValidationError()
    {
        var tempFile = System.IO.Path.GetTempFileName() + ".csv";
        try
        {
            var csvLines = new[]
            {
                "SKU;Name",
                "ABC-1234;Producto SKU Alfanumerico"
            };
            await System.IO.File.WriteAllLinesAsync(tempFile, csvLines);

            var service = new Desktop.Client.Services.ProductImportService(new System.Net.Http.HttpClient());
            var mapping = new Dictionary<string, string>
            {
                { "SKU", "SKU" },
                { "Name", "Name" }
            };

            var list = await service.ParseFileWithMappingAsync(tempFile, mapping, 1.0m);

            Assert.Single(list);
            var item = list.First();
            Assert.False(item.IsValid);
            Assert.Contains("entero", item.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseCsv_EmptyOrWhitespaceSku_ReturnsValidationError()
    {
        var tempFile = System.IO.Path.GetTempFileName() + ".csv";
        try
        {
            var csvLines = new[]
            {
                "SKU;Name",
                "   ;Producto SKU Vacio"
            };
            await System.IO.File.WriteAllLinesAsync(tempFile, csvLines);

            var service = new Desktop.Client.Services.ProductImportService(new System.Net.Http.HttpClient());
            var mapping = new Dictionary<string, string>
            {
                { "SKU", "SKU" },
                { "Name", "Name" }
            };

            var list = await service.ParseFileWithMappingAsync(tempFile, mapping, 1.0m);

            Assert.Single(list);
            var item = list.First();
            Assert.False(item.IsValid);
            Assert.Contains("no puede estar vacío", item.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }
}

internal class MockProductImportServiceWithReadHeaderError : Desktop.Client.Services.IProductImportService
{
    public Task<List<string>> ReadHeadersAsync(string filePath) => throw new System.IO.IOException("Simulated file read exception");
    public Task<List<ProductImportDto>> ParseFileWithMappingAsync(string filePath, Dictionary<string, string> columnMapping, decimal currentExchangeRate) => Task.FromResult(new List<ProductImportDto>());
    public Task<(int added, int updated)> CommitImportAsync(IEnumerable<ProductImportDto> products, bool overwriteMerge) => Task.FromResult((0, 0));
    public Task<string> GenerateTemplateAsync(string destinationPath) => Task.FromResult(destinationPath);
    public Task<string> ExportProductsToFileAsync(string destinationPath, bool activeOnly = true, string? filter = null) => Task.FromResult(destinationPath);
}

internal class MockExchangeRateService : Desktop.Client.Services.IExchangeRateService
{
    public decimal CurrentRate { get; set; } = 36.5m;
    public Task<(decimal Rate, DateTime? LastUpdated)> GetCurrentRateAsync() => Task.FromResult((CurrentRate, (DateTime?)DateTime.UtcNow));
    public Task SaveRateAsync(decimal rate) { CurrentRate = rate; return Task.CompletedTask; }
    public Task<List<Desktop.Client.Services.ExchangeRateHistoryDto>> GetHistoryAsync() => Task.FromResult(new List<Desktop.Client.Services.ExchangeRateHistoryDto>());
    public Task<(decimal Rate, DateTime? LastUpdated)> SyncBcvAsync() => Task.FromResult((CurrentRate, (DateTime?)DateTime.UtcNow));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal class StubDialogService : Desktop.Client.Services.IDialogService
{
    public bool HasOpenModalDialog => false;

    public bool ShowErrorCalled { get; private set; }
    public string LastTitle { get; private set; } = string.Empty;
    public string LastMessage { get; private set; } = string.Empty;

    public bool ShowConfirm(string title, string message) => false;
    public void ShowError(string title, string message)
    {
        ShowErrorCalled = true;
        LastTitle = title;
        LastMessage = message;
    }
    public void ShowWarning(string title, string message) { }
    public void ShowInfo(string title, string message) { }
    public Task<string?> ShowTextInputAsync(string prompt, string hint) => Task.FromResult<string?>(null);
    public Task<(bool success, string currentPassword, string newPassword)?> ShowChangePasswordDialogAsync() => Task.FromResult<(bool success, string currentPassword, string newPassword)?>(null);
    public decimal? ShowCashAdvanceDialog() => null;
    public void ShowSuccessDialog(string message) { }
    public Task<(bool success, decimal amount, string reason)?> ShowCashTransactionDialogAsync(string title) => Task.FromResult<(bool success, decimal amount, string reason)?>(null);
    public bool? ShowProductDialog(Desktop.Client.ViewModels.ProductDialogViewModel dialogVm) => false;
    public (bool success, decimal quantityChange, string reason) ShowAdjustStockDialog(Core.DTOs.ProductDto product) => (false, 0m, string.Empty);
    public void ShowInterruptedTransactionDialog(string title, string message) { }
    public Task<Core.DTOs.CustomerDto?> ShowCustomerPickerAsync() => Task.FromResult<Core.DTOs.CustomerDto?>(null);
    public Task<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?> ShowCashAdvanceRegisterDialogAsync(List<Desktop.Client.Services.PaymentMethodDto> paymentMethods, decimal availableCashLocal) => Task.FromResult<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?>(null);
    public Task<(bool confirmed, IEnumerable<Desktop.Client.Services.UpdateSaleItemDto>? modifiedItems)> ShowEditSaleDialogAsync(Core.DTOs.SaleDto sale, decimal exchangeRate) => Task.FromResult<(bool confirmed, IEnumerable<Desktop.Client.Services.UpdateSaleItemDto>? modifiedItems)>((false, null));
    public Task ShowPairingQrDialogAsync() => Task.CompletedTask;
    public Task<bool> ShowServerConnectionDialogAsync() => Task.FromResult(false);
}


