using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using System;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class InventoryViewModel
{
    private void OnProductItemChanged(ProductItemViewModel item)
    {
        Core.Common.TaskExtensions.SafeFireAndForget(OnProductItemChangedAsync(item), "InventoryViewModel.ProductItemChanged");
    }

    private async Task OnProductItemChangedAsync(ProductItemViewModel item)
    {
        try
        {
            var product = await _productService.GetByIdAsync(item.Id);
            if (product != null)
            {
                var dto = item.GetDto();
                product.ProfitPercentage = dto.ProfitPercentage;
                product.PriceUSD = dto.PriceUSD;
                product.PriceBsS = dto.PriceBsS;
                await _productService.UpdateAsync(product);
            }
        }
        catch (Exception ex)
        {
            _dialogService?.ShowWarning("Error de Auto-Guardado", $"Error al guardar automáticamente el producto {item.Id}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SetStatusFilter(string filter)
    {
        SelectedStatusFilter = filter;
    }

    [RelayCommand]
    private async Task TogglePauseProduct(ProductItemViewModel item)
    {
        try
        {
            bool newActive = !item.IsActive;
            await _productService.SetStatusAsync(item.Id, newActive, false);
            item.IsActive = newActive;
            item.IsDeleted = false;

            if (SelectedStatusFilter != "all")
            {
                Products.Remove(item);
            }
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Error de Estado", $"Error al cambiar estado del producto: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RestoreProduct(ProductItemViewModel item)
    {
        try
        {
            await _productService.RestoreAsync(item.Id);
            item.IsActive = true;
            item.IsDeleted = false;
            
            if (SelectedStatusFilter != "all")
            {
                Products.Remove(item);
            }
            _dialogService?.ShowSuccessDialog($"Producto '{item.Name}' restaurado exitosamente a estado Activo.");
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Error al Restaurar", $"Error restaurando producto: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteProduct(ProductItemViewModel item)
    {
        if (_dialogService == null) return;

        // Paso 1: Preguntar si desea pausar/deshabilitar el producto
        bool wantPause = _dialogService.ShowConfirm(
            "Pausar / Deshabilitar Producto",
            $"¿Desea pausar/deshabilitar el producto '{item.Name}'?\n\n(El producto quedará Inactivo y podrá reactivarse posteriormente)");

        if (wantPause)
        {
            await TogglePauseProduct(item);
            return;
        }

        // Paso 2: Si no deseaba pausar, preguntar si desea eliminar definitivamente / archivar
        bool wantDelete = _dialogService.ShowConfirm(
            "Eliminar Producto",
            $"¿Desea eliminar definitivamente el producto '{item.Name}'?\n\n(Si el producto posee ventas pasadas en el historial, se archivará automáticamente para auditoría contable)");

        if (wantDelete)
        {
            try
            {
                var res = await _productService.DeleteAsync(item.Id, hardDelete: true);
                if (res == "hard_deleted")
                {
                    Products.Remove(item);
                    _dialogService.ShowSuccessDialog($"Producto '{item.Name}' eliminado permanentemente de la base de datos.");
                }
                else
                {
                    item.IsActive = false;
                    item.IsDeleted = true;
                    if (SelectedStatusFilter == "active")
                    {
                        Products.Remove(item);
                    }
                    _dialogService.ShowWarning("Archivado por Auditoría", $"El producto '{item.Name}' posee ventas registradas en el historial. Ha sido archivado en 'Eliminados - Archivo Contable' para preservar la integridad de los datos.");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error al Eliminar", $"Error al eliminar producto: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task OpenAddProduct()
    {
        if (_dialogService == null) return;
        var dialogVm = new ProductDialogViewModel(_productService, _exchangeRateService, null, UserSession, _dialogService);
        
        if (_dialogService.ShowProductDialog(dialogVm) == true)
        {
            try
            {
                var createdProduct = await _productService.CreateAsync(dialogVm.ResultProduct);
                var newDto = MapToDto(createdProduct);
                
                // Add to list and select
                var viewModelItem = new ProductItemViewModel(newDto, _exchangeRateService, OnProductItemChanged);
                Products.Insert(0, viewModelItem);
                
                _dialogService.ShowSuccessDialog($"Producto '{createdProduct.Name}' agregado exitosamente.");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error al Agregar", $"Error al agregar producto: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task EditProduct(ProductItemViewModel item)
    {
        if (_dialogService == null) return;
        try
        {
            var product = await _productService.GetByIdAsync(item.Id);
            if (product == null)
            {
                _dialogService.ShowWarning("Producto no encontrado", "No se encontró el producto especificado.");
                return;
            }

            var dialogVm = new ProductDialogViewModel(_productService, _exchangeRateService, product, UserSession, _dialogService);
            
            if (_dialogService.ShowProductDialog(dialogVm) == true)
            {
                await _productService.UpdateAsync(dialogVm.ResultProduct);
                var updatedDto = MapToDto(dialogVm.ResultProduct);
                
                var index = Products.IndexOf(item);
                if (index != -1)
                {
                    Products[index] = new ProductItemViewModel(updatedDto, _exchangeRateService, OnProductItemChanged);
                }
                
                _dialogService.ShowSuccessDialog($"Producto '{dialogVm.ResultProduct.Name}' actualizado con éxito.");
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error al Editar", $"Error al editar el producto: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AdjustStock(ProductItemViewModel item)
    {
        if (_dialogService == null) return;
        if (!item.CanAdjustStock)
        {
            _dialogService.ShowWarning("Ajuste no permitido", item.AdjustStockToolTip);
            return;
        }
        try
        {
            var product = await _productService.GetByIdAsync(item.Id);
            if (product == null)
            {
                _dialogService.ShowWarning("Producto no encontrado", "No se encontró el producto especificado.");
                return;
            }

            var (success, qtyChange, reason) = _dialogService.ShowAdjustStockDialog(MapToDto(product));
            if (success)
            {
                await _productService.AdjustStockAsync(product.Id, qtyChange, reason);
                // The item in our list needs to show updated stock
                var dto = item.GetDto();
                dto.StockQuantity += qtyChange;
                
                var index = Products.IndexOf(item);
                if (index != -1)
                {
                    Products[index] = new ProductItemViewModel(dto, _exchangeRateService, OnProductItemChanged);
                }
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error al Ajustar Stock", $"Error al ajustar stock: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task Scan(string term)
    {
        IsScanning = true;
        try
        {
            var product = await _productService.GetQuickInfoAsync(term);
            if (product != null)
            {
                _dialogService?.ShowInfo("Verificación Rápida", $"Escaneado: {product.Name}\nPrecio USD: ${product.PriceUSD:N2}\nStock: {product.StockQuantity}");
            }
            else
            {
                _dialogService?.ShowWarning("Búsqueda por Escaneo", $"Producto no encontrado con el código: {term}");
            }
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Error de Escaneo", $"Fallo en la lectura del código: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    private ProductDto MapToDto(Product p)
    {
        return new ProductDto
        {
            Id = p.Id,
            Name = p.Name,
            SKU = p.SKU,
            Description = p.Description,
            PriceUSD = p.PriceUSD,
            PriceRetailUSD = p.PriceRetailUSD,
            PriceWholesaleUSD = p.PriceWholesaleUSD,
            CostPriceUSD = p.CostPriceUSD,
            ProfitMarginRetail = p.ProfitMarginRetail,
            ProfitMarginWholesale = p.ProfitMarginWholesale,
            MinWholesaleQuantity = p.MinWholesaleQuantity,
            HasWholesale = p.HasWholesale,
            IsFractional = p.IsFractional,
            PriceBsS = p.PriceBsS,
            Cost = p.Cost,
            StockQuantity = p.StockQuantity,
            ProfitPercentage = p.ProfitPercentage,
            UnitOfMeasure = p.UnitOfMeasure,
            LowStockThreshold = p.LowStockThreshold,
            IsCashAdvance = p.IsCashAdvance,
            IsActive = p.IsActive,
            IsDeleted = p.IsDeleted,
            ReservedQuantity = p.ReservedQuantity,
            IsGroupHeader = p.IsGroupHeader,
            ParentProductId = p.ParentProductId,
            GroupKey = p.GroupKey
        };
    }
}
