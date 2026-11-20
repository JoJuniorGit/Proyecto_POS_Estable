using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class InventoryViewModel
{
    /// <summary>
    /// Paso 15 (S6): acceso al informe ejecutivo. La sesion legacy (null) conserva el acceso de
    /// pruebas; una sesion sin permiso de catalogo no ve costos ni capital inmovilizado.
    /// </summary>
    public bool CanUseInventoryHealth =>
        _inventoryRotationService != null &&
        (UserSession == null || UserSession.CanMutateCatalog);

    [RelayCommand(CanExecute = nameof(CanUseInventoryHealth))]
    private async Task OpenInventoryHealth()
    {
        if (_dialogService == null || _inventoryRotationService == null)
        {
            return;
        }

        if (UserSession != null && !UserSession.CanMutateCatalog)
        {
            return;
        }

        var viewModel = new InventoryHealthViewModel(
            _inventoryRotationService,
            _productService,
            _categoryService,
            _dialogService,
            _settingsService,
            UserSession,
            _dispatcherInvoker);

        // El dialogo abre de inmediato y muestra su estado de carga; no bloqueamos la apertura
        // esperando el HTTP del reporte.
        _ = viewModel.EnsureLoadedAsync();
        var applied = _dialogService.ShowInventoryHealthDialog(viewModel);
        viewModel.Dispose();

        if (!applied)
        {
            return;
        }

        ResetSelection();
        await LoadDataAsync(false, targetPage: CurrentPage > 0 ? CurrentPage : 1);
        WeakReferenceMessenger.Default.Send(new CatalogUpdatedMessage());
    }
}
