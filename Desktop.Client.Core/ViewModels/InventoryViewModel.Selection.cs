using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Messages;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.181-T2 (S5): selección múltiple del catálogo (checkboxes) para la edición masiva de precios.
/// La selección explícita se conserva por ProductId entre páginas (no por fila visual); el modo
/// "todos los filtrados" delega la resolución al backend con los filtros vigentes del listado
/// (Filter + SelectAllFiltered del contrato congelado en T1). El checkbox es estado de UI puro:
/// no dispara persistencia ni recálculos de precio.
/// </summary>
public partial class InventoryViewModel
{
    public const int MaxBulkSelectionSize = 1000;

    private readonly HashSet<int> _selectedProductIds = new();

    /// <summary>true cuando la selección es "todos los que matchean el filtro vigente" (modo filtro del contrato).</summary>
    private bool _selectAllFiltered;

    /// <summary>TotalCount del listado vigente; dimensión del modo "todos los filtrados".</summary>
    private int _filteredMatchCount;

    /// <summary>Suprime el callback de selección mientras el contenedor aplica estado programático.</summary>
    private bool _isApplyingSelection;

    /// <summary>Cantidad efectiva de productos seleccionados (explícitos o todos los filtrados).</summary>
    public int SelectedCount => _selectAllFiltered ? _filteredMatchCount : _selectedProductIds.Count;

    public bool HasSelection => SelectedCount > 0;

    public bool IsAllFilteredSelected => _selectAllFiltered;

    /// <summary>Texto del contador de seleccionados para la barra del catálogo.</summary>
    public string SelectionSummary => _selectAllFiltered
        ? $"Todos los filtrados seleccionados ({SelectedCount})"
        : SelectedCount == 0
            ? "Sin selección"
            : $"{SelectedCount} seleccionado(s)";

    public bool CanEditSelectedPrices => HasSelection;

    /// <summary>
    /// Constructor de filas del catálogo con el estado de selección aplicado y el callback enganchado.
    /// Reemplaza la construcción directa de <see cref="ProductItemViewModel"/> en las cargas y mutaciones.
    /// </summary>
    private ProductItemViewModel CreateItemViewModel(ProductDto dto)
    {
        var itemVm = new ProductItemViewModel(dto, _exchangeRateService, OnProductItemChanged);
        itemVm.NotifyCurrencyChanged(SelectedCurrency);
        ApplySelectionState(itemVm);
        return itemVm;
    }

    private void ApplySelectionState(ProductItemViewModel item)
    {
        // El valor se fija antes de enganchar el callback para que la carga no dispare notificaciones.
        item.IsSelected = _selectAllFiltered || _selectedProductIds.Contains(item.Id);
        item.SelectionChanged = OnProductItemSelectionChanged;
    }

    private void OnProductItemSelectionChanged(ProductItemViewModel item)
    {
        if (_isApplyingSelection)
        {
            return;
        }

        if (_selectAllFiltered)
        {
            // El modo filtro no admite exclusiones: al desmarcar una fila se materializa la selección
            // con los ítems cargados (lo ya visible queda explícito y deja de representar "todos").
            _selectAllFiltered = false;
            foreach (var product in Products)
            {
                if (!ReferenceEquals(product, item))
                {
                    _selectedProductIds.Add(product.Id);
                }
            }
        }

        if (item.IsSelected)
        {
            _selectedProductIds.Add(item.Id);
        }
        else
        {
            _selectedProductIds.Remove(item.Id);
        }

        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsAllFilteredSelected));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanEditSelectedPrices));
    }

    /// <summary>Actualiza la dimensión del modo "todos los filtrados" tras cada listado/merge.</summary>
    private void UpdateFilteredMatchCount(int totalCount)
    {
        _filteredMatchCount = totalCount;
        if (_selectAllFiltered)
        {
            NotifySelectionChanged();
        }
    }

    private void ResetSelection()
    {
        _selectAllFiltered = false;
        _selectedProductIds.Clear();
        SetVisibleSelection(false);
        NotifySelectionChanged();
    }

    private void SetVisibleSelection(bool selected)
    {
        _isApplyingSelection = true;
        try
        {
            foreach (var product in Products)
            {
                product.IsSelected = selected;
            }
        }
        finally
        {
            _isApplyingSelection = false;
        }
    }

    /// <summary>Descarta el modo filtro cuando cambian los filtros: la selección deja de ser válida.</summary>
    private void ClearFilteredSelectionOnFilterChange()
    {
        if (_selectAllFiltered)
        {
            ResetSelection();
        }
    }

    /// <summary>Quita un producto eliminado/pausado de la selección explícita para evitar ids fantasma.</summary>
    private void RemoveFromSelection(int productId)
    {
        if (_selectedProductIds.Remove(productId))
        {
            NotifySelectionChanged();
        }
    }

    [RelayCommand]
    private void SelectAllFiltered()
    {
        if (_filteredMatchCount <= 0)
        {
            return;
        }

        _selectAllFiltered = true;
        _selectedProductIds.Clear();
        SetVisibleSelection(true);
        NotifySelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection() => ResetSelection();

    [RelayCommand]
    private async Task OpenBulkPriceEdit()
    {
        if (_dialogService == null)
        {
            return;
        }

        if (UserSession != null && !UserSession.CanMutateCatalog)
        {
            return;
        }

        if (!HasSelection)
        {
            return;
        }

        if (SelectedCount > MaxBulkSelectionSize)
        {
            _dialogService.ShowWarning(
                "Selección demasiado grande",
                $"La selección incluye {SelectedCount} producto(s) y el máximo por operación es {MaxBulkSelectionSize}. " +
                "Reduzca la selección o refine los filtros del catálogo.");
            return;
        }

        var dialogVm = new BulkPriceEditDialogViewModel(_productService, _dialogService, BuildBulkPriceSelection());
        if (!_dialogService.ShowBulkPriceEditDialog(dialogVm))
        {
            return;
        }

        // Aplicado: se limpia la selección (los precios cambiaron) y el catálogo se recarga para
        // reflejar valores y orden vigentes; el broadcast in-process refresca a los demás consumidores.
        ResetSelection();
        await LoadDataAsync(false, targetPage: CurrentPage > 0 ? CurrentPage : 1);
        WeakReferenceMessenger.Default.Send(new CatalogUpdatedMessage());
        _dialogService.ShowSuccessDialog($"Precios actualizados: {dialogVm.AppliedCount} producto(s).");
    }

    private BulkPriceSelection BuildBulkPriceSelection() => _selectAllFiltered
        ? new BulkPriceSelection(null, BuildSelectionFilter(), SelectAllFiltered: true, SelectedCount)
        : new BulkPriceSelection(_selectedProductIds.OrderBy(id => id).ToList(), null, SelectAllFiltered: false, _selectedProductIds.Count);

    private BulkPriceSelectionFilterDto BuildSelectionFilter() => new()
    {
        SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        StatusFilter = SelectedStatusFilter,
        CategoryId = CategoryFilterIdForQuery
    };
}
