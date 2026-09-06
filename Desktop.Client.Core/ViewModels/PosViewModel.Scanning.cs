using Core.DTOs;
using Core.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class PosViewModel
{
    public ObservableCollection<RecentScannedItemViewModel> RecentScannedProducts { get; } = new();

    private string? _lastScannedCode;
    private DateTime _lastScannedTime = DateTime.MinValue;
    private const int SameCodeCooldownMs = 2000;

    private readonly SemaphoreSlim _scannerLock = new(1, 1);

    /// <summary>
    /// Adds a scanned barcode (or any code coming from the camera tool) directly to the cart.
    /// Resolves the product by exact SKU match; unknown codes / cash-advance items are not
    /// added. Cooldown de 2.0s exactos para el mismo código y panel de últimos 3 productos escaneados.
    /// </summary>
    public async Task AddProductByCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var trimmedCode = code.Trim();

        // Restringir lectura exclusivamente a códigos de barras válidos (ignorar QR / URLs / texto largo)
        if (!BarcodeValidator.IsValidBarcode(trimmedCode))
        {
            SearchText = string.Empty;
            return;
        }

        // Cooldown de 2.0 segundos exactos para el mismo producto
        var now = DateTime.UtcNow;
        if (_lastScannedCode == trimmedCode && (now - _lastScannedTime).TotalMilliseconds < SameCodeCooldownMs)
        {
            SearchText = string.Empty;
            return;
        }
        _lastScannedCode = trimmedCode;
        _lastScannedTime = now;

        await _scannerLock.WaitAsync();
        try
        {
            // Lazy-start the sale, mirroring AddSelectedSuggestionAsync.
            if (Cart.CurrentSale == null)
            {
                await StartNewSaleAsync();

                if (Cart.CurrentSale == null)
                {
                    if (_dialogService != null) _dialogService.ShowError("Connection Error", "Could not start a sale session. Please check that the server is running and try again.");
                    else if (Application.Current != null) MessageBox.Show("Could not start a sale session. Please check that the server is running and try again.", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            try
            {
                var results = await _productService.GetSuggestionsAsync(trimmedCode, true, CancellationToken.None);
                var product = results.FirstOrDefault(p => p.SKU == trimmedCode) ?? results.FirstOrDefault();

                // Unknown code, or a cash-advance item:
                if (product == null || product.Id <= 0 || product.IsCashAdvance)
                {
                    return;
                }

                if (product.IsGroupHeader)
                {
                    var variant = await (_dialogService?.ShowVariantSelectionDialogAsync(product) ?? Task.FromResult<ProductDto?>(null));
                    if (variant == null) return;

                    product = new ProductQuickInfoDto
                    {
                        Id = variant.Id,
                        Name = variant.Name,
                        SKU = variant.SKU,
                        PriceUSD = variant.PriceUSD,
                        PriceRetailUSD = variant.PriceRetailUSD,
                        PriceWholesaleUSD = variant.PriceWholesaleUSD,
                        PriceBsS = variant.PriceBsS,
                        StockQuantity = variant.StockQuantity,
                        IsActive = variant.IsActive
                    };
                }

                IsProcessing = true;
                try
                {
                    Cart.CurrentSale = await _salesService.AddItemAsync(Cart.CurrentSale.Id, product.Id, 1, CurrentExchangeRate, null, null);

                    // Actualizar panel reactivo de los últimos 3 productos escaneados
                    var existingRecent = RecentScannedProducts.FirstOrDefault(r => r.ProductId == product.Id);
                    if (existingRecent != null)
                    {
                        RecentScannedProducts.Remove(existingRecent);
                        RecentScannedProducts.Insert(0, existingRecent);
                    }
                    else
                    {
                        var recentVm = new RecentScannedItemViewModel(
                            product.Id,
                            product.SKU,
                            product.Name,
                            product.PriceBsS,
                            product.PriceUSD,
                            Cart,
                            async (saleDetailId, newQty) =>
                            {
                                if (Cart.CurrentSale != null)
                                {
                                    Cart.CurrentSale = await _salesService.UpdateItemQuantityAsync(Cart.CurrentSale.Id, saleDetailId, newQty, CurrentExchangeRate);
                                    foreach (var r in RecentScannedProducts) r.Refresh();
                                }
                            });
                        RecentScannedProducts.Insert(0, recentVm);
                        while (RecentScannedProducts.Count > 3)
                        {
                            RecentScannedProducts.RemoveAt(RecentScannedProducts.Count - 1);
                        }
                    }
                    foreach (var r in RecentScannedProducts) r.Refresh();
                }
                catch (Exception ex)
                {
                    if (_dialogService != null) _dialogService.ShowError("Error", $"Error adding item: {ex.Message}");
                    else if (Application.Current != null) MessageBox.Show($"Error adding item: {ex.Message}");
                }
                finally
                {
                    IsProcessing = false;
                }
            }
            catch (HttpRequestException ex)
            {
                if (_dialogService != null) _dialogService.ShowWarning("Error de Red", $"Error de conexión al consultar el código: {ex.Message}");
                else if (Application.Current != null) MessageBox.Show($"Error de conexión al consultar el código: {ex.Message}", "Error de Red", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
                // Operación cancelada por timeout o token
            }
            catch (Exception ex)
            {
                if (_dialogService != null) _dialogService.ShowError("Error", $"Error looking up the scanned code: {ex.Message}");
                else if (Application.Current != null) MessageBox.Show($"Error looking up the scanned code: {ex.Message}");
            }
        }
        finally
        {
            _scannerLock.Release();
            // La caja de búsqueda se limpia tras CADA intento de escaneo, sin importar el
            // resultado (producto encontrado, no encontrado o error de lectura).
            SearchText = string.Empty;
        }
    }

    /// <summary>
    /// Exact-SKU lookup used by the barcode scanner window to show the product name, price
    /// and status (found / not found / inactive) on the scan result card.
    /// Non-numeric SKUs (e.g. alphanumeric Code-128 values) are treated as "not found".
    /// </summary>
    public async Task<ProductQuickInfoDto?> ResolveScannedCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Trim();
        if (!BarcodeValidator.IsValidBarcode(trimmed)) return null;
        try
        {
            return await _productService.GetQuickInfoAsync(trimmed);
        }
        catch
        {
            return null;
        }
    }
}
