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
        StageInvoiceCommand.NotifyCanExecuteChanged();

        if (IsBsSCurrency && string.IsNullOrWhiteSpace(AppliedRateText))
        {
            PrefillAppliedRateAsync().SafeFireAndForget(nameof(PrefillAppliedRateAsync));
        }
    }

    partial void OnAppliedRateTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidAppliedRate));
        OnPropertyChanged(nameof(ShowRateValidationHint));
        OnPropertyChanged(nameof(CanStageInvoice));
        StageInvoiceCommand.NotifyCanExecuteChanged();
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
            var dialogViewModel = new CreateInvoiceProductDialogViewModel(line, IsBsSInvoice);
            if (_dialogService.ShowCreateInvoiceProductDialog(dialogViewModel) != true)
            {
                return;
            }

            var barcode = dialogViewModel.Barcode.Trim();
            var productName = dialogViewModel.Name.Trim();
            IsBusy = true;
            StatusMessage = "Creando el producto en el catálogo...";
            try
            {
                var updatedInvoice = await _invoiceService.CreateProductFromLineAsync(
                    StagedInvoice.Id,
                    line.LineId,
                    new CreateInvoiceProductRequestDto(barcode, productName),
                    cancellationToken);
                LoadStagedInvoice(updatedInvoice);
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

    /// <summary>Precarga la tasa vigente solo si el usuario aun no escribio una (Bs.S).</summary>
    private async Task PrefillAppliedRateAsync()
    {
        try
        {
            var currentRate = await _exchangeRateService.GetCurrentRateAsync();
            if (currentRate.Rate > 0m && IsBsSCurrency && string.IsNullOrWhiteSpace(AppliedRateText))
            {
                AppliedRateText = currentRate.Rate.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (Exception)
        {
            // Sin tasa vigente la factura Bs.S queda bloqueada; el usuario puede escribirla a mano.
        }
    }

    /// <summary>Acepta tasa con coma o punto decimal ("36,50" / "36.50").</summary>
    private static bool TryParseAppliedRate(string? value, out decimal rate) =>
        FlexibleDecimalParser.TryParse(value, out rate);
}
