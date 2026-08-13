using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

public interface IDialogService
{
    bool ShowConfirm(string title, string message);
    void ShowError(string title, string message);
    void ShowWarning(string title, string message);
    void ShowInfo(string title, string message);
    Task<string?> ShowTextInputAsync(string prompt, string hint);
    decimal? ShowCashAdvanceDialog();
    void ShowSuccessDialog(string message);
    Task<(bool success, decimal amount, string reason)?> ShowCashTransactionDialogAsync(string title);
    bool? ShowProductDialog(ViewModels.ProductDialogViewModel dialogVm);
    (bool success, int quantityChange, string reason) ShowAdjustStockDialog(ProductDto product);
    void ShowInterruptedTransactionDialog(string title, string message);
    Task<CustomerDto?> ShowCustomerPickerAsync();
    Task<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?> ShowCashAdvanceRegisterDialogAsync(System.Collections.Generic.List<PaymentMethodDto> paymentMethods, decimal availableCashLocal);
    Task<(bool confirmed, System.Collections.Generic.IEnumerable<UpdateSaleItemDto>? modifiedItems)> ShowEditSaleDialogAsync(SaleDto sale, decimal exchangeRate);
}



