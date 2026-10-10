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
    bool ShowSuccessDialog(string message, string? secondaryActionLabel = null);
    Task<(bool success, decimal amount, string reason)?> ShowCashTransactionDialogAsync(string title);
    bool? ShowProductDialog(ViewModels.ProductDialogViewModel dialogVm);

    /// <summary>
    /// 8.146-T5 (S4): modal de captura del código de barras universal al crear un producto desde una línea de factura.
    /// Implementación por defecto sin UI (stubs headless): null equivale a diálogo cancelado, igual que
    /// <see cref="ShowPartialDeliveryDialogAsync"/>.
    /// </summary>
    bool? ShowCreateInvoiceProductDialog(ViewModels.CreateInvoiceProductDialogViewModel dialogVm) => null;

    (bool success, decimal quantityChange, string reason) ShowAdjustStockDialog(ProductDto product);
    void ShowInterruptedTransactionDialog(string title, string message);
    Task<CustomerDto?> ShowCustomerPickerAsync();
    Task<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?> ShowCashAdvanceRegisterDialogAsync(System.Collections.Generic.List<PaymentMethodDto> paymentMethods, decimal availableCashLocal);
    Task<(bool confirmed, System.Collections.Generic.IEnumerable<UpdateSaleItemDto>? modifiedItems)> ShowEditSaleDialogAsync(SaleDto sale, decimal exchangeRate);
    Task<PartialDeliveryDialogResult?> ShowPartialDeliveryDialogAsync(PendingPickupClientDto pickup) =>
        Task.FromResult<PartialDeliveryDialogResult?>(null);
    Task ShowPairingQrDialogAsync();
    Task<bool> ShowServerConnectionDialogAsync();
    Task<ProductDto?> ShowVariantSelectionDialogAsync(ProductQuickInfoDto parentProduct);
    Task ShowVariantManagementDialogAsync(ProductDto parentProduct);

    /// <summary>Abre el dialogo modal alojado (p. ej. checkout) y devuelve el resultado al cerrarse.</summary>
    Task<object?> ShowModalAsync(object content, string? dialogIdentifier = null);

    /// <summary>Cierra el dialogo modal actual devolviendo un resultado (sustituye DialogHost.CloseDialogCommand).</summary>
    void CloseCurrentModal(object? result = null);

    /// <summary>
    /// 8.150 (T10, design D7): flujo de espera bloqueante del cajero para una accion protegida. La
    /// implementacion WPF construye el <c>AuthorizationWaitViewModel</c>, dispara StartCommand
    /// antes de ShowDialog, reintenta la operacion con el token aprobado y mapea la fase final a
    /// un resultado. La implementacion por defecto (stubs headless) equivale a cancelado.
    /// </summary>
    Task<AuthorizationWaitResult?> ShowAuthorizationWaitAsync(
        AuthorizationRequestContext context,
        Func<string, Task> retry) => Task.FromResult<AuthorizationWaitResult?>(null);

    /// <summary>
    /// 8.150 (T10, design D7): modal interrumpente de notificaciones admin (aprobar/rechazar,
    /// motivo opcional, badge de cola y aviso de carrera/expiracion). No dispone el ViewModel:
    /// su ciclo de vida pertenece a la sesion.
    /// </summary>
    Task ShowAuthorizationNotificationAsync(ViewModels.AuthorizationNotificationViewModel viewModel) => Task.CompletedTask;
}

/// <summary>8.150 (T10): desenlace del dialogo de espera del cajero.</summary>
public enum AuthorizationWaitOutcome
{
    Granted,
    Rejected,
    Expired,
    Cancelled,
    Failed
}

/// <summary>8.150 (T10): resultado del flujo de espera para el consumidor del POS.</summary>
public sealed record AuthorizationWaitResult(
    AuthorizationWaitOutcome Outcome,
    string? Message = null,
    string? RejectionReason = null);

public class PartialDeliveryDialogResult
{
    public System.Collections.Generic.List<PartialDeliveryItemRequestDto> Items { get; set; } = new();
    public string? Notes { get; set; }
}

