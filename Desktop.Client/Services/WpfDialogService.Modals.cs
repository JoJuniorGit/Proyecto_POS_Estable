using System;
using System.Windows;
using Core.Common;
using Core.DTOs;
using Core.Logging;
using Desktop.Client.Views;
using Desktop.Client.ViewModels;
using Microsoft.Extensions.Logging;

namespace Desktop.Client.Services;

public partial class WpfDialogService
{
    public async System.Threading.Tasks.Task<string?> ShowTextInputAsync(string prompt, string hint)
    {
        if (Application.Current == null) return null;
        using var _ = TrackModal();
        var inputDialog = new TextInputDialog(prompt, hint);
        var result = await MaterialDesignThemes.Wpf.DialogHost.Show(inputDialog, "RootDialog");
        return result as string;
    }

    public System.Threading.Tasks.Task<(bool success, string currentPassword, string newPassword)?> ShowChangePasswordDialogAsync()
    {
        if (Application.Current == null)
            return System.Threading.Tasks.Task.FromResult<(bool, string, string)?>(null);

        (bool success, string currentPassword, string newPassword)? result = null;

        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new ChangePasswordDialog
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true)
            {
                result = (true, dialog.CurrentPassword, dialog.NewPassword);
            }
            else
            {
                result = (false, string.Empty, string.Empty);
            }
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.FromResult(result);
    }

    public decimal? ShowCashAdvanceDialog()
    {
        if (Application.Current == null) return null;

        decimal? resultAmount = null;
        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new CashAdvanceDialog();
            dialog.Owner = Application.Current.MainWindow;
            var res = dialog.ShowDialog();
            if (res == true && dialog.RequestedAmountBsS > 0)
            {
                resultAmount = dialog.RequestedAmountBsS;
            }
        };

        if (Application.Current.Dispatcher.CheckAccess())
        {
            openDialog();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(openDialog);
        }

        return resultAmount;
    }

    /// <summary>
    /// 8.151 (W4, R8/design D7): captura de precio manual (USD/Bs.S) para el alta de un producto
    /// normal. La moneda omitida se deriva dentro del ViewModel con la tasa recibida.
    /// </summary>
    public ManualPriceDialogResult? ShowManualPriceDialog(decimal exchangeRate)
    {
        if (Application.Current == null) return null;

        var viewModel = new ManualPriceDialogViewModel(exchangeRate);
        ManualPriceDialogResult? result = null;
        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new ManualPriceDialog
            {
                DataContext = viewModel,
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            if (dialog.ShowDialog() == true)
            {
                result = viewModel.Result;
            }
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return result;
    }

    public bool ShowSuccessDialog(string message, string? secondaryActionLabel = null)
    {
        if (Application.Current == null) return false;
        using var _ = TrackModal();
        bool secondaryClicked = false;
        Action openDialog = () =>
        {
            var dialog = new SuccessDialogWindow(message, secondaryActionLabel)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();
            secondaryClicked = dialog.SecondaryActionClicked;
        };

        if (Application.Current.Dispatcher.CheckAccess())
        {
            openDialog();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(openDialog);
        }

        return secondaryClicked;
    }

    public async System.Threading.Tasks.Task<(bool success, decimal amount, string reason)?> ShowCashTransactionDialogAsync(string title)
    {
        if (Application.Current == null) return null;
        using var _ = TrackModal();
        var dialog = new CashTransactionDialog(title);
        var result = await MaterialDesignThemes.Wpf.DialogHost.Show(dialog, "RootDialog");
        if (result is bool success && success)
        {
            return (true, dialog.Amount, dialog.Reason);
        }
        return null;
    }

    public bool? ShowProductDialog(ViewModels.ProductDialogViewModel dialogVm)
    {
        if (Application.Current == null) return null;
        using var _ = TrackModal();
        bool? res = null;
        Action openDialog = () =>
        {
            var dialog = new ProductDialog(dialogVm);
            dialog.Owner = Application.Current.MainWindow;
            res = dialog.ShowDialog();
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return res;
    }

    public bool? ShowCreateInvoiceProductDialog(ViewModels.CreateInvoiceProductDialogViewModel dialogVm)
    {
        if (Application.Current == null) return null;
        using var _ = TrackModal();
        bool? res = null;
        Action openDialog = () =>
        {
            var dialog = new CreateInvoiceProductDialog(dialogVm);
            dialog.Owner = Application.Current.MainWindow;
            res = dialog.ShowDialog();
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return res;
    }

    public (bool success, decimal quantityChange, string reason) ShowAdjustStockDialog(Core.DTOs.ProductDto product)
    {
        if (Application.Current == null) return (false, 0m, string.Empty);
        using var _ = TrackModal();
        bool success = false;
        decimal qtyChange = 0m;
        string reason = string.Empty;

        Action openDialog = () =>
        {
            var dialog = new AdjustStockDialog(product);
            dialog.Owner = Application.Current.MainWindow;
            if (dialog.ShowDialog() == true)
            {
                success = true;
                qtyChange = dialog.QuantityChange;
                reason = dialog.Reason;
            }
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return (success, qtyChange, reason);
    }

    public void ShowInterruptedTransactionDialog(string title, string message)
    {
        if (Application.Current == null) return;
        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var vm = new ViewModels.InterruptedTransactionViewModel(title, message);
            var dialog = new InterruptedTransactionDialog(vm);
            dialog.Owner = Application.Current.MainWindow;
            dialog.ShowDialog();
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.InvokeAsync(openDialog).Task.SafeFireAndForget("WpfDialogService.ShowInterruptedTransactionDialog");
    }

    public async System.Threading.Tasks.Task<CustomerDto?> ShowCustomerPickerAsync()
    {
        if (Application.Current == null) return null;

        var vm = new ViewModels.CustomerPickerViewModel(_salesService);
        await vm.InitializeAsync();

        CustomerDto? result = null;
        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new CustomerPickerDialog(vm);
            dialog.Owner = Application.Current.MainWindow;
            if (dialog.ShowDialog() == true)
                result = dialog.SelectedCustomer;
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return result;
    }

    public async System.Threading.Tasks.Task<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?> ShowCashAdvanceRegisterDialogAsync(
        System.Collections.Generic.List<PaymentMethodDto> paymentMethods, 
        decimal availableCashLocal)
    {
        if (Application.Current == null) 
            return null;

        (bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)? result = null;

        using var _ = TrackModal();

        var vm = new ViewModels.CashAdvanceRegisterViewModel(paymentMethods, availableCashLocal, _exchangeRateService?.CurrentRate ?? 1.0m, _cashDrawerService);
        await vm.RefreshCommissionAsync();

        Action openDialog = () =>
        {
            var dialog = new CashAdvanceRegisterDialog(vm);
            dialog.ShowDialog();
            if (vm.DialogResult && vm.SelectedPaymentMethod != null)
            {
                result = (true, vm.RequestedAmountBsS, vm.CommissionAmountBsS ?? 0m, vm.SelectedPaymentMethod.Id, vm.SelectedPaymentMethod.Name, vm.IsTransfer);
            }
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return result;
    }

    public System.Threading.Tasks.Task<(bool confirmed, System.Collections.Generic.IEnumerable<UpdateSaleItemDto>? modifiedItems)> ShowEditSaleDialogAsync(
        SaleDto sale, decimal exchangeRate)
    {
        if (Application.Current == null)
            return System.Threading.Tasks.Task.FromResult<(bool, System.Collections.Generic.IEnumerable<UpdateSaleItemDto>?)>((false, null));

        bool confirmed = false;
        System.Collections.Generic.IEnumerable<UpdateSaleItemDto>? modifiedItems = null;

        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new EditSaleDialog();
            dialog.LoadSale(sale, exchangeRate, _productService);
            dialog.Owner = Application.Current.MainWindow;
            if (dialog.ShowDialog() == true && dialog.HasChanges)
            {
                confirmed = true;
                modifiedItems = dialog.ModifiedItems;
            }
        };


        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.FromResult((confirmed, modifiedItems));
    }

    public System.Threading.Tasks.Task<PartialDeliveryDialogResult?> ShowPartialDeliveryDialogAsync(PendingPickupClientDto pickup)
    {
        if (Application.Current == null)
        {
            return System.Threading.Tasks.Task.FromResult<PartialDeliveryDialogResult?>(null);
        }

        ArgumentNullException.ThrowIfNull(pickup);
        PartialDeliveryDialogResult? result = null;
        var viewModel = new PartialDeliveryDialogViewModel(pickup);

        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new PartialDeliveryDialog
            {
                DataContext = viewModel,
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            if (dialog.ShowDialog() == true)
            {
                result = viewModel.Result;
            }
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.FromResult(result);
    }

    public System.Threading.Tasks.Task ShowPairingQrDialogAsync()
    {
        if (Application.Current == null)
            return System.Threading.Tasks.Task.CompletedTask;

        // 8.9-M13: sin dependencias inyectadas no se crean fallbacks crudos (HttpClient suelto sin
        // ciclo de vida); el diálogo se deniega y se registra, en lugar de operar fuera de DI.
        if (_httpClientFactory == null || _connectionManager == null)
        {
            string logMessage = "[NO-OP DIALOG SUPPRESSED] ShowPairingQrDialogAsync denegado: faltan dependencias inyectadas (IHttpClientFactory / IConnectionManager).";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var serverAddr = _connectionManager.CurrentServerAddress ?? "http://localhost:5000/";
            var httpClient = _httpClientFactory.CreateClient("SalesApi");
            httpClient.BaseAddress = new Uri(serverAddr);

            var vm = new ViewModels.PairingQrViewModel(httpClient);
            var dialog = new PairingQrDialog(vm);
            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                dialog.Owner = Application.Current.MainWindow;
            }
            dialog.ShowDialog();
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task<bool> ShowServerConnectionDialogAsync()
    {
        if (Application.Current == null)
            return System.Threading.Tasks.Task.FromResult(false);

        // 8.9-M13: sin dependencias inyectadas no se construyen ConnectionManager/SubnetScanner
        // ad-hoc; el diálogo se deniega y se registra.
        if (_connectionManager == null || _scannerService == null)
        {
            string logMessage = "[NO-OP DIALOG SUPPRESSED] ShowServerConnectionDialogAsync denegado: faltan dependencias inyectadas (IConnectionManager / ISubnetScannerService).";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return System.Threading.Tasks.Task.FromResult(false);
        }

        bool result = false;
        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var vm = new ViewModels.ServerConnectionViewModel(_connectionManager, _scannerService);
            var dialog = new ServerConnectionDialog(vm);
            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                dialog.Owner = Application.Current.MainWindow;
            }
            var dr = dialog.ShowDialog();
            result = dr == true;
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.FromResult(result);
    }

    public System.Threading.Tasks.Task<ProductDto?> ShowVariantSelectionDialogAsync(ProductQuickInfoDto parentProduct)
    {
        if (Application.Current == null || _productService == null)
            return System.Threading.Tasks.Task.FromResult<ProductDto?>(null);

        // 8.9-M13: el ExchangeRateService lo provee DI; si falta, se deniega el diálogo.
        if (_exchangeRateService == null)
        {
            string logMessage = "[NO-OP DIALOG SUPPRESSED] ShowVariantSelectionDialogAsync denegado: falta dependencia inyectada (IExchangeRateService).";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return System.Threading.Tasks.Task.FromResult<ProductDto?>(null);
        }

        ProductDto? result = null;
        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var vm = new ViewModels.VariantSelectionViewModel(_productService, _exchangeRateService, parentProduct);
            var dialog = new VariantSelectionDialog(vm);
            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                dialog.Owner = Application.Current.MainWindow;
            }
            var dr = dialog.ShowDialog();
            if (dr == true)
            {
                result = vm.SelectedVariant;
            }
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.FromResult(result);
    }

    public System.Threading.Tasks.Task ShowVariantManagementDialogAsync(ProductDto parentProduct)
    {
        if (Application.Current == null || _productService == null)
            return System.Threading.Tasks.Task.CompletedTask;

        // 8.9-M13: el ExchangeRateService lo provee DI; si falta, se deniega el diálogo.
        if (_exchangeRateService == null)
        {
            string logMessage = "[NO-OP DIALOG SUPPRESSED] ShowVariantManagementDialogAsync denegado: falta dependencia inyectada (IExchangeRateService).";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var vm = new ViewModels.VariantManagementViewModel(_productService, _exchangeRateService, this, parentProduct);
            var dialog = new VariantManagementDialog(vm);
            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                dialog.Owner = Application.Current.MainWindow;
            }
            dialog.ShowDialog();
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// 8.150 (T10, design D7): flujo de espera bloqueante del cajero para una accion protegida.
    /// Construye el ViewModel con la dependencia inyectada, dispara StartCommand ANTES de
    /// ShowDialog (contrato T9), abre el dialogo y mapea la fase final a un resultado tipado. Un
    /// fallo de creacion degrada a Failed con mensaje visible (residual T9 cubierto por T10).
    /// </summary>
    public async System.Threading.Tasks.Task<AuthorizationWaitResult?> ShowAuthorizationWaitAsync(
        AuthorizationRequestContext context,
        Func<string, System.Threading.Tasks.Task> retry)
    {
        if (Application.Current == null || _authorizationHubService == null || context == null || retry == null)
        {
            return null;
        }

        var viewModel = new AuthorizationWaitViewModel(_authorizationHubService, context, retry, _dispatcherInvoker);
        try
        {
            await viewModel.StartCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            viewModel.Dispose();
            return new AuthorizationWaitResult(
                AuthorizationWaitOutcome.Failed,
                string.IsNullOrWhiteSpace(ex.Message) ? AuthorizationWaitViewModel.ExecutionFailedMessage : ex.Message);
        }

        if (viewModel.Phase != AuthorizationWaitPhase.Waiting)
        {
            viewModel.Dispose();
            return new AuthorizationWaitResult(
                AuthorizationWaitOutcome.Failed,
                AuthorizationWaitViewModel.ExecutionFailedMessage);
        }

        bool? dialogResult = null;
        // Cancel() del VM vuelve a Idle ANTES de cerrar; se retiene la ultima fase no-idle para
        // mapear Rejected/Expired despues de que ShowDialog retorne.
        var lastNonIdlePhase = AuthorizationWaitPhase.Idle;
        void OnPhaseChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AuthorizationWaitViewModel.Phase)
                && viewModel.Phase != AuthorizationWaitPhase.Idle)
            {
                lastNonIdlePhase = viewModel.Phase;
            }
        }

        viewModel.PropertyChanged += OnPhaseChanged;
        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new AuthorizationWaitDialog(viewModel);
            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                dialog.Owner = Application.Current.MainWindow;
            }
            dialogResult = dialog.ShowDialog();
        };

        try
        {
            if (Application.Current.Dispatcher.CheckAccess()) openDialog();
            else Application.Current.Dispatcher.Invoke(openDialog);
        }
        finally
        {
            viewModel.PropertyChanged -= OnPhaseChanged;
        }

        if (dialogResult == true)
        {
            return new AuthorizationWaitResult(AuthorizationWaitOutcome.Granted);
        }

        return lastNonIdlePhase switch
        {
            AuthorizationWaitPhase.Rejected => new AuthorizationWaitResult(
                AuthorizationWaitOutcome.Rejected, null, viewModel.RejectionReason),
            AuthorizationWaitPhase.Expired => new AuthorizationWaitResult(AuthorizationWaitOutcome.Expired),
            _ => new AuthorizationWaitResult(AuthorizationWaitOutcome.Cancelled)
        };
    }

    /// <summary>
    /// 8.150 (T10, design D7): modal interrumpente de notificaciones admin bindeado a la cola del
    /// ViewModel singleton. No lo dispone: el ciclo de vida pertenece a la sesion.
    /// </summary>
    public System.Threading.Tasks.Task ShowAuthorizationNotificationAsync(AuthorizationNotificationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (Application.Current == null)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new AuthorizationNotificationDialog(viewModel);
            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                dialog.Owner = Application.Current.MainWindow;
            }
            dialog.ShowDialog();
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.CompletedTask;
    }
}

