using System;
using System.Windows;
using Core.DTOs;
using Core.Logging;
using Desktop.Client.Views;
using Microsoft.Extensions.Logging;

namespace Desktop.Client.Services;

public class WpfDialogService : IDialogService
{
    private readonly IClientStateService _clientState;
    private readonly ISalesService _salesService;
    private readonly IProductService? _productService;
    private readonly ILogger<WpfDialogService>? _logger;

    // Contador de diálogos modales abiertos (ventanas ShowDialog y DialogHost). Todos los
    // diálogos se abren desde esta clase, así que este contador cubre la app completa.
    private int _openModalCount;

    /// <summary>true si hay un diálogo modal abierto (ventana ShowDialog o DialogHost).</summary>
    public bool HasOpenModalDialog => _openModalCount > 0;

    private IDisposable TrackModal()
    {
        _openModalCount++;
        return new ModalScope(this);
    }

    private sealed class ModalScope : IDisposable
    {
        private readonly WpfDialogService _owner;
        public ModalScope(WpfDialogService owner) => _owner = owner;
        public void Dispose() => _owner._openModalCount--;
    }

    public WpfDialogService(IClientStateService clientState, ISalesService? salesService = null, IProductService? productService = null, ILogger<WpfDialogService>? logger = null)
    {
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _salesService = salesService!;
        _productService = productService;
        _logger = logger;
    }


    public bool ShowConfirm(string title, string message)
    {
        if (Application.Current == null)
        {
            // Seguridad Extrema / Denegación por Defecto:
            // Sin contexto UI no es posible obtener confirmación humana del cajero.
            string logMessage = $"[NO-OP DIALOG SUPPRESSED - ACTION DENIED] Application.Current es nulo en ShowConfirm. Operación denegada automáticamente: {title} - {message}";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return false;
        }

        if (_clientState.IsFatalErrorActive)
        {
            // Política de Denegación Estricta sin Excepciones / Defensa en Profundidad:
            // Blinda el sistema frente a operaciones destructivas que no dependen de HTTP.
            // En estado degradado se bloquea EN RAÍZ cualquier acción que pida confirmación humana
            // para evitar malinterpretaciones del cajero; el diálogo no se renderiza y retorna false.
            string logMessage = $"[CIRCUIT BREAKER - ACTION DENIED] Cortacircuitos activo en ShowConfirm. Operación denegada de raíz: {title} - {message}";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return false;
        }

        using var _ = TrackModal();
        bool? dialogResult = false;
        if (Application.Current.Dispatcher.CheckAccess())
        {
            var dialog = new CustomDialogWindow(title, message, CustomDialogType.Confirm);
            dialogResult = dialog.ShowDialog();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new CustomDialogWindow(title, message, CustomDialogType.Confirm);
                dialogResult = dialog.ShowDialog();
            });
        }

        return dialogResult == true;
    }

    public void ShowError(string title, string message)
    {
        ShowNotificationDialog(title, message, CustomDialogType.Error, "ShowError");
    }

    public void ShowWarning(string title, string message)
    {
        ShowNotificationDialog(title, message, CustomDialogType.Warning, "ShowWarning");
    }

    public void ShowInfo(string title, string message)
    {
        ShowNotificationDialog(title, message, CustomDialogType.Info, "ShowInfo");
    }

    private void ShowNotificationDialog(string title, string message, CustomDialogType type, string methodName)
    {
        if (Application.Current == null)
        {
            string logMessage = $"[NO-OP DIALOG SUPPRESSED] Application.Current es nulo en {methodName}. Diálogo suprimido: {title} - {message}";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return;
        }

        if (_clientState.IsFatalErrorActive)
        {
            string logMessage = $"[CIRCUIT BREAKER - DIALOG SUPPRESSED] Cortacircuitos activo en {methodName}. Diálogo suprimido: {title} - {message}";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return;
        }

        using var _ = TrackModal();
        if (Application.Current.Dispatcher.CheckAccess())
        {
            var dialog = new CustomDialogWindow(title, message, type);
            dialog.ShowDialog();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new CustomDialogWindow(title, message, type);
                dialog.ShowDialog();
            });
        }
    }

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

    public void ShowSuccessDialog(string message)
    {
        if (Application.Current == null) return;
        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var dialog = new SuccessDialogWindow(message)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();
        };

        if (Application.Current.Dispatcher.CheckAccess())
        {
            openDialog();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(openDialog);
        }
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

    public (bool success, int quantityChange, string reason) ShowAdjustStockDialog(Core.DTOs.ProductDto product)
    {
        if (Application.Current == null) return (false, 0, string.Empty);
        using var _ = TrackModal();
        bool success = false;
        int qtyChange = 0;
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
        else Application.Current.Dispatcher.Invoke(openDialog);
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

    public System.Threading.Tasks.Task<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?> ShowCashAdvanceRegisterDialogAsync(
        System.Collections.Generic.List<PaymentMethodDto> paymentMethods, 
        decimal availableCashLocal)
    {
        if (Application.Current == null) 
            return System.Threading.Tasks.Task.FromResult<(bool, decimal, decimal, int, string, bool)?>(null);

        (bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)? result = null;

        using var _ = TrackModal();
        Action openDialog = () =>
        {
            var vm = new ViewModels.CashAdvanceRegisterViewModel(paymentMethods, availableCashLocal);
            var dialog = new CashAdvanceRegisterDialog(vm);
            dialog.ShowDialog();
            if (vm.DialogResult && vm.SelectedPaymentMethod != null)
            {
                result = (true, vm.RequestedAmountBsS, vm.CommissionAmountBsS, vm.SelectedPaymentMethod.Id, vm.SelectedPaymentMethod.Name, vm.IsTransfer);
            }
        };

        if (Application.Current.Dispatcher.CheckAccess()) openDialog();
        else Application.Current.Dispatcher.Invoke(openDialog);

        return System.Threading.Tasks.Task.FromResult(result);
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
}

