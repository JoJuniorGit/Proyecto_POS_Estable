using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class SupplierInvoiceViewModel
{
    /// <summary>
    /// 8.175-S1: habilita el botón "Actualizar" solo para filas resueltas con coincidencia de
    /// catálogo ([ACTUALIZAR]) cuando el rol puede mutar el catálogo y no hay otra operación en curso.
    /// </summary>
    public bool CanUpdateLinkedProduct(SupplierInvoiceLineViewModel? line) =>
        CanMutateCatalog && !IsBusy && !_isCreatingProduct && line?.CanUpdateLink == true;

    /// <summary>
    /// 8.175-S1/S2/S4/S7: abre el modal existente "Resolver producto desde factura" precargado con
    /// el/los producto(s) ya vinculado(s) de la fila (o de su grupo 1:N, anclado a la fila primaria)
    /// y, al guardar, re-resuelve el vínculo reemplazando las filas absorbidas del grupo.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateLinkedProduct))]
    private async Task UpdateLinkedProductAsync(SupplierInvoiceLineViewModel? line, CancellationToken cancellationToken)
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

        if (IsBusy || _isCreatingProduct || !line.CanUpdateLink)
        {
            return;
        }

        var invoiceId = StagedInvoice.Id;
        _isCreatingProduct = true;
        try
        {
            // La fila primaria es la primera del grupo en orden de presentación (construcción de
            // AssignExpandedLinkedGroups); el clic puede llegar desde cualquier miembro.
            var groupRows = string.IsNullOrWhiteSpace(line.LinkedGroupKey)
                ? new List<SupplierInvoiceLineViewModel> { line }
                : Lines
                    .Where(candidate => string.Equals(
                        candidate.LinkedGroupKey,
                        line.LinkedGroupKey,
                        StringComparison.Ordinal))
                    .ToList();
            var anchor = groupRows[0];
            var productIds = groupRows
                .Select(row => row.ResolvedProductId)
                .Where(productId => productId is not null)
                .Select(productId => productId!.Value)
                .Distinct()
                .ToList();

            IReadOnlyList<ProductDto> fetchedProducts;
            try
            {
                fetchedProducts = await _invoiceService.GetCatalogProductsByIdsAsync(productIds, cancellationToken);
            }
            catch (Exception exception)
            {
                StatusMessage = $"No se pudieron cargar los productos vinculados: {exception.Message}";
                _dialogService.ShowError("Error al cargar los productos vinculados", StatusMessage);
                return;
            }

            var productsById = fetchedProducts.ToDictionary(product => product.Id);
            if (productIds.Any(productId => !productsById.ContainsKey(productId)))
            {
                // 8.175-S6: si un producto del vínculo ya no resuelve en el catálogo, se aborta la
                // edición en vez de guardar una re-resolución que perdería el producto eliminado.
                StatusMessage = "No se pudieron cargar todos los productos vinculados de la línea. Revise el catálogo e intente nuevamente.";
                _dialogService.ShowError("Error al cargar los productos vinculados", StatusMessage);
                return;
            }

            var linkSeeds = new List<InvoiceProductLinkSeed>(groupRows.Count);
            foreach (var groupRow in groupRows)
            {
                if (groupRow.ResolvedProductId is not int groupProductId
                    || !productsById.TryGetValue(groupProductId, out var groupProduct))
                {
                    continue;
                }

                linkSeeds.Add(new InvoiceProductLinkSeed(
                    groupProduct,
                    groupRow.MarginRetailOverride > 0m ? groupRow.MarginRetailOverride : (decimal?)null,
                    groupRow.MarginWholesaleOverride > 0m ? groupRow.MarginWholesaleOverride : (decimal?)null));
            }

            var dialogViewModel = new CreateInvoiceProductDialogViewModel(
                anchor,
                IsBsSInvoice,
                (query, token) => _invoiceService.SearchCatalogProductsAsync(query, token),
                linkSeeds: linkSeeds);
            if (_dialogService.ShowCreateInvoiceProductDialog(dialogViewModel) != true)
            {
                return;
            }

            var editedLineIds = groupRows.Select(row => row.LineId).ToHashSet();
            var replacedLineIds = groupRows.Skip(1).Select(row => row.LineId).ToArray();
            var previousLinesById = Lines.ToDictionary(item => item.LineId);

            if (dialogViewModel.IsLinkExistingMode)
            {
                var selectedProducts = dialogViewModel.GetEffectiveSelectedProducts();
                if (selectedProducts.Count == 0)
                {
                    StatusMessage = "Seleccione al menos un producto del catálogo para actualizar el vínculo.";
                    return;
                }

                var selectedProductIds = selectedProducts
                    .Select(product => product.Id)
                    .Distinct()
                    .ToArray();
                var marginOverrides = dialogViewModel.GetSelectedMarginOverrides();
                IsBusy = true;
                StatusMessage = "Actualizando el vínculo con el catálogo...";
                try
                {
                    // 8.175-S5: el re-vínculo nunca re-apunta la descripción (el nombre previo
                    // pertenece al vínculo anterior); solo viajan márgenes y filas reemplazadas.
                    var linkRequest = selectedProductIds.Length > 1
                        ? new LinkInvoiceProductRequestDto(
                            selectedProducts[0].Id,
                            null,
                            selectedProductIds,
                            marginOverrides,
                            replacedLineIds.Length > 0 ? replacedLineIds : null)
                        : new LinkInvoiceProductRequestDto(
                            selectedProducts[0].Id,
                            null,
                            null,
                            marginOverrides,
                            replacedLineIds.Length > 0 ? replacedLineIds : null);
                    var updatedInvoice = await _invoiceService.LinkProductToLineAsync(
                        invoiceId,
                        anchor.LineId,
                        linkRequest,
                        cancellationToken);
                    LoadStagedInvoice(updatedInvoice);
                    PreserveUneditedLineEdits(previousLinesById, editedLineIds);
                    StatusMessage = $"Vínculo actualizado: {selectedProducts.Count} producto(s).";
                }
                catch (Exception exception)
                {
                    StatusMessage = $"No se pudo actualizar el vínculo: {exception.Message}";
                    _dialogService.ShowError("Error al actualizar el vínculo", StatusMessage);
                }
                finally
                {
                    IsBusy = false;
                }

                return;
            }

            var barcode = dialogViewModel.Barcode.Trim();
            var productName = dialogViewModel.Name.Trim();
            IsBusy = true;
            StatusMessage = "Creando el producto en el catálogo...";
            try
            {
                var updatedInvoice = await _invoiceService.CreateProductFromLineAsync(
                    invoiceId,
                    anchor.LineId,
                    new CreateInvoiceProductRequestDto(
                        barcode,
                        productName,
                        replacedLineIds.Length > 0 ? replacedLineIds : null),
                    cancellationToken);
                LoadStagedInvoice(updatedInvoice);
                PreserveUneditedLineEdits(previousLinesById, editedLineIds);
                StatusMessage = $"Producto '{productName}' creado y vinculado. Alias guardado para futuras facturas.";
            }
            catch (Exception exception)
            {
                StatusMessage = $"No se pudo actualizar el vínculo: {exception.Message}";
                _dialogService.ShowError("Error al actualizar el vínculo", StatusMessage);
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            _isCreatingProduct = false;
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// 8.175-S7: tras recargar la factura, las filas ajenas al grupo editado conservan sus
    /// ediciones en memoria; las filas editadas/creadas quedan con el estado servido por el DTO.
    /// </summary>
    private void PreserveUneditedLineEdits(
        Dictionary<int, SupplierInvoiceLineViewModel> previousLinesById,
        HashSet<int> editedLineIds)
    {
        foreach (var reloadedLine in Lines)
        {
            if (editedLineIds.Contains(reloadedLine.LineId))
            {
                continue;
            }

            if (previousLinesById.TryGetValue(reloadedLine.LineId, out var previousLine))
            {
                reloadedLine.PreserveEditsFrom(previousLine);
            }
        }
    }
}
