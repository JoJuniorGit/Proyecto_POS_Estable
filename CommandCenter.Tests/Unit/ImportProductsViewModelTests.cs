using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ImportProductsViewModelTests
{
    [Fact]
    public void KpiFilters_ChangeVisibleRows_WithoutMutatingPreviewCollection()
    {
        var viewModel = CreateViewModel();
        var valid = CreateProduct("SKU-001", "Producto válido", isValid: true);
        var invalid = CreateProduct("SKU-002", "Producto con error", isValid: false);
        viewModel.ImportedProducts = new ObservableCollection<ProductImportDto> { valid, invalid };

        Assert.Equal(2, viewModel.TotalCount);
        Assert.Equal(1, viewModel.ValidCount);
        Assert.Equal(1, viewModel.ErrorCount);
        Assert.Equal(2, viewModel.FilteredProducts.Count);

        viewModel.SetFilterValidCommand.Execute(null);

        Assert.Single(viewModel.FilteredProducts);
        Assert.Same(valid, viewModel.FilteredProducts[0]);
        Assert.Equal(2, viewModel.ImportedProducts.Count);

        viewModel.SetFilterErrorsCommand.Execute(null);

        Assert.Single(viewModel.FilteredProducts);
        Assert.Same(invalid, viewModel.FilteredProducts[0]);

        viewModel.SetFilterAllCommand.Execute(null);

        Assert.Equal(2, viewModel.FilteredProducts.Count);
    }

    [Fact]
    public void Search_FiltersBySkuOrName_AndKeepsCurrentKpiFilter()
    {
        var viewModel = CreateViewModel();
        viewModel.ImportedProducts = new ObservableCollection<ProductImportDto>
        {
            CreateProduct("7591001", "Arroz Primor", isValid: true),
            CreateProduct("7591002", "Café Molido", isValid: true),
            CreateProduct("7591003", "Arroz con error", isValid: false)
        };

        viewModel.SearchQuery = "arroz";

        Assert.Equal(2, viewModel.FilteredProducts.Count);

        viewModel.SearchQuery = "7591002";

        Assert.Single(viewModel.FilteredProducts);
        Assert.Equal("7591002", viewModel.FilteredProducts[0].SKU);

        viewModel.SetFilterValidCommand.Execute(null);
        viewModel.SearchQuery = "arroz";

        Assert.Single(viewModel.FilteredProducts);
        Assert.True(viewModel.FilteredProducts[0].IsValid);
    }

    [Fact]
    public void ClearData_ResetsPreviewFiltersAndActiveFile()
    {
        var viewModel = CreateViewModel();
        viewModel.ImportedProducts = new ObservableCollection<ProductImportDto>
        {
            CreateProduct("SKU-001", "Producto", isValid: true)
        };
        viewModel.ActiveFileName = "productos.csv";
        viewModel.ActiveFilePath = @"C:\imports\productos.csv";
        viewModel.SearchQuery = "producto";
        viewModel.SelectedFilter = ImportFilterMode.Valid;

        viewModel.ClearDataCommand.Execute(null);

        Assert.Empty(viewModel.ImportedProducts);
        Assert.Empty(viewModel.FilteredProducts);
        Assert.False(viewModel.HasFileData);
        Assert.False(viewModel.HasActiveFile);
        Assert.Equal(string.Empty, viewModel.ActiveFileName);
        Assert.Equal(string.Empty, viewModel.ActiveFilePath);
        Assert.Equal(string.Empty, viewModel.SearchQuery);
        Assert.Equal(ImportFilterMode.All, viewModel.SelectedFilter);
        Assert.False(viewModel.IsImportCompleted);
    }

    [Fact]
    public void MergeToggle_UpdatesTheHumanReadableState()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.OverwriteMerge);
        Assert.Equal("Omitir SKUs existentes", viewModel.MergeStatusText);

        viewModel.OverwriteMerge = true;

        Assert.Equal("Actualizará productos con SKU coincidente", viewModel.MergeStatusText);
        Assert.Contains("SKU", viewModel.MergeTooltip);
    }

    [Fact]
    public void CanCommit_RequiresAtLeastOneValidRowAndCatalogPermission()
    {
        var viewModel = CreateViewModel();
        var invalid = CreateProduct("SKU-001", "Producto con error", isValid: false);

        viewModel.ImportedProducts = new ObservableCollection<ProductImportDto> { invalid };

        Assert.False(viewModel.CanCommit);
        Assert.Equal("Guardar en Base de Datos", viewModel.SaveButtonText);

        viewModel.ImportedProducts.Add(CreateProduct("SKU-002", "Producto válido", isValid: true));

        Assert.True(viewModel.CanCommit);
        Assert.Equal("Guardar en Base de Datos (1)", viewModel.SaveButtonText);

        var cashierViewModel = CreateViewModel(UserRole.Cashier);
        cashierViewModel.ImportedProducts = new ObservableCollection<ProductImportDto>
        {
            CreateProduct("SKU-003", "Producto válido", isValid: true)
        };

        Assert.False(cashierViewModel.CanMutateCatalog);
        Assert.False(cashierViewModel.CanCommit);
    }

    [Fact]
    public void Stepper_TracksLoadPreviewAndReadyToSaveStates()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.IsStep1Active);
        Assert.False(viewModel.IsStep2Active);
        Assert.False(viewModel.IsStep3Active);

        viewModel.ImportedProducts = new ObservableCollection<ProductImportDto>
        {
            CreateProduct("SKU-001", "Producto con error", isValid: false)
        };

        Assert.True(viewModel.IsStep1Complete);
        Assert.True(viewModel.IsStep2Active);
        Assert.False(viewModel.IsStep3Active);

        viewModel.ImportedProducts.Add(CreateProduct("SKU-002", "Producto válido", isValid: true));

        Assert.True(viewModel.IsStep2Active);
        Assert.False(viewModel.IsStep3Active);

        viewModel.ImportedProducts.RemoveAt(0);

        Assert.True(viewModel.IsStep2Complete);
        Assert.True(viewModel.IsStep3Active);

        viewModel.IsImportCompleted = true;

        Assert.True(viewModel.IsStep3Complete);
        Assert.False(viewModel.IsStep3Active);
    }

    [Fact]
    public void SelectedInvalidRow_ExposesErrorStripValues()
    {
        var viewModel = CreateViewModel();
        var invalid = CreateProduct("SKU-009", "Producto roto", isValid: false);
        invalid.ErrorMessage = "El SKU contiene caracteres no permitidos.";

        viewModel.SelectedProduct = invalid;

        Assert.True(viewModel.HasSelectedError);
        Assert.Equal("SKU-009", viewModel.SelectedErrorSku);
        Assert.Equal("El SKU contiene caracteres no permitidos.", viewModel.SelectedErrorDetail);

        viewModel.SelectedProduct = CreateProduct("SKU-010", "Producto válido", isValid: true);

        Assert.False(viewModel.HasSelectedError);
        Assert.Equal(string.Empty, viewModel.SelectedErrorDetail);
    }

    [Fact]
    public void ImportStatusText_ReflectsErrorsAndReadyState()
    {
        var viewModel = CreateViewModel();
        viewModel.ImportedProducts = new ObservableCollection<ProductImportDto>
        {
            CreateProduct("SKU-001", "Producto con error", isValid: false)
        };

        Assert.Contains("1 filas con errores", viewModel.ImportStatusText);

        viewModel.ImportedProducts.Add(CreateProduct("SKU-002", "Producto válido", isValid: true));

        Assert.Contains("1 filas con errores", viewModel.ImportStatusText);

        viewModel.ImportedProducts.RemoveAt(0);

        Assert.Contains("1 productos válidos", viewModel.ImportStatusText);
    }

    private static ImportProductsViewModel CreateViewModel(UserRole role = UserRole.Admin)
    {
        var session = new UserSession
        {
            CurrentUser = new UserDto
            {
                Cedula = "V-12345678",
                Name = "Test User",
                Role = role,
                IsActive = true
            }
        };

        return new ImportProductsViewModel(
            new StubProductImportService(),
            new StubExchangeRateService(),
            session,
            new StubDialogService());
    }

    private static ProductImportDto CreateProduct(string sku, string name, bool isValid) => new()
    {
        SKU = sku,
        Name = name,
        IsValid = isValid,
        CostPriceUSD = 1.25m,
        ProfitMarginRetail = 30m,
        PriceRetailUSD = 1.63m,
        StockQuantity = 10m
    };

    private sealed class StubProductImportService : IProductImportService
    {
        public Task<string> GenerateTemplateAsync(string destinationPath) => Task.FromResult(destinationPath);

        public Task<List<string>> ReadHeadersAsync(string filePath) => Task.FromResult(new List<string> { "SKU", "Nombre" });

        public Task<List<ProductImportDto>> ParseFileWithMappingAsync(
            string filePath,
            Dictionary<string, string> columnMapping,
            decimal currentExchangeRate) => Task.FromResult(new List<ProductImportDto>());

        public Task<(int added, int updated)> CommitImportAsync(
            IEnumerable<ProductImportDto> products,
            bool overwriteMerge) => Task.FromResult((0, 0));

        public Task<string> ExportProductsToFileAsync(
            string destinationPath,
            bool activeOnly = true,
            string? filter = null) => Task.FromResult(destinationPath);
    }

    private sealed class StubExchangeRateService : IExchangeRateService
    {
        public decimal CurrentRate { get; set; } = 36.5m;
        public DateTime? LastUpdated => DateTime.UtcNow;
        public bool IsRateOutdated => false;

        public Task<(decimal Rate, DateTime? LastUpdated)> GetCurrentRateAsync() =>
            Task.FromResult((CurrentRate, LastUpdated));

        public Task SaveRateAsync(decimal rate) => Task.CompletedTask;

        public Task<List<ExchangeRateHistoryDto>> GetHistoryAsync() =>
            Task.FromResult(new List<ExchangeRateHistoryDto>());

        public Task<(decimal Rate, DateTime? LastUpdated)> SyncBcvAsync() =>
            Task.FromResult((CurrentRate, LastUpdated));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubDialogService : IDialogService
    {
        public bool HasOpenModalDialog => false;

        public bool ShowConfirm(string title, string message) => false;

        public void ShowError(string title, string message) { }

        public void ShowWarning(string title, string message) { }

        public void ShowInfo(string title, string message) { }

        public Task<string?> ShowTextInputAsync(string prompt, string hint) => Task.FromResult<string?>(null);

        public Task<(bool success, string currentPassword, string newPassword)?> ShowChangePasswordDialogAsync() =>
            Task.FromResult<(bool success, string currentPassword, string newPassword)?>(null);

        public decimal? ShowCashAdvanceDialog() => null;

        public bool ShowSuccessDialog(string message, string? secondaryActionLabel = null) => false;

        public Task<(bool success, decimal amount, string reason)?> ShowCashTransactionDialogAsync(string title) =>
            Task.FromResult<(bool success, decimal amount, string reason)?>(null);

        public bool? ShowProductDialog(ProductDialogViewModel dialogVm) => false;

        public (bool success, decimal quantityChange, string reason) ShowAdjustStockDialog(ProductDto product) =>
            (false, 0m, string.Empty);

        public void ShowInterruptedTransactionDialog(string title, string message) { }

        public Task<CustomerDto?> ShowCustomerPickerAsync() => Task.FromResult<CustomerDto?>(null);

        public Task<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?> ShowCashAdvanceRegisterDialogAsync(
            List<PaymentMethodDto> paymentMethods,
            decimal? availableCashLocal) =>
            Task.FromResult<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?>(null);

        public Task<(bool confirmed, IEnumerable<UpdateSaleItemDto>? modifiedItems)> ShowEditSaleDialogAsync(
            SaleDto sale,
            decimal exchangeRate) =>
            Task.FromResult<(bool confirmed, IEnumerable<UpdateSaleItemDto>? modifiedItems)>((false, null));

        public Task ShowPairingQrDialogAsync() => Task.CompletedTask;

        public Task<bool> ShowServerConnectionDialogAsync() => Task.FromResult(false);

        public Task<ProductDto?> ShowVariantSelectionDialogAsync(ProductQuickInfoDto parentProduct) =>
            Task.FromResult<ProductDto?>(null);

        public Task ShowVariantManagementDialogAsync(ProductDto parentProduct) => Task.CompletedTask;

        public Task<object?> ShowModalAsync(object content, string? dialogIdentifier = null) =>
            Task.FromResult<object?>(null);

        public void CloseCurrentModal(object? result = null) { }
    }
}
