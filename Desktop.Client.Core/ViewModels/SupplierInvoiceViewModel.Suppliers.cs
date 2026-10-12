using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class SupplierInvoiceViewModel
{
    partial void OnSelectedSupplierChanged(SupplierSummaryDto? value)
    {
        if (value is not null)
        {
            SupplierRifOrNit = value.RifOrNit ?? string.Empty;
            SupplierCommercialName = value.CommercialName;
            // 8.167: prefija la moneda recordada del proveedor (nunca la pisa con un default).
            if (!string.IsNullOrWhiteSpace(value.InvoiceCurrency)
                && CurrencyOptions.Contains(value.InvoiceCurrency, StringComparer.Ordinal))
            {
                SelectedCurrency = value.InvoiceCurrency;
            }
        }

        ApplyCurrentColumnMapping();
        RefreshCommandStates();
    }

    partial void OnSelectedCurrencyChanged(string value)
    {
        OnPropertyChanged(nameof(IsBsSCurrency));
        OnPropertyChanged(nameof(IsRateInputEnabled));
        OnPropertyChanged(nameof(HasValidAppliedRate));
        OnPropertyChanged(nameof(ShowRateValidationHint));
        OnPropertyChanged(nameof(CanStageInvoice));
        OnPropertyChanged(nameof(CanConfirmInvoice));
        StageInvoiceCommand.NotifyCanExecuteChanged();
        ConfirmInvoiceCommand.NotifyCanExecuteChanged();

        if (IsBsSCurrency)
        {
            // 8.172: si el OCR ya detectó una tasa de cambio en la factura, prevalece y reemplaza
            // el valor mostrado en la sección al pasar a Bs.S.
            if (OcrDetectedExchangeRate is decimal detectedRate && detectedRate > 0m)
            {
                AppliedRateText = FormatAppliedRateText(detectedRate);
            }
            else if (string.IsNullOrWhiteSpace(AppliedRateText))
            {
                PrefillAppliedRateAsync().SafeFireAndForget(nameof(PrefillAppliedRateAsync));
            }
        }

        PropagateCurrencyAndRateToLines();
    }

    partial void OnAppliedRateTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidAppliedRate));
        OnPropertyChanged(nameof(ShowRateValidationHint));
        OnPropertyChanged(nameof(CanStageInvoice));
        OnPropertyChanged(nameof(CanConfirmInvoice));
        StageInvoiceCommand.NotifyCanExecuteChanged();
        ConfirmInvoiceCommand.NotifyCanExecuteChanged();
        PropagateCurrencyAndRateToLines();
    }

    /// <summary>
    /// 8.161: carga/refresca el listado completo de proveedores del selector de la vista.
    /// Se dispara al abrir la vista; preserva la seleccion vigente por Id.
    /// </summary>
    [RelayCommand]
    private async Task LoadSuppliersAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || !CanMutateCatalog)
        {
            return;
        }

        var previousSupplierId = SelectedSupplier?.Id;
        IsBusy = true;
        try
        {
            var suppliers = await _invoiceService.GetSuppliersAsync(null, null, cancellationToken);
            Suppliers = new ObservableCollection<SupplierSummaryDto>(suppliers);
            if (previousSupplierId is int supplierId)
            {
                var match = Suppliers.FirstOrDefault(supplier => supplier.Id == supplierId);
                if (!ReferenceEquals(SelectedSupplier, match))
                {
                    // 8.161: los DTO son records (igualdad por valor); el null intermedio fuerza
                    // la instancia fresca del listado y evita una seleccion stale.
                    SelectedSupplier = null;
                    SelectedSupplier = match;
                }
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo cargar la lista de proveedores: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// 8.161: el alta de proveedor vive en un modal (RIF/NIT + nombre comercial) precargado con
    /// los datos vigentes (incluidas las pistas OCR). Al confirmar, registra y selecciona.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenRegisterSupplier))]
    private async Task OpenRegisterSupplierAsync(CancellationToken cancellationToken)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        var dialogViewModel = new RegisterSupplierDialogViewModel(
            NormalizeInput(SupplierRifOrNit),
            SupplierCommercialName);
        if (_dialogService.ShowRegisterSupplierDialog(dialogViewModel) != true)
        {
            return;
        }

        await RegisterSupplierAsync(
            dialogViewModel.RifOrNit,
            dialogViewModel.CommercialName,
            cancellationToken);
    }

    /// <summary>8.161: registra el proveedor capturado en el modal y lo deja seleccionado.</summary>
    public async Task RegisterSupplierAsync(
        string? rifOrNit,
        string commercialName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commercialName);

        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        IsBusy = true;
        try
        {
            var supplier = await _invoiceService.CreateSupplierAsync(
                new CreateSupplierRequestDto(
                    NormalizeInput(rifOrNit),
                    commercialName.Trim()),
                cancellationToken);
            if (!Suppliers.Any(existing => existing.Id == supplier.Id))
            {
                Suppliers.Add(supplier);
            }

            SelectedSupplier = supplier;
            StatusMessage = "Proveedor registrado y seleccionado.";
            _dialogService.ShowInfo("Proveedor registrado", StatusMessage);
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo registrar el proveedor: {exception.Message}";
            _dialogService.ShowError("Error de registro", StatusMessage);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand]
    private async Task CreateProductAsync(SupplierInvoiceLineViewModel? line, CancellationToken cancellationToken)
    {
        if (line is null || StagedInvoice is null)
        {
            return;
        }

        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (IsBusy || _isCreatingProduct || !line.CanCreateProduct)
        {
            return;
        }

        _isCreatingProduct = true;
        try
        {
            var dialogViewModel = new CreateInvoiceProductDialogViewModel(
                line,
                IsBsSInvoice,
                (query, token) => _invoiceService.SearchCatalogProductsAsync(query, token));
            if (_dialogService.ShowCreateInvoiceProductDialog(dialogViewModel) != true)
            {
                return;
            }

            var selectedProducts = dialogViewModel.GetEffectiveSelectedProducts();
            if (dialogViewModel.IsLinkExistingMode && selectedProducts.Count > 0)
            {
                var primarySelectedProduct = selectedProducts[0];
                var selectedProductIds = selectedProducts
                    .Select(product => product.Id)
                    .Distinct()
                    .ToArray();
                var invoiceDescription = !string.IsNullOrWhiteSpace(line.EditName)
                    ? line.EditName.Trim()
                    : line.Name;
                var marginOverrides = dialogViewModel.GetSelectedMarginOverrides();
                var marginOverridesByProductId = marginOverrides
                    .GroupBy(item => item.ProductId)
                    .ToDictionary(group => group.Key, group => group.Last());
                IsBusy = true;
                StatusMessage = "Vinculando el ítem con el producto del catálogo...";
                try
                {
                    var previousLinesById = Lines.ToDictionary(item => item.LineId);
                    var linkRequest = selectedProductIds.Length > 1
                        ? new LinkInvoiceProductRequestDto(
                            primarySelectedProduct.Id,
                            invoiceDescription,
                            selectedProductIds,
                            marginOverrides)
                        : new LinkInvoiceProductRequestDto(
                            primarySelectedProduct.Id,
                            invoiceDescription,
                            null,
                            marginOverrides);
                    var updatedInvoice = await _invoiceService.LinkProductToLineAsync(
                        StagedInvoice.Id,
                        line.LineId,
                        linkRequest,
                        cancellationToken);
                    LoadStagedInvoice(updatedInvoice);
                    var linkedGroupKey = selectedProductIds.Length > 1 ? $"group-{line.LineId}" : null;
                    _isPropagatingLinkedGroup = true;
                    try
                    {
                        foreach (var reloadedLine in Lines)
                        {
                            if (previousLinesById.TryGetValue(reloadedLine.LineId, out var previousLine))
                            {
                                if (reloadedLine.LineId == line.LineId)
                                {
                                    reloadedLine.LinkedGroupKey = linkedGroupKey;
                                    reloadedLine.PreserveEditsAfterMultiLinkFrom(previousLine, isPrimaryLine: true);
                                    if (reloadedLine.ResolvedProductId is int primaryProductId
                                        && marginOverridesByProductId.TryGetValue(primaryProductId, out var primaryMarginOverride))
                                    {
                                        reloadedLine.MarginRetailOverride = primaryMarginOverride.MarginRetail;
                                        reloadedLine.MarginWholesaleOverride = primaryMarginOverride.MarginWholesale;
                                    }
                                }
                                else
                                {
                                    reloadedLine.PreserveEditsFrom(previousLine);
                                }
                            }
                            else
                            {
                                reloadedLine.LinkedGroupKey = linkedGroupKey ?? $"group-{line.LineId}";
                                reloadedLine.PreserveEditsAfterMultiLinkFrom(line, isPrimaryLine: false);
                                if (reloadedLine.ResolvedProductId is int secondaryProductId
                                    && marginOverridesByProductId.TryGetValue(secondaryProductId, out var secondaryMarginOverride))
                                {
                                    reloadedLine.MarginRetailOverride = secondaryMarginOverride.MarginRetail;
                                    reloadedLine.MarginWholesaleOverride = secondaryMarginOverride.MarginWholesale;
                                }
                            }
                        }
                    }
                    finally
                    {
                        _isPropagatingLinkedGroup = false;
                    }

                    StatusMessage = selectedProducts.Count > 1
                        ? $"Ítem vinculado con {selectedProducts.Count} productos ({string.Join(", ", selectedProducts.Select(product => product.Name))}). Alias guardado para futuras facturas."
                        : $"Ítem vinculado con '{primarySelectedProduct.Name}'. Alias guardado para futuras facturas.";
                }
                catch (Exception exception)
                {
                    StatusMessage = $"No se pudo vincular el producto: {exception.Message}";
                    _dialogService.ShowError("Error al vincular el producto", StatusMessage);
                }
                finally
                {
                    IsBusy = false;
                }

                return;
            }

            var barcode = dialogViewModel.Barcode.Trim();
            var productName = dialogViewModel.Name.Trim();
            IsBusy = true;
            StatusMessage = "Creando el producto en el catálogo...";
            try
            {
                var previousLinesById = Lines.ToDictionary(item => item.LineId);
                var updatedInvoice = await _invoiceService.CreateProductFromLineAsync(
                    StagedInvoice.Id,
                    line.LineId,
                    new CreateInvoiceProductRequestDto(barcode, productName),
                    cancellationToken);
                LoadStagedInvoice(updatedInvoice);
                foreach (var reloadedLine in Lines)
                {
                    if (previousLinesById.TryGetValue(reloadedLine.LineId, out var previousLine))
                    {
                        reloadedLine.PreserveEditsFrom(
                            previousLine,
                            reloadedLine.LineId == line.LineId ? productName : null);
                    }
                }

                StatusMessage = $"Producto '{productName}' creado. Revise el costo y confirme la factura.";
            }
            catch (Exception exception)
            {
                StatusMessage = $"No se pudo crear el producto: {exception.Message}";
                _dialogService.ShowError("Error al crear el producto", StatusMessage);
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            _isCreatingProduct = false;
        }
    }


    /// <summary>Precarga la tasa vigente solo si el usuario aun no escribio una ni el OCR detectó la de la factura (Bs.S).</summary>
    private async Task PrefillAppliedRateAsync()
    {
        try
        {
            var currentRate = await _exchangeRateService.GetCurrentRateAsync();
            if (currentRate.Rate > 0m
                && IsBsSCurrency
                && OcrDetectedExchangeRate is null
                && string.IsNullOrWhiteSpace(AppliedRateText))
            {
                AppliedRateText = FormatAppliedRateText(currentRate.Rate);
            }
        }
        catch (Exception)
        {
            // Sin tasa vigente la factura Bs.S queda bloqueada; el usuario puede escribirla a mano.
        }
    }

    /// <summary>
    /// 8.172: propaga en vivo la moneda y la tasa aplicada de la factura hacia todas las líneas
    /// cargadas en revisión para que los costos y precios sugeridos se recalculen reactivamente.
    /// </summary>
    private void PropagateCurrencyAndRateToLines()
    {
        if (Lines.Count == 0)
        {
            return;
        }

        if (IsBsSCurrency || IsBsSInvoice)
        {
            if (!TryParseAppliedRate(AppliedRateText, out var parsedRate) || parsedRate <= 0m)
            {
                return;
            }

            var normalizedRate = Math.Round(parsedRate, 4, MidpointRounding.AwayFromZero);
            if (StagedInvoice is not null && IsBsSInvoice && StagedInvoice.AppliedRate != normalizedRate)
            {
                StagedInvoice = StagedInvoice with { AppliedRate = normalizedRate };
            }

            foreach (var line in Lines)
            {
                line.UpdateCurrencyAndRate(CurrencyCodes.BsS, normalizedRate);
            }

            return;
        }

        foreach (var line in Lines)
        {
            line.UpdateCurrencyAndRate(CurrencyCodes.Usd, 1m);
        }
    }

    internal static string FormatAppliedRateText(decimal rate) =>
        rate.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>Acepta tasa con coma o punto decimal ("36,50" / "36.50").</summary>
    private static bool TryParseAppliedRate(string? value, out decimal rate) =>
        SupplierInvoiceLineViewModel.TryParseFlexibleDecimal(value, out rate);
}
