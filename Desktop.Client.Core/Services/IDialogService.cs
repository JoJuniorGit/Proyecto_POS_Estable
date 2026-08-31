using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

public interface IDialogService
{
    /// <summary>
    /// true si hay un diálogo modal abierto en este momento (ventana ShowDialog o DialogHost).
    /// Lo usa MainWindow para advertir antes de cerrar con información posiblemente sin guardar.
    /// </summary>
    bool HasOpenModalDialog { get; }

    bool ShowConfirm(string title, string message);
    void ShowError(string title, string message);
    void ShowWarning(string title, string message);
    void ShowInfo(string title, string message);
    Task<string?> ShowTextInputAsync(string prompt, string hint);
    Task<(bool success, string currentPassword, string newPassword)?> ShowChangePasswordDialogAsync();
    decimal? ShowCashAdvanceDialog();
    void ShowSuccessDialog(string message);
    Task<(bool success, decimal amount, string reason)?> ShowCashTransactionDialogAsync(string title);
    bool? ShowProductDialog(ViewModels.ProductDialogViewModel dialogVm);
    (bool success, decimal quantityChange, string reason) ShowAdjustStockDialog(ProductDto product);
    void ShowInterruptedTransactionDialog(string title, string message);
    Task<CustomerDto?> ShowCustomerPickerAsync();
    Task<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?> ShowCashAdvanceRegisterDialogAsync(System.Collections.Generic.List<PaymentMethodDto> paymentMethods, decimal availableCashLocal);
    Task<(bool confirmed, System.Collections.Generic.IEnumerable<UpdateSaleItemDto>? modifiedItems)> ShowEditSaleDialogAsync(SaleDto sale, decimal exchangeRate);
    Task ShowPairingQrDialogAsync();
    Task<bool> ShowServerConnectionDialogAsync();
    Task<ProductDto?> ShowVariantSelectionDialogAsync(ProductQuickInfoDto parentProduct);
}



