using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class VariantSelectionViewModel : ObservableObject
{
    private readonly IProductService _productService;
    private readonly IExchangeRateService _exchangeRateService;

    public Action<bool>? RequestClose;
    public ProductDto? SelectedVariant { get; private set; }

    [ObservableProperty]
    private ProductQuickInfoDto? _parentProduct;

    [ObservableProperty]
    private ObservableCollection<ProductDto> _variants = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedVariant))]
    private ProductDto? _currentSelectedVariant;

    public bool HasSelectedVariant => CurrentSelectedVariant != null;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public decimal ExchangeRate => _exchangeRateService?.CurrentRate ?? 1m;

    public decimal BasePriceUSD => ParentProduct?.PriceRetailUSD > 0 ? ParentProduct.PriceRetailUSD : (ParentProduct?.PriceUSD ?? 0m);
    // 8.143: derivar de USD x tasa vigente; el snapshot PriceBsS solo es fallback sin USD o sin tasa valida.
    public decimal BasePriceBsS => BasePriceUSD > 0 && ExchangeRate > 0
        ? Helpers.PricingHelper.ToBsSCeiling(BasePriceUSD, ExchangeRate)
        : (ParentProduct?.PriceBsS > 0 ? ParentProduct.PriceBsS : 0m);

    public VariantSelectionViewModel(
        IProductService productService,
        IExchangeRateService exchangeRateService,
        ProductQuickInfoDto parentProduct)
    {
        _productService = productService;
        _exchangeRateService = exchangeRateService;
        ParentProduct = parentProduct;

        LoadVariantsAsync().SafeFireAndForget("VariantSelectionViewModel.LoadVariants");
    }

    public async Task LoadVariantsAsync()
    {
        if (ParentProduct == null || ParentProduct.Id <= 0) return;

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var list = await _productService.GetVariantsAsync(ParentProduct.Id);
            Variants.Clear();
            foreach (var v in list)
            {
                Variants.Add(v);
            }

            if (Variants.Count > 0)
            {
                CurrentSelectedVariant = Variants[0];
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al cargar variantes: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectVariant(ProductDto? variant)
    {
        var target = variant ?? CurrentSelectedVariant;
        if (target != null)
        {
            SelectedVariant = target;
            RequestClose?.Invoke(true);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        SelectedVariant = null;
        RequestClose?.Invoke(false);
    }
}
