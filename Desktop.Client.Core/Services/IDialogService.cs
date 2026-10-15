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
    void ShowMessage(string title, string message) => ShowWarning(title, message);
    Task<string?> ShowTextInputAsync(string prompt, string hint);
    Task<(bool success, string currentPassword, string newPassword)?> ShowChangePasswordDialogAsync();
    decimal? ShowCashAdvanceDialog();

    /// <summary>
    /// 8.151 (W4, R8/design D7): captura de precio manual (USD/Bs.S) para un producto normal; la
    /// moneda omitida se deriva con la tasa recibida. Implementacion por defecto sin UI (stubs
    /// headless): null equivale a dialogo cancelado, igual que <see cref="ShowCreateInvoiceProductDialog"/>.
    /// </summary>
    ViewModels.ManualPriceDialogResult? ShowManualPriceDialog(decimal exchangeRate) => null;
    bool ShowSuccessDialog(string message, string? secondaryActionLabel = null);
    Task<(bool success, decimal amount, string reason)?> ShowCashTransactionDialogAsync(string title);
    bool? ShowProductDialog(ViewModels.ProductDialogViewModel dialogVm);

    /// <summary>
    /// 8.179-T2 (S3): gestión del árbol de categorías del catálogo (crear/renombrar/mover/borrar)
    /// abierta desde la tarjeta "Categorías" del diálogo de producto. Devuelve true cuando hubo
    /// cambios persistidos, para que el llamador refresque su listado preservando la selección.
    /// La implementación por defecto sin UI (stubs headless) equivale a "sin cambios".
    /// </summary>
    bool ShowCategoryManagementDialog(ViewModels.CategoryManagementDialogViewModel viewModel) => false;

    /// <summary>
    /// 8.181-T2 (S5): diálogo modal de edición masiva de precios del catálogo (configuración de
    /// operaciones, vista previa "Antes vs Después" y aplicación con confirmación). Devuelve true
    /// cuando la actualización se aplicó y persistió, para que el catálogo se refresque.
    /// La implementación por defecto sin UI (stubs headless) equivale a "sin cambios".
    /// </summary>
    bool ShowBulkPriceEditDialog(ViewModels.BulkPriceEditDialogViewModel viewModel) => false;

    /// <summary>
    /// 8.146-T5 (S4): modal de captura del código de barras universal al crear un producto desde una línea de factura.
    /// Implementación por defecto sin UI (stubs headless): null equivale a diálogo cancelado, igual que
    /// <see cref="ShowPartialDeliveryDialogAsync"/>.
    /// </summary>
    bool? ShowCreateInvoiceProductDialog(ViewModels.CreateInvoiceProductDialogViewModel dialogVm) => null;

    /// <summary>
    /// 8.161: modal de alta de proveedor (RIF/NIT + nombre comercial) desde la vista de facturas.
    /// Implementacion por defecto sin UI (stubs headless): null equivale a dialogo cancelado.
    /// </summary>
    bool? ShowRegisterSupplierDialog(ViewModels.RegisterSupplierDialogViewModel dialogVm) => null;

    /// <summary>
    /// Panel interactivo de configuración avanzada del escáner OCR y plantillas por proveedor.
    /// Implementación por defecto sin UI (stubs headless): null equivale a diálogo cancelado.
    /// </summary>
    bool? ShowOcrScannerConfigDialog(ViewModels.OcrScannerConfigDialogViewModel dialogVm) => null;

    (bool success, decimal quantityChange, string reason) ShowAdjustStockDialog(ProductDto product);

    /// <summary>
    /// 8.177-T3 (S1/S4): variante auditada de <see cref="ShowAdjustStockDialog"/> — devuelve además
    /// el motivo estandarizado y la observación obligatoria del ajuste manual. La implementación por
    /// defecto (stubs headless) delega en el método legado y completa los campos nuevos con null;
    /// el flujo de inventario usa siempre esta sobrecarga.
    /// </summary>
    AdjustStockDialogResult ShowAdjustStockDialogWithAudit(ProductDto product)
    {
        var (success, quantityChange, reason) = ShowAdjustStockDialog(product);
        return new AdjustStockDialogResult(success, quantityChange, reason, null, null);
    }

    void ShowInterruptedTransactionDialog(string title, string message);
    Task<CustomerDto?> ShowCustomerPickerAsync();
    Task<(bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?> ShowCashAdvanceRegisterDialogAsync(System.Collections.Generic.List<PaymentMethodDto> paymentMethods, decimal? availableCashLocal);
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

/// <summary>
/// 8.177-T3 (S1): resultado del ajuste manual con los campos de auditoría capturados por el
/// diálogo (motivo estandarizado + observación). <see cref="Cancelled"/> representa diálogo
/// cancelado o stub headless sin UI.
/// </summary>
public sealed record AdjustStockDialogResult(
    bool Success,
    decimal QuantityChange,
    string Reason,
    Core.Entities.StockMovementReason? ReasonCode,
    string? Observation)
{
    public static AdjustStockDialogResult Cancelled { get; } = new(false, 0m, string.Empty, null, null);
}
