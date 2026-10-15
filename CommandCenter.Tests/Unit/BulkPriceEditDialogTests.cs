using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.181-T2 (S5): flujo del diálogo de edición masiva de precios (configuración de operaciones →
/// vista previa → aplicación con confirmación), validaciones client-side y errores del servidor.
/// </summary>
public class BulkPriceEditDialogTests
{
    private readonly Mock<IProductService> _productServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();

    private BulkPriceEditDialogViewModel CreateViewModel(BulkPriceSelection? selection = null)
        => new(
            _productServiceMock.Object,
            _dialogServiceMock.Object,
            selection ?? ExplicitSelection(1, 2, 3));

    private static BulkPriceSelection ExplicitSelection(params int[] ids)
        => new(ids.ToList(), null, false, ids.Length);

    private static BulkPriceSelection FilterSelection(int selectedCount) => new(
        null,
        new BulkPriceSelectionFilterDto { SearchText = "arroz", StatusFilter = "active", CategoryId = 7 },
        SelectAllFiltered: true,
        selectedCount);

    private static BulkPriceUpdateResultDto PreviewResult(int updatedCount = 2, int skippedCount = 1, bool withErrorRow = false)
    {
        var rows = new List<BulkPricePreviewRowDto>
        {
            new()
            {
                ProductId = 1,
                SKU = "A-1",
                Name = "Arroz",
                Changes =
                {
                    new BulkPriceFieldChangeDto
                    {
                        Field = BulkPriceField.RetailPrice,
                        BeforeValue = 1.20m,
                        AfterValue = 1.50m,
                        BeforeBsS = 60m,
                        AfterBsS = 75m
                    }
                }
            },
            new()
            {
                ProductId = 2,
                SKU = "B-2",
                Name = "Harina",
                Changes =
                {
                    new BulkPriceFieldChangeDto
                    {
                        Field = BulkPriceField.WholesalePrice,
                        BeforeValue = 1.00m,
                        AfterValue = 1.20m
                    }
                }
            }
        };

        rows.Add(withErrorRow
            ? new BulkPricePreviewRowDto
            {
                ProductId = 3,
                SKU = "C-3",
                Name = "Aceite",
                SkipReason = "El valor resultante no puede ser negativo (precio al detal).",
                IsError = true
            }
            : new BulkPricePreviewRowDto
            {
                ProductId = 3,
                SKU = "C-3",
                Name = "Aceite",
                SkipReason = "Sin cambios: los valores resultantes son iguales a los actuales."
            });

        return new BulkPriceUpdateResultDto
        {
            ExchangeRate = 50m,
            SelectedCount = updatedCount + skippedCount,
            UpdatedCount = updatedCount,
            SkippedCount = skippedCount,
            Rows = rows
        };
    }

    private static void EnableOperation(
        BulkPriceEditDialogViewModel viewModel,
        BulkPriceField field,
        BulkPriceMode mode,
        string valueText)
    {
        var row = viewModel.Operations.Single(r => r.Field == field);
        row.IsEnabled = true;
        row.SelectedMode = mode;
        row.ValueText = valueText;
    }

    private void SetupPreview(BulkPriceUpdateResultDto? result = null, Action<BulkPriceUpdateRequestDto>? capture = null)
    {
        var setup = _productServiceMock.Setup(s => s.PreviewBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()));

        if (capture is not null)
        {
            setup.Callback((BulkPriceUpdateRequestDto request, CancellationToken _) => capture(request));
        }

        setup.ReturnsAsync(result ?? PreviewResult());
    }

    private void SetupApply(BulkPriceUpdateResultDto? result = null, Action<BulkPriceUpdateRequestDto>? capture = null)
    {
        var setup = _productServiceMock.Setup(s => s.ApplyBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()));

        if (capture is not null)
        {
            setup.Callback((BulkPriceUpdateRequestDto request, CancellationToken _) => capture(request));
        }

        setup.ReturnsAsync(result ?? PreviewResult());
    }

    [Fact]
    public void Operations_ExposeTheFiveEditableFields_DisabledByDefault()
    {
        var vm = CreateViewModel();

        Assert.Equal(5, vm.Operations.Count);
        Assert.Equal(
            new[]
            {
                BulkPriceField.RetailPrice,
                BulkPriceField.WholesalePrice,
                BulkPriceField.RetailMargin,
                BulkPriceField.WholesaleMargin,
                BulkPriceField.CostPrice
            },
            vm.Operations.Select(r => r.Field).ToArray());
        Assert.All(vm.Operations, row => Assert.False(row.IsEnabled));
        Assert.All(vm.Operations, row => Assert.Equal(BulkPriceMode.SetFixed, row.SelectedMode));
        Assert.All(vm.Operations, row => Assert.Equal(3, row.ModeOptions.Count));
    }

    [Fact]
    public async Task Preview_WithoutEnabledOperations_ShowsValidationErrorAndDoesNotCallService()
    {
        var vm = CreateViewModel();

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(BulkPriceEditDialogViewModel.NoOperationsMessage, vm.ErrorMessage);
        Assert.False(vm.HasPreview);
        _productServiceMock.Verify(s => s.PreviewBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("")]
    public async Task Preview_WithInvalidOrNegativeValue_ShowsValidationError(string valueText)
    {
        var vm = CreateViewModel();
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, valueText);

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(BulkPriceEditDialogViewModel.InvalidValueMessage, vm.ErrorMessage);
        Assert.False(vm.HasPreview);
        _productServiceMock.Verify(s => s.PreviewBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Preview_WithCommaDecimal_ParsesValueWithInvariantDecimalSeparator()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel();
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "10,5");

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        var operation = Assert.Single(captured!.Operations);
        Assert.Equal(10.5m, operation.Value);
    }

    [Fact]
    public async Task Preview_SendsExplicitSelectionAndOperations_AndMapsResult()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel(ExplicitSelection(3, 1, 2));
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.IncreasePercent, "10");
        EnableOperation(vm, BulkPriceField.CostPrice, BulkPriceMode.SetFixed, "0.90");

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(new[] { 3, 1, 2 }, captured!.ProductIds);
        Assert.False(captured.SelectAllFiltered);
        Assert.Null(captured.Filter);
        Assert.Equal(2, captured.Operations.Count);
        Assert.Contains(captured.Operations, op =>
            op.Field == BulkPriceField.RetailPrice
            && op.Mode == BulkPriceMode.IncreasePercent
            && op.Value == 10m);
        Assert.Contains(captured.Operations, op =>
            op.Field == BulkPriceField.CostPrice
            && op.Mode == BulkPriceMode.SetFixed
            && op.Value == 0.90m);

        Assert.True(vm.HasPreview);
        Assert.False(vm.HasPreviewErrors);
        Assert.Equal(3, vm.PreviewRows.Count);
        Assert.Equal(2, vm.PreviewUpdatedCount);
        Assert.Equal(1, vm.PreviewSkippedCount);
        Assert.Equal(50m, vm.ExchangeRate);

        var updatedRow = vm.PreviewRows.Single(r => r.ProductId == 1);
        Assert.False(updatedRow.IsError);
        Assert.Null(updatedRow.SkipReason);
        var change = Assert.Single(updatedRow.Changes);
        Assert.Equal(BulkPriceField.RetailPrice, change.Field);
        Assert.Equal(1.20m, change.BeforeValue);
        Assert.Equal(1.50m, change.AfterValue);

        var skippedRow = vm.PreviewRows.Single(r => r.ProductId == 3);
        Assert.Contains("Sin cambios", skippedRow.SkipReason);
    }

    [Fact]
    public async Task Preview_WithFilterSelection_SendsSelectAllFilteredInsteadOfProductIds()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel(FilterSelection(selectedCount: 120));
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.IncreasePercent, "5");

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.True(captured!.SelectAllFiltered);
        Assert.Null(captured.ProductIds);
        Assert.NotNull(captured.Filter);
        Assert.Equal("arroz", captured.Filter!.SearchText);
        Assert.Equal("active", captured.Filter.StatusFilter);
        Assert.Equal(7, captured.Filter.CategoryId);
        Assert.True(vm.IsSelectAllFiltered);
        Assert.Equal(120, vm.SelectedCount);
    }

    [Fact]
    public async Task Preview_WhenServiceThrows_ShowsServerMessageWithoutPreview()
    {
        _productServiceMock
            .Setup(s => s.PreviewBulkPricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("La selección excede el máximo permitido (1000 productos)."));

        var vm = CreateViewModel();
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "2");

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Contains("1000", vm.ErrorMessage);
        Assert.False(vm.HasPreview);
        Assert.False(vm.CanApply);
        Assert.Empty(vm.PreviewRows);
    }

    [Fact]
    public async Task Preview_WithErrorRows_BlocksApplyAndExposesRowState()
    {
        SetupPreview(PreviewResult(withErrorRow: true));

        var vm = CreateViewModel();
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "2");

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.True(vm.HasPreview);
        Assert.True(vm.HasPreviewErrors);
        Assert.False(vm.CanApply);
        var errorRow = vm.PreviewRows.Single(r => r.IsError);
        Assert.Equal(3, errorRow.ProductId);
        Assert.Contains("negativo", errorRow.SkipReason);
    }

    [Fact]
    public async Task Preview_WithNoEffectiveChanges_DisablesApply()
    {
        SetupPreview(PreviewResult(updatedCount: 0, skippedCount: 2));

        var vm = CreateViewModel();
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "2");

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.True(vm.HasPreview);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task Apply_WithoutPreview_DoesNothing()
    {
        var vm = CreateViewModel();
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "2");

        Assert.False(vm.CanApply);
        await vm.ApplyCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _productServiceMock.Verify(s => s.ApplyBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(vm.HasApplied);
    }

    [Fact]
    public async Task Apply_AfterPreview_ConfirmsAndReusesPreviewedRequest()
    {
        BulkPriceUpdateRequestDto? previewRequest = null;
        BulkPriceUpdateRequestDto? applyRequest = null;
        SetupPreview(capture: request => previewRequest = request);
        SetupApply(capture: request => applyRequest = request);
        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var vm = CreateViewModel(ExplicitSelection(1, 2, 3));
        bool? closeResult = null;
        vm.RequestClose = applied => closeResult = applied;

        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.IncreasePercent, "10");
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.CanApply);

        await vm.ApplyCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.Same(previewRequest, applyRequest);
        Assert.True(vm.HasApplied);
        Assert.Equal(2, vm.AppliedCount);
        Assert.True(closeResult);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task Apply_WhenConfirmationDeclined_DoesNotCallService()
    {
        SetupPreview();
        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var vm = CreateViewModel();
        bool? closeResult = null;
        vm.RequestClose = applied => closeResult = applied;

        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "2");
        await vm.PreviewCommand.ExecuteAsync(null);

        await vm.ApplyCommand.ExecuteAsync(null);

        _productServiceMock.Verify(s => s.ApplyBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(vm.HasApplied);
        Assert.Null(closeResult);
    }

    [Fact]
    public async Task Apply_WhenServerThrows409_KeepsDialogOpenAndShowsMessage()
    {
        SetupPreview();
        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        _productServiceMock
            .Setup(s => s.ApplyBulkPricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(
                "No se aplicó la actualización masiva: los precios fueron modificados por otro usuario. Vuelva a cargar el catálogo e intente nuevamente.",
                null,
                HttpStatusCode.Conflict));

        var vm = CreateViewModel();
        bool? closeResult = null;
        vm.RequestClose = applied => closeResult = applied;

        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "2");
        await vm.PreviewCommand.ExecuteAsync(null);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Contains("modificados por otro usuario", vm.ErrorMessage);
        Assert.False(vm.HasApplied);
        Assert.Null(closeResult);
        Assert.True(vm.HasPreview);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task EditingOperationAfterPreview_InvalidatesPreview()
    {
        SetupPreview();

        var vm = CreateViewModel();
        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "2");
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreview);
        Assert.True(vm.CanApply);

        EnableOperation(vm, BulkPriceField.CostPrice, BulkPriceMode.SetFixed, "1");

        Assert.False(vm.HasPreview);
        Assert.False(vm.CanApply);
        Assert.Empty(vm.PreviewRows);
    }

    [Fact]
    public void Close_WithoutApply_ReturnsFalse()
    {
        var vm = CreateViewModel();
        bool? closeResult = null;
        vm.RequestClose = applied => closeResult = applied;

        vm.CloseCommand.Execute(null);

        Assert.False(closeResult);
    }

    [Fact]
    public void CanClose_IsFalseWhileBusy()
    {
        var vm = CreateViewModel();

        Assert.True(vm.CanClose);

        vm.IsBusy = true;
        Assert.False(vm.CanClose);

        vm.IsBusy = false;
        Assert.True(vm.CanClose);
    }

    [Fact]
    public async Task Close_WithApplyInFlight_IsBlocked_AndClosesAfterApplyCompletes()
    {
        // 8.181-T2 (fix verificación): cerrar con el apply en vuelo no debe perder el resultado
        // (ni habilitar reintentos que dupliquen operaciones porcentuales).
        var pending = new TaskCompletionSource<BulkPriceUpdateResultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupPreview();
        _productServiceMock
            .Setup(s => s.ApplyBulkPricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var vm = CreateViewModel();
        bool? closeResult = null;
        vm.RequestClose = applied => closeResult = applied;

        EnableOperation(vm, BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, "2");
        await vm.PreviewCommand.ExecuteAsync(null);

        var applyTask = vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        vm.CloseCommand.Execute(null);
        Assert.Null(closeResult);

        pending.SetResult(PreviewResult());
        await applyTask;

        Assert.True(vm.HasApplied);
        Assert.False(vm.IsBusy);
        Assert.True(closeResult);
    }
}
