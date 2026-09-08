using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Desktop.Client.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class PosViewModel
{
    [RelayCommand]
    private async Task ChangeCustomerAsync()
    {
        if (Cart.CurrentSale == null) return;
        if (_dialogService == null) return;

        var customer = await _dialogService.ShowCustomerPickerAsync();
        if (customer == null) return;

        try
        {
            var updatedSale = await _salesService.UpdateSaleCustomerAsync(Cart.CurrentSale.Id, customer.Id);
            Cart.CurrentSale = updatedSale;
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error al asignar cliente: {ex.Message}");
        }
    }

    partial void OnSelectedSuggestionChanged(ProductQuickInfoDto? value)
    {
        if (value != null)
        {
            AddSelectedSuggestionAsync(value).SafeFireAndForget("PosViewModel.Orders.AddSelectedSuggestion");
        }
    }

    private async Task AddSelectedSuggestionAsync(ProductQuickInfoDto? value)
    {
        // 8.5-W1: El lock compartido con el scanner permite que una sugerencia seleccionada
        // no se duplique si llega un escaneo rápido de código de barras del mismo producto.
        await _scannerLock.WaitAsync();
        try
        {
            await AddSelectedSuggestionCoreAsync(value);
        }
        finally
        {
            _scannerLock.Release();
        }
    }

    private async Task AddSelectedSuggestionCoreAsync(ProductQuickInfoDto? value)
    {
        // Lazy-start: If sale is null, start one now
        if (Cart.CurrentSale == null)
        {
            System.Diagnostics.Debug.WriteLine("[POS] CurrentSale is null when adding product — attempting lazy StartNewSaleAsync.");
            await StartNewSaleAsync();

            // If still null after the attempt, the API is unreachable — abort with visible error
            if (Cart.CurrentSale == null)
            {
                System.Diagnostics.Debug.WriteLine("[POS] Lazy start FAILED — CurrentSale still null after retry.");
                if (_dialogService != null) _dialogService.ShowError("Connection Error", "Could not start a sale session. Please check that the server is running and try again.");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[POS] Lazy start succeeded.");
        }

        if (value != null && value.Id > 0 && Cart.CurrentSale != null)
        {
            if (value.IsGroupHeader)
            {
                var variant = await (_dialogService?.ShowVariantSelectionDialogAsync(value) ?? Task.FromResult<ProductDto?>(null));
                if (variant == null)
                {
                    SelectedSuggestion = null;
                    return;
                }

                value = new ProductQuickInfoDto
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

            decimal? customPriceUsd = null;
            decimal? customPriceLocal = null;

            if (value.IsCashAdvance)
            {
                if (CurrentExchangeRate <= 0)
                {
                    if (_dialogService != null) _dialogService.ShowWarning("Missing Rate", "Please set a valid Exchange Rate in the top header before requesting a cash advance.");
                    SelectedSuggestion = null;
                    return;
                }

                decimal? requestedBsS = _dialogService?.ShowCashAdvanceDialog();

                if (requestedBsS.HasValue && requestedBsS.Value > 0)
                {
                    decimal totalBsS = requestedBsS.Value * (1 + (value.ProfitPercentage / 100m));
                    customPriceUsd = totalBsS / CurrentExchangeRate;
                    customPriceLocal = totalBsS;
                }
                else
                {
                    SelectedSuggestion = null;
                    return;
                }
            }

            IsProcessing = true;
            try
            {
                Cart.CurrentSale = await _salesService.AddItemAsync(Cart.CurrentSale.Id, value.Id, 1, CurrentExchangeRate, customPriceUsd, customPriceLocal);
            }
            catch (Exception ex)
            {
                if (_dialogService != null) _dialogService.ShowError("Error", $"Error adding item: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                SearchText = string.Empty;
                SelectedSuggestion = null;
                Suggestions.Clear();
                HasSuggestions = false;
            }
        }
    }

    [RelayCommand]
    private async Task CheckoutAsync()
    {
        if (IsProcessing) return;
        if (Cart.CurrentSale == null) return;

        await Cart.FlushAllQuantitiesAsync();

        if (!Cart.CartItems.Any())
        {
            if (_dialogService != null)
                _dialogService.ShowWarning("Validación", "El carrito está vacío. Por favor agregue productos antes de cobrar.");
            return;
        }

        if (CurrentExchangeRate <= 0)
        {
            if (_dialogService != null)
                _dialogService.ShowWarning("Tasa Requerida", "No se puede proceder al cobro. Por favor establezca una tasa de cambio válida en el encabezado.");
            return;
        }

        var checkoutVm = new CheckoutViewModel(Cart.CurrentSale, ActivePaymentMethods, _salesService, CurrentExchangeRate, _userSession, overrideSale: null, dialogService: _dialogService);
        var result = await MaterialDesignThemes.Wpf.DialogHost.Show(checkoutVm, "RootDialog");

        if (result is int realInvoice)
        {
            string formattedMessage = checkoutVm.IsPendingPickup
                ? $"Factura N° {realInvoice:D5}: Cuenta liquidada, stock descontado y enviada a Mercancía en Custodia."
                : $"¡Factura N° {realInvoice:D5} completada con éxito!";

            _dialogService?.ShowSuccessDialog(formattedMessage);
            
            StartNewSaleAsync().SafeFireAndForget("PosViewModel.Orders.PostCheckoutStartNewSale");
        }
    }

    [RelayCommand]
    private async Task HoldOrderAsync()
    {
        if (IsProcessing) return;
        if (Cart.CurrentSale == null) return;

        await Cart.FlushAllQuantitiesAsync();

        if (!Cart.CartItems.Any())
        {
            if (_dialogService != null) _dialogService.ShowWarning("Validación", "El carrito está vacío. Agregue productos antes de guardar en espera.");
            return;
        }

        if (CurrentExchangeRate <= 0)
        {
            if (_dialogService != null) _dialogService.ShowWarning("Tasa Requerida", "No se puede guardar en espera. Por favor establezca una tasa de cambio válida.");
            return;
        }

        // Customer validation: Hold sale requires a registered real customer (cannot be Consumidor Final / IsDefault / V-00000000)
        var currentCustomer = Cart.CurrentSale.Customer;
        bool isDefaultCustomer = currentCustomer == null || currentCustomer.IsDefault || currentCustomer.CedulaOrRif == "V-00000000";

        if (isDefaultCustomer)
        {
            if (_dialogService == null) return;

            _dialogService.ShowInfo(
                "Cliente Requerido",
                "Las ventas en espera requieren asignar un cliente real registrado.\nA continuación seleccione o registre un cliente.");

            var selectedCustomer = await _dialogService.ShowCustomerPickerAsync();
            if (selectedCustomer == null || selectedCustomer.IsDefault || selectedCustomer.CedulaOrRif == "V-00000000")
            {
                _dialogService.ShowWarning(
                    "Cliente Inválido",
                    "Operación cancelada. No se puede guardar en espera a nombre del Consumidor Final.");
                return;
            }

            try
            {
                Cart.CurrentSale = await _salesService.UpdateSaleCustomerAsync(Cart.CurrentSale.Id, selectedCustomer.Id);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error", $"Error al asignar cliente: {ex.Message}");
                return;
            }
        }

        if (!Cart.CurrentSale.CustomerId.HasValue)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", "Error de consistencia: La venta no posee cliente asociado.");
            return;
        }

        IsProcessing = true;
        try
        {
            var request = new HoldSaleRequestDto
            {
                CustomerId = Cart.CurrentSale.CustomerId.Value,
                ExchangeRate = CurrentExchangeRate,
                IsProductDelivered = false,
                InitialPayments = null
            };

            var heldSale = await _salesService.HoldSaleAsync(Cart.CurrentSale.Id, request);

            string customerName = heldSale.CustomerName ?? Cart.CurrentSale.CustomerName ?? "Cliente";
            string successMsg = $"¡Pedido #{heldSale.Id} guardado exitosamente en Cuentas Abiertas para {customerName}!";

            _dialogService?.ShowSuccessDialog(successMsg);

            await StartNewSaleAsync();
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error al guardar pedido en espera: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task TogglePriceListAsync()
    {
        if (Cart.CurrentSale == null || IsProcessing) return;
        string nextType = Cart.IsWholesalePriceList ? "Retail" : "Wholesale";
        await Cart.SetPriceListAsync(nextType);
    }

    [RelayCommand]
    private async Task ClearCartAsync()
    {
        if (Cart.CurrentSale == null || !Cart.CartItems.Any() || IsProcessing) return;

        bool confirmed = _dialogService != null
            ? _dialogService.ShowConfirm("Cancelar Venta (F8)", "¿Está seguro de que desea cancelar la venta actual y limpiar el carrito?")
            : false;

        if (confirmed)
        {
            await StartNewSaleAsync();
        }
    }

    [RelayCommand]
    private void CancelOrClear()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            SearchText = string.Empty;
            Suggestions.Clear();
            HasSuggestions = false;
        }
    }

    [RelayCommand]
    private async Task SyncExchangeRateAsync()
    {
        try
        {
            await _exchangeRateService.SyncBcvAsync();
            OnPropertyChanged(nameof(CurrentExchangeRate));
            OnPropertyChanged(nameof(IsRateOutdated));
        }
        catch { }
    }
}
