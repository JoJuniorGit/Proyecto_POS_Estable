using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Logging;
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
                var changed = item.GetDto();
                var dto = ProductClientMapping.ToUpdateProductDto(product);
                dto.ProfitMarginRetail = changed.ProfitPercentage;
                dto.PriceUSD = changed.PriceUSD;
                dto.PriceBsS = changed.PriceBsS;
                await _productService.UpdateAsync(dto);
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al auto-guardar el producto {item.Id}: {ex.Message}", nameof(InventoryViewModel));
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
                RemoveFromSelection(item.Id);
                Products.Remove(item);
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al cambiar el estado del producto {item.Id}: {ex.Message}", nameof(InventoryViewModel));
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
                RemoveFromSelection(item.Id);
                Products.Remove(item);
            }
            _dialogService?.ShowSuccessDialog($"Producto '{item.Name}' restaurado exitosamente a estado Activo.");
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al restaurar el producto {item.Id}: {ex.Message}", nameof(InventoryViewModel));
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
                    RemoveFromSelection(item.Id);
                    Products.Remove(item);
                    _dialogService.ShowSuccessDialog($"Producto '{item.Name}' eliminado permanentemente de la base de datos.");
                }
                else
                {
                    item.IsActive = false;
                    item.IsDeleted = true;
                    if (SelectedStatusFilter == "active")
                    {
                        RemoveFromSelection(item.Id);
                        Products.Remove(item);
                    }
                    _dialogService.ShowWarning("Archivado por Auditoría", $"El producto '{item.Name}' posee ventas registradas en el historial. Ha sido archivado en 'Eliminados - Archivo Contable' para preservar la integridad de los datos.");
                }
            }
            catch (Exception ex)
            {
                ClientStateLogger.LogError($"Fallo al eliminar el producto {item.Id}: {ex.Message}", nameof(InventoryViewModel));
                _dialogService.ShowError("Error al Eliminar", $"Error al eliminar producto: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task OpenAddProduct()
    {
        if (_dialogService == null) return;
        var dialogVm = new ProductDialogViewModel(_productService, _exchangeRateService, null, UserSession, _dialogService, settingsService: _settingsService);
        
        if (_dialogService.ShowProductDialog(dialogVm) == true)
        {
            try
            {
                var createdProduct = await _productService.CreateAsync(dialogVm.ResultProduct);
                
                // Add to list and select
                Products.Insert(0, CreateItemViewModel(createdProduct));
                
                _dialogService.ShowSuccessDialog($"Producto '{createdProduct.Name}' agregado exitosamente.");
            }
            catch (Exception ex)
            {
                ClientStateLogger.LogError($"Fallo al agregar el producto: {ex.Message}", nameof(InventoryViewModel));
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

            var dialogVm = new ProductDialogViewModel(_productService, _exchangeRateService, product, UserSession, _dialogService, settingsService: _settingsService);
            
            if (_dialogService.ShowProductDialog(dialogVm) == true)
            {
                await _productService.UpdateAsync(ProductClientMapping.ToUpdateProductDto(dialogVm.ResultProduct, dialogVm.ResultProductId));
                var updatedDto = ProductClientMapping.Merge(product, dialogVm.ResultProduct);
                
                var index = Products.IndexOf(item);
                if (index != -1)
                {
                    Products[index] = CreateItemViewModel(updatedDto);
                }
                
                _dialogService.ShowSuccessDialog($"Producto '{dialogVm.ResultProduct.Name}' actualizado con éxito.");
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al editar el producto {item.Id}: {ex.Message}", nameof(InventoryViewModel));
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

            // 8.177-T3 (S1/S4): el flujo manual exige motivo estandarizado + observación desde la UI.
            var (success, qtyChange, reason, reasonCode, observation) = _dialogService.ShowAdjustStockDialogWithAudit(product);
            if (success)
            {
                await _productService.AdjustStockAsync(product.Id, qtyChange, reason, reasonCode, observation);
                // The item in our list needs to show updated stock
                var dto = item.GetDto();
                dto.StockQuantity += qtyChange;
                
                var index = Products.IndexOf(item);
                if (index != -1)
                {
                    Products[index] = CreateItemViewModel(dto);
                }
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al ajustar el stock del producto {item.Id}: {ex.Message}", nameof(InventoryViewModel));
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
                _dialogService?.ShowInfo("Verificación Rápida", $"Escaneado: {product.Name}\nPrecio USD: ${CurrencyDisplay.Number(product.PriceUSD)}\nStock: {product.StockQuantity}");
            }
            else
            {
                _dialogService?.ShowWarning("Búsqueda por Escaneo", $"Producto no encontrado con el código: {term}");
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al escanear el código '{term}': {ex.Message}", nameof(InventoryViewModel));
            _dialogService?.ShowError("Error de Escaneo", $"Fallo en la lectura del código: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }
    }

}
