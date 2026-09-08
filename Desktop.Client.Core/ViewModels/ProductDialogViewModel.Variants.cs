using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class ProductDialogViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditStockShared))]
    [NotifyPropertyChangedFor(nameof(CanEditIndependentPricing))]
    [NotifyPropertyChangedFor(nameof(ShowStockInputs))]
    [NotifyPropertyChangedFor(nameof(ShowConversionFactorInput))]
    [NotifyPropertyChangedFor(nameof(ShowManageVariantsButton))]
    [NotifyPropertyChangedFor(nameof(ShowPricingInputs))]
    [NotifyPropertyChangedFor(nameof(ShowIndependentPricingNotice))]
    [NotifyPropertyChangedFor(nameof(CanEditPricing))]
    [NotifyPropertyChangedFor(nameof(CanEditWholesale))]
    [NotifyPropertyChangedFor(nameof(CanEditFractional))]
    private bool _isGroupHeader;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditStockShared))]
    [NotifyPropertyChangedFor(nameof(CanEditIndependentPricing))]
    [NotifyPropertyChangedFor(nameof(ShowStockInputs))]
    private bool _isStockShared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditStockShared))]
    [NotifyPropertyChangedFor(nameof(CanEditIndependentPricing))]
    [NotifyPropertyChangedFor(nameof(ShowPricingInputs))]
    [NotifyPropertyChangedFor(nameof(ShowIndependentPricingNotice))]
    [NotifyPropertyChangedFor(nameof(CanEditPricing))]
    [NotifyPropertyChangedFor(nameof(CanEditWholesale))]
    private bool _hasIndependentPricing;

    [ObservableProperty]
    private int? _parentProductId;

    [ObservableProperty]
    private string? _groupKey;

    [ObservableProperty]
    private ObservableCollection<ProductDto> _parentProducts = new();

    [ObservableProperty]
    private ProductDto? _selectedParentProduct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditGroupHeader))]
    [NotifyPropertyChangedFor(nameof(CanSelectParentProduct))]
    [NotifyPropertyChangedFor(nameof(GroupHeaderToolTip))]
    private bool _hasActiveVariants;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupHeaderToolTip))]
    private int _activeVariantsCount;

    [ObservableProperty]
    [Range(0.0001, 1000000.0, ErrorMessage = "El factor de conversión debe ser mayor a 0")]
    private decimal _conversionFactor = 1.0000m;

    public bool IsVariant => SelectedParentProduct != null && SelectedParentProduct.Id > 0;
    public bool CanEditStockShared => IsCreateMode && IsGroupHeader && !IsCashAdvance;
    public bool CanEditIndependentPricing => IsCreateMode && IsGroupHeader && !IsCashAdvance;
    public bool ShowManageVariantsButton => IsEditMode && IsGroupHeader;
    public bool CanEditFractional => !IsCashAdvance && !IsGroupHeader && !IsInheritingPricing;
    public bool CanEditGroupHeader => !IsCashAdvance && (SelectedParentProduct == null || SelectedParentProduct.Id == 0) && !HasActiveVariants;
    public bool CanSelectParentProduct => !HasActiveVariants && !IsCashAdvance;
    public bool ShowStockInputs => !IsCashAdvance && (!IsGroupHeader || (IsGroupHeader && IsStockShared)) && !(SelectedParentProduct != null && SelectedParentProduct.Id > 0 && SelectedParentProduct.IsStockShared);
    public bool ShowConversionFactorInput => !IsCashAdvance && !IsGroupHeader && SelectedParentProduct != null && SelectedParentProduct.Id > 0 && SelectedParentProduct.IsStockShared;

    public string GroupHeaderToolTip => HasActiveVariants 
        ? $"Este producto es un grupo con {ActiveVariantsCount} variante(s) asociada(s). No puede convertirse en producto independiente; elimine o desvincule primero las variantes."
        : "Activa esta opción si viene en diferentes sabores, tallas o capacidades.";

    partial void OnSelectedParentProductChanged(ProductDto? value)
    {
        OnPropertyChanged(nameof(IsVariant));
        OnPropertyChanged(nameof(IsInheritingPricing));
        OnPropertyChanged(nameof(CanEditPricing));
        OnPropertyChanged(nameof(CanEditWholesale));
        OnPropertyChanged(nameof(CanEditFractional));
        OnPropertyChanged(nameof(CanEditGroupHeader));
        OnPropertyChanged(nameof(CanSelectParentProduct));
        OnPropertyChanged(nameof(ShowStockInputs));
        OnPropertyChanged(nameof(ShowConversionFactorInput));

        if (value != null && value.Id > 0)
        {
            if (HasActiveVariants)
            {
                SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == 0);
                ErrorMessage = "Un producto con variantes asociadas no puede ser asignado como variante de otro padre.";
                IsError = true;
                return;
            }

            IsGroupHeader = false;
            IsStockShared = false;
            HasIndependentPricing = false;

            if (value.IsStockShared)
            {
                StockQuantity = 0m;
                LowStockThreshold = 0m;
            }

            if (!value.HasIndependentPricing)
            {
                if (ParentProductId == null)
                {
                    CaptureManualPricingSnapshot();
                }

                _isUpdatingPrices = true;
                try
                {
                    ParentProductId = value.Id;
                    CostPriceUSD = value.CostPriceUSD;
                    ProfitMarginRetail = value.ProfitMarginRetail;
                    PriceRetailUSD = value.PriceRetailUSD;
                    HasWholesale = value.HasWholesale;
                    ProfitMarginWholesale = value.ProfitMarginWholesale;
                    PriceWholesaleUSD = value.PriceWholesaleUSD;
                    MinWholesaleQuantity = value.MinWholesaleQuantity > 0m ? value.MinWholesaleQuantity : 6.000m;
                    IsFractional = value.IsFractional;
                    UnitOfMeasureType = value.UnitOfMeasure;
                }
                finally
                {
                    _isUpdatingPrices = false;
                }
                CalculatePricing("Cost");
            }
            else
            {
                ParentProductId = value.Id;
            }
        }
        else
        {
            ParentProductId = null;
            RestoreManualPricingSnapshot();
        }
    }

    partial void OnIsGroupHeaderChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditFractional));
        OnPropertyChanged(nameof(CanEditGroupHeader));
        OnPropertyChanged(nameof(CanEditStockShared));
        OnPropertyChanged(nameof(CanEditIndependentPricing));
        OnPropertyChanged(nameof(ShowStockInputs));
        OnPropertyChanged(nameof(ShowManageVariantsButton));

        if (value)
        {
            IsCashAdvance = false;
            SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == 0);
            ParentProductId = null;
            if (!IsStockShared)
            {
                StockQuantity = 0;
                LowStockThreshold = 0;
            }
            IsSkuValid = true;
            SkuVerificationMessage = string.Empty;
            RestoreManualPricingSnapshot();
        }
        else
        {
            IsStockShared = false;
            HasIndependentPricing = false;
            if (!string.IsNullOrWhiteSpace(Sku))
            {
                OnSkuChanged(Sku);
            }
            else
            {
                IsSkuValid = false;
                SkuVerificationMessage = "El código SKU es obligatorio.";
            }
        }
    }

    partial void OnIsStockSharedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStockInputs));
        if (IsGroupHeader && !value)
        {
            StockQuantity = 0;
            LowStockThreshold = 0;
        }
    }

    private void ApplyParentProductsUpdate(List<ProductDto> parents)
    {
        void Update()
        {
            ParentProducts.Clear();
            ParentProducts.Add(new ProductDto { Id = 0, Name = "Ninguno (Producto Independiente)" });
            foreach (var p in parents)
            {
                if (p.Id != _initialProduct?.Id)
                {
                    ParentProducts.Add(p);
                }
            }
            if (_initialProduct?.ParentProductId.HasValue == true && _initialProduct.ParentProductId.Value > 0)
            {
                SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == _initialProduct.ParentProductId.Value)
                                         ?? ParentProducts.FirstOrDefault(p => p.Id == 0);
            }
            else
            {
                SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == 0);
            }
        }

        if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(Update);
        }
        else
        {
            Update();
        }
    }

    [RelayCommand]
    private async Task ManageVariantsAsync()
    {
        if (_initialProduct == null || !IsGroupHeader || _dialogService == null) return;
        var parentDto = new ProductDto
        {
            Id = _initialProduct.Id,
            Name = Name,
            SKU = Sku,
            IsGroupHeader = true,
            IsStockShared = IsStockShared,
            HasIndependentPricing = HasIndependentPricing,
            CostPriceUSD = CostPriceUSD,
            ProfitMarginRetail = ProfitMarginRetail,
            PriceRetailUSD = PriceRetailUSD,
            HasWholesale = HasWholesale,
            ProfitMarginWholesale = ProfitMarginWholesale,
            PriceWholesaleUSD = PriceWholesaleUSD,
            MinWholesaleQuantity = MinWholesaleQuantity,
            IsFractional = IsFractional,
            UnitOfMeasure = UnitOfMeasureType,
        };
        await _dialogService.ShowVariantManagementDialogAsync(parentDto);
        await LoadMetadataAsync();
    }
}
