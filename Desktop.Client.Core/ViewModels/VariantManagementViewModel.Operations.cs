using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class VariantManagementViewModel
{
    partial void OnSearchCatalogTextChanged(string value)
    {
        SearchCandidatesDebouncedAsync().SafeFireAndForget("VariantManagementViewModel.SearchCandidates");
    }

    private async Task SearchCandidatesDebouncedAsync()
    {
        var oldCts = System.Threading.Interlocked.Exchange(ref _searchCts, new System.Threading.CancellationTokenSource());
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }

        var token = _searchCts.Token;
        try
        {
            await Task.Delay(300, token);
            await SearchCandidatesAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Debounce cancel is normal
        }
    }

    [RelayCommand]
    public async Task SearchCandidatesAsync(System.Threading.CancellationToken token = default)
    {
        try
        {
            IsSearchingCatalog = true;
            var result = await _productService.GetCandidateVariantsPagedAsync(ParentProduct.Id, SearchCatalogText, 1, 30, token);
            CandidateProducts.Clear();
            foreach (var item in result.Items)
            {
                var vm = new CandidateProductItemViewModel(item)
                {
                    SelectionChanged = UpdateSelectedCandidatesState
                };
                CandidateProducts.Add(vm);
            }
            UpdateSelectedCandidatesState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = $"Error al buscar candidatos: {ex.Message}";
        }
        finally
        {
            IsSearchingCatalog = false;
        }
    }

    public void UpdateSelectedCandidatesState()
    {
        HasSelectedCandidates = CandidateProducts.Any(c => c.IsSelected);
    }

    [RelayCommand]
    public void ToggleSelectAllCandidates()
    {
        bool target = !CandidateProducts.All(c => c.IsSelected);
        foreach (var c in CandidateProducts)
        {
            c.IsSelected = target;
        }
        UpdateSelectedCandidatesState();
    }

    [RelayCommand]
    public async Task ToggleLinkingPanelAsync()
    {
        IsLinkingPanelOpen = !IsLinkingPanelOpen;
        if (IsLinkingPanelOpen)
        {
            SearchCatalogText = string.Empty;
            await SearchCandidatesAsync();
        }
    }

    [RelayCommand]
    public async Task LinkSelectedCandidatesAsync()
    {
        var selectedIds = CandidateProducts.Where(c => c.IsSelected).Select(c => c.Id).ToList();
        if (!selectedIds.Any()) return;

        try
        {
            IsSaving = true;
            StatusMessage = "Vinculando productos seleccionados...";
            var updatedVariants = await _productService.LinkVariantsBatchAsync(ParentProduct.Id, selectedIds);

            Variants.Clear();
            foreach (var dto in updatedVariants)
            {
                Variants.Add(new VariantItemViewModel(dto, HasIndependentPricing, IsStockShared, _exchangeRateService.CurrentRate));
            }

            StatusMessage = $"{selectedIds.Count} productos vinculados exitosamente.";
            _dialogService.ShowSuccessDialog($"Se vincularon {selectedIds.Count} variantes exitosamente.");
            IsLinkingPanelOpen = false;
            SearchCatalogText = string.Empty;
            CandidateProducts.Clear();
            UpdateSelectedCandidatesState();
        }
        catch (Exception ex)
        {
            StatusMessage = "Conflicto o error al vincular productos.";
            _dialogService.ShowError("Error al vincular", $"No se pudieron vincular las variantes:\n{ex.Message}\n\nSe recargará la lista de variantes.");
            await LoadVariantsAsync();
            if (IsLinkingPanelOpen)
            {
                await SearchCandidatesAsync();
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    public async Task UnlinkVariantAsync(VariantItemViewModel? item)
    {
        if (item == null) return;

        bool confirmed = _dialogService.ShowConfirm("Desvincular Variante", $"¿Está seguro de que desea desvincular '{item.Name}' del producto padre? Pasará a ser un producto individual independiente.");
        if (!confirmed) return;

        try
        {
            IsSaving = true;
            StatusMessage = $"Desvinculando {item.Name}...";
            await _productService.UnlinkVariantAsync(ParentProduct.Id, item.Id);
            Variants.Remove(item);
            StatusMessage = $"Variante '{item.Name}' desvinculada exitosamente.";
            _dialogService.ShowSuccessDialog($"La variante '{item.Name}' fue desvinculada correctamente.");
            if (IsLinkingPanelOpen)
            {
                await SearchCandidatesAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Conflicto o error al desvincular.";
            _dialogService.ShowError("Error al desvincular", $"No se pudo desvincular la variante:\n{ex.Message}");
            await LoadVariantsAsync();
        }
        finally
        {
            IsSaving = false;
        }
    }
}
