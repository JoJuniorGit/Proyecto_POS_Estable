using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.177-T3 (S2/S3): Centro de Auditoría de Kardex en Desktop — atajos de fecha local→UTC,
/// construcción de filtros/consulta, KPIs, paginación server-side, exportación y RBAC.
/// </summary>
public sealed class StockKardexViewModelTests
{
    private readonly Mock<IStockKardexClientService> _kardexMock = new();
    private readonly Mock<IProductService> _productMock = new();
    private readonly Mock<IDialogService> _dialogMock = new();
    private readonly Mock<IFilePickerDialog> _filePickerMock = new();

    private StockKardexFilterDto? _lastMovementsFilter;
    private int _lastPage;
    private int _lastPageSize;
    private StockKardexFilterDto? _lastSummaryFilter;
    private StockKardexFilterDto? _lastExportFilter;
    private string? _lastExportFormat;

    private StockKardexViewModel CreateViewModel(UserSession? userSession = null)
    {
        _kardexMock
            .Setup(s => s.GetMovementsAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<StockKardexFilterDto, int, int, CancellationToken>((f, page, pageSize, _) =>
            {
                _lastMovementsFilter = f;
                _lastPage = page;
                _lastPageSize = pageSize;
            })
            .ReturnsAsync(new PagedResultDto<StockKardexRowDto>(Array.Empty<StockKardexRowDto>(), 0));

        _kardexMock
            .Setup(s => s.GetSummaryAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<CancellationToken>()))
            .Callback<StockKardexFilterDto, CancellationToken>((f, _) => _lastSummaryFilter = f)
            .ReturnsAsync(new StockKardexSummaryDto());

        _kardexMock
            .Setup(s => s.ExportAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<StockKardexFilterDto, string, CancellationToken>((f, format, _) =>
            {
                _lastExportFilter = f;
                _lastExportFormat = format;
            })
            .ReturnsAsync(new byte[] { 1, 2, 3 });

        return new StockKardexViewModel(
            _kardexMock.Object,
            _productMock.Object,
            _dialogMock.Object,
            _filePickerMock.Object,
            userSession);
    }

    private static UserSession CreateSession(UserRole role)
    {
        var session = new UserSession();
        session.CurrentUser = new UserDto { Id = 1, Name = "Usuario Test", Role = role };
        return session;
    }

    private static StockKardexRowDto CreateRow() => new()
    {
        Source = "Active",
        MovementId = 10,
        ProductId = 3,
        SKU = "759000123",
        ProductName = "Harina P.A.N.",
        MovementDateUtc = new DateTime(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc),
        QuantityChange = 2m,
        StockBefore = 10m,
        NewStockLevel = 12m,
        UnitCostUSD = 1.5m,
        TotalValueUSD = 3m,
        OriginType = StockMovementOriginType.Manual,
        ReasonCode = StockMovementReason.Damage,
        ExecutedByUserId = "user-1",
        SaleId = 77
    };

    [Fact]
    public async Task ApplyTodayCommand_SendsVenezuelaLocalDayAsUtcRange()
    {
        var vm = CreateViewModel();

        await vm.ApplyTodayCommand.ExecuteAsync(null);

        var filter = _lastMovementsFilter;
        Assert.NotNull(filter);
        var today = TimeZoneHelper.GetVenezuelaDate();
        var localDay = today.ToDateTime(TimeOnly.MinValue);
        var (expectedStart, expectedEndExclusive) = TimeZoneHelper.GetUtcRange(localDay, localDay);

        Assert.Equal(expectedStart, filter.DateFromUtc);
        Assert.Equal(expectedEndExclusive!.Value.AddTicks(-1), filter.DateToUtc);
        Assert.Equal(1, _lastPage);
        Assert.Equal(25, _lastPageSize);
    }

    [Fact]
    public async Task ApplyCurrentMonthCommand_StartsAtFirstLocalDayOfMonth()
    {
        var vm = CreateViewModel();

        await vm.ApplyCurrentMonthCommand.ExecuteAsync(null);

        var filter = _lastMovementsFilter;
        Assert.NotNull(filter);
        var today = TimeZoneHelper.GetVenezuelaDate();
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1).ToDateTime(TimeOnly.MinValue);
        var (expectedStart, _) = TimeZoneHelper.GetUtcRange(firstOfMonth, today.ToDateTime(TimeOnly.MinValue));

        Assert.Equal(expectedStart, filter.DateFromUtc);
    }

    [Fact]
    public async Task EnsureLoaded_MapsRowsSummaryAndUnvaluedWarning()
    {
        var vm = CreateViewModel();
        _kardexMock
            .Setup(s => s.GetMovementsAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<StockKardexFilterDto, int, int, CancellationToken>((f, page, pageSize, _) =>
            {
                _lastMovementsFilter = f;
                _lastPage = page;
                _lastPageSize = pageSize;
            })
            .ReturnsAsync(new PagedResultDto<StockKardexRowDto>(new[] { CreateRow() }, 1));
        _kardexMock
            .Setup(s => s.GetSummaryAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StockKardexSummaryDto
            {
                TotalMovements = 12,
                NetVolume = 5.5m,
                EntriesValueUsd = 100m,
                LossesValueUsd = 40m,
                NetValueUsd = 60m,
                UnvaluedMovements = 3
            });

        await vm.EnsureLoadedAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal("759000123 — Harina P.A.N.", row.ProductDisplay);
        Assert.Equal("Merma/Daño", row.ReasonLabel);
        Assert.Equal("Manual", row.OriginLabel);
        Assert.Equal("+2", row.DeltaText);
        Assert.Equal("#venta 77", row.ReferenceDisplay);
        Assert.Equal("$1,50", row.UnitCostDisplay);
        Assert.Equal("$3,00", row.TotalValueDisplay);
        Assert.Contains("00:00:00", row.DateLocalText);

        Assert.Equal("12", vm.TotalMovementsDisplay);
        Assert.Equal("5,5", vm.NetVolumeDisplay);
        Assert.Equal("$100,00", vm.EntriesValueDisplay);
        Assert.Equal("$40,00", vm.LossesValueDisplay);
        Assert.Equal("$60,00", vm.NetValueDisplay);
        Assert.True(vm.HasUnvaluedMovements);
        Assert.Equal("3 movimientos sin costo histórico, excluidos de la valorización", vm.UnvaluedWarningText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task NextPageCommand_AdvancesPageWithServerTotals()
    {
        var vm = CreateViewModel();
        _kardexMock
            .Setup(s => s.GetMovementsAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<StockKardexFilterDto, int, int, CancellationToken>((f, page, pageSize, _) =>
            {
                _lastMovementsFilter = f;
                _lastPage = page;
                _lastPageSize = pageSize;
            })
            .ReturnsAsync(new PagedResultDto<StockKardexRowDto>(new[] { CreateRow() }, 60));

        await vm.EnsureLoadedAsync();
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(3, vm.TotalPages);
        Assert.False(vm.CanGoPrevious);
        Assert.True(vm.CanGoNext);
        Assert.Contains("60 movimientos", vm.PageSummary, StringComparison.Ordinal);
        Assert.True(vm.CanExport);

        await vm.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(2, _lastPage);
        Assert.True(vm.CanGoPrevious);
        Assert.True(vm.CanGoNext);

        await vm.PreviousPageCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(1, _lastPage);
        Assert.False(vm.CanGoPrevious);
    }

    [Fact]
    public async Task ApplyFilters_UsesReasonOriginDirectionAndProductSelections()
    {
        var vm = CreateViewModel();
        vm.SelectedReason = vm.ReasonOptions.Single(o => o.Value == StockMovementReason.Damage);
        vm.SelectedOrigin = vm.OriginOptions.Single(o => o.Value == StockMovementOriginType.Manual);
        vm.SelectedDirection = vm.DirectionOptions.Single(o => o.Value == StockKardexDirection.Out);
        vm.SelectedProduct = new ProductQuickInfoDto { Id = 9, SKU = "759000123", Name = "Harina P.A.N." };

        await vm.ApplyFiltersCommand.ExecuteAsync(null);

        var filter = _lastMovementsFilter;
        Assert.NotNull(filter);
        Assert.Equal(9, filter.ProductId);
        Assert.Equal(new[] { StockMovementReason.Damage }, filter.ReasonCodes);
        Assert.Equal(new[] { StockMovementOriginType.Manual }, filter.OriginTypes);
        Assert.Equal(StockKardexDirection.Out, filter.Direction);
        Assert.Equal(1, _lastPage);
    }

    [Fact]
    public async Task ClearFiltersCommand_ResetsAllDimensionsToDefaults()
    {
        var vm = CreateViewModel();
        vm.SelectedReason = vm.ReasonOptions.Single(o => o.Value == StockMovementReason.Damage);
        vm.SelectedOrigin = vm.OriginOptions.Single(o => o.Value == StockMovementOriginType.Import);
        vm.SelectedDirection = vm.DirectionOptions.Single(o => o.Value == StockKardexDirection.In);
        vm.SelectedProduct = new ProductQuickInfoDto { Id = 9 };

        await vm.ClearFiltersCommand.ExecuteAsync(null);

        var filter = _lastMovementsFilter;
        Assert.NotNull(filter);
        Assert.Null(filter.ProductId);
        Assert.Null(filter.ReasonCodes);
        Assert.Null(filter.OriginTypes);
        Assert.Equal(StockKardexDirection.All, filter.Direction);
        Assert.Null(vm.SelectedProduct);
        Assert.Null(vm.SelectedReason.Value);
        Assert.Null(vm.SelectedOrigin.Value);
        Assert.Equal(StockKardexDirection.All, vm.SelectedDirection.Value);
        Assert.True(vm.DateFrom.HasValue);
        Assert.True(vm.DateTo.HasValue);
    }

    [Fact]
    public async Task ExportExcelCommand_UsesXlsxFormatAndWritesPickedBytes()
    {
        var vm = CreateViewModel();
        var path = Path.Combine(Path.GetTempPath(), $"kardex-test-{Guid.NewGuid():N}.xlsx");
        _filePickerMock
            .Setup(f => f.PickSaveFilePath("Exportar Kardex", It.IsAny<string>(), It.IsAny<string>()))
            .Returns(path);

        try
        {
            await vm.ExportExcelCommand.ExecuteAsync(null);

            Assert.Equal("xlsx", _lastExportFormat);
            Assert.NotNull(_lastExportFilter);
            Assert.True(File.Exists(path));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ExportCsvCommand_WhenCashierSession_IsBlockedAndDoesNotCallService()
    {
        var vm = CreateViewModel(CreateSession(UserRole.Cashier));

        await vm.ExportCsvCommand.ExecuteAsync(null);

        Assert.False(vm.HasAccess);
        Assert.False(vm.CanExport);
        Assert.Null(_lastExportFormat);
        _filePickerMock.Verify(f => f.PickSaveFilePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EnsureLoaded_WhenCashierSession_DoesNotCallServiceAndReportsAccessError()
    {
        var vm = CreateViewModel(CreateSession(UserRole.Cashier));

        await vm.EnsureLoadedAsync();

        Assert.False(vm.HasAccess);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.Rows);
        Assert.Null(_lastMovementsFilter);
        Assert.Null(_lastSummaryFilter);
    }

    [Fact]
    public async Task Refresh_WhenServiceFails_SetsNonBlockingErrorMessage()
    {
        var vm = CreateViewModel();
        _kardexMock
            .Setup(s => s.GetMovementsAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("backend caído"));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("backend caído", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task StockKardexClientService_GetMovementsAsync_BuildsEnumNameQueryWithPaging()
    {
        var handler = new StubHttpMessageHandler(JsonResponse("{\"items\":[],\"totalCount\":0,\"hasMore\":false}"));
        var service = new StockKardexClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        await service.GetMovementsAsync(
            new StockKardexFilterDto
            {
                DateFromUtc = new DateTime(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc),
                ProductId = 9,
                ReasonCodes = new[] { StockMovementReason.Damage, StockMovementReason.Return },
                OriginTypes = new[] { StockMovementOriginType.Manual },
                Direction = StockKardexDirection.Out
            },
            page: 2,
            pageSize: 25);

        var query = Uri.UnescapeDataString(Assert.Single(handler.RequestPaths));
        Assert.Contains("/api/stock-movements?", query, StringComparison.Ordinal);
        Assert.Contains("dateFromUtc=2026-10-12T04:00:00.0000000Z", query, StringComparison.Ordinal);
        Assert.Contains("productId=9", query, StringComparison.Ordinal);
        Assert.Contains("reasonCodes=Damage,Return", query, StringComparison.Ordinal);
        Assert.Contains("originTypes=Manual", query, StringComparison.Ordinal);
        Assert.Contains("direction=2", query, StringComparison.Ordinal);
        Assert.Contains("page=2", query, StringComparison.Ordinal);
        Assert.Contains("pageSize=25", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StockKardexClientService_GetSummaryAsync_MapsCamelCaseJson()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            "{\"totalMovements\":12,\"netVolume\":5.5,\"entriesValueUsd\":100,\"lossesValueUsd\":40,\"netValueUsd\":60,\"unvaluedMovements\":3}"));
        var service = new StockKardexClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        var summary = await service.GetSummaryAsync(new StockKardexFilterDto { UserId = "cashier-1" });

        Assert.Equal(12, summary.TotalMovements);
        Assert.Equal(5.5m, summary.NetVolume);
        Assert.Equal(100m, summary.EntriesValueUsd);
        Assert.Equal(40m, summary.LossesValueUsd);
        Assert.Equal(60m, summary.NetValueUsd);
        Assert.Equal(3, summary.UnvaluedMovements);
        Assert.Contains("/api/stock-movements/summary?", handler.RequestPaths[0], StringComparison.Ordinal);
        Assert.Contains("userId=cashier-1", Uri.UnescapeDataString(handler.RequestPaths[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StockKardexClientService_ExportAsync_SendsFormatAndReturnsBytes()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 7, 8, 9 })
        });
        var service = new StockKardexClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        var bytes = await service.ExportAsync(new StockKardexFilterDto(), "pdf");

        Assert.Equal(new byte[] { 7, 8, 9 }, bytes);
        Assert.Contains("/api/stock-movements/export?", handler.RequestPaths[0], StringComparison.Ordinal);
        Assert.Contains("format=pdf", handler.RequestPaths[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StockKardexClientService_WhenServerFails_ThrowsWithParsedProblemDetails()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"title\":\"reasonCodes inválidos\"}", Encoding.UTF8, "application/json")
        });
        var service = new StockKardexClientService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        var ex = await Assert.ThrowsAsync<Exception>(() => service.GetMovementsAsync(new StockKardexFilterDto(), 1, 25));

        Assert.Contains("reasonCodes inválidos", ex.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
