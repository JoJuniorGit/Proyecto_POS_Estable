using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Entities;
using Desktop.Client.Services;
using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class AddProductViewModel : ObservableValidator
{
    private readonly IProductService _productService;
    private readonly Action _closeAction;
    private int _id;

    public string ViewTitle => _id == 0 ? "Add New Product" : "Edit Product";

    [ObservableProperty]
    [Required(ErrorMessage = "Product name is required")]
    [MinLength(2, ErrorMessage = "Name must be at least 2 characters")]
    private string _name = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "SKU/Barcode is required")]
    private string _sku = string.Empty;

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Price cannot be negative")]
    private decimal _price;

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Cost cannot be negative")]
    private decimal _cost;

    [ObservableProperty]
    [Range(0, int.MaxValue, ErrorMessage = "Stock cannot be negative")]
    private int _stockQuantity;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _unitOfMeasure = "Unit";

    public System.Collections.Generic.List<string> UnitOfMeasures { get; } = new() { "Unit", "Kg", "Liter", "M", "M2", "M3" };

    [ObservableProperty]
    private decimal _profitPercentage;

    [ObservableProperty]
    [Range(0, int.MaxValue, ErrorMessage = "Threshold cannot be negative")]
    private int _lowStockThreshold;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    private readonly IDialogService? _dialogService;

    public AddProductViewModel(IProductService productService, Action closeAction, Product? product = null, IDialogService? dialogService = null)
    {
        _productService = productService;
        _closeAction = closeAction;
        _dialogService = dialogService;

        if (product != null)
        {
            _id = product.Id;
            Name = product.Name;
            Sku = product.SKU;
            Description = product.Description;
            Price = product.PriceUSD;
            Cost = product.Cost;
            StockQuantity = product.StockQuantity;
            UnitOfMeasure = product.UnitOfMeasure.ToString();
            ProfitPercentage = product.ProfitPercentage;
            LowStockThreshold = product.LowStockThreshold;
        }

        _ = LoadMetadataAsync();
    }

    [RelayCommand]
    private Task LoadMetadataAsync()
    {
        // UOM list is locally hardcoded — no network call needed.
        IsLoading = false;
        IsError = false;
        ErrorMessage = string.Empty;
        return Task.CompletedTask;
    }

    partial void OnCostChanged(decimal value)
    {
        CalculatePrice();
    }

    partial void OnProfitPercentageChanged(decimal value)
    {
        CalculatePrice();
    }

    private bool _isCalculating;

    private void CalculatePrice()
    {
        if (_isCalculating) return;
        _isCalculating = true;
        Price = Cost * (1 + ProfitPercentage / 100);
        _isCalculating = false;
    }

    partial void OnPriceChanged(decimal value)
    {
        if (_isCalculating) return;
        if (Cost == 0) return;
        _isCalculating = true;
        ProfitPercentage = ((Price - Cost) / Cost) * 100;
        _isCalculating = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ValidateAllProperties();

        if (HasErrors || IsLoading || IsError) return;

        try
        {
            var uomType = Enum.TryParse<Core.Entities.UnitOfMeasureType>(UnitOfMeasure, true, out var parsedUom) ? parsedUom : Core.Entities.UnitOfMeasureType.Und;
            var product = new Product
            {
                Id = _id,
                Name = Name,
                SKU = Sku,
                Description = Description,
                PriceUSD = Price,
                Cost = Cost,
                StockQuantity = StockQuantity,
                UnitOfMeasure = uomType,
                ProfitPercentage = ProfitPercentage,
                LowStockThreshold = LowStockThreshold,
                IsActive = true
            };

            if (_id == 0)
            {
                await _productService.CreateAsync(product);
            }
            else
            {
                await _productService.UpdateAsync(product);
            }

            _dialogService?.ShowSuccessDialog("¡Producto guardado exitosamente!");
            _closeAction.Invoke();
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Error al Guardar", $"Error guardando el producto: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _closeAction.Invoke();
    }
}
