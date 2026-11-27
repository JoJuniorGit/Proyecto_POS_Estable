using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Navigation;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// F4-WU1 (Paso 14): WPF rentabilidad dashboard. Covers KPI mapping, the Devengado/Percibido
/// toggle, coverage/empty/error states, exports through the file picker and the Admin/Manager
/// navigation policy. Cashier/Driver must never reach the report.
/// </summary>
public sealed class ProfitabilityViewModelTests
{
    private readonly Mock<IProfitabilityClientService> _service = new();
    private readonly Mock<IFilePickerDialog> _filePicker = new();
    private readonly Mock<IDialogService> _dialogService = new();

    private ProfitabilityViewModel CreateViewModel(UserRole role = UserRole.Admin)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Role = role, IsActive = true });
        return new ProfitabilityViewModel(
            _service.Object,
            _filePicker.Object,
            _dialogService.Object,
            session);
    }

    [Fact]
    public async Task LoadAsync_PopulatesKpisBreakdownAndCoverage()
    {
        _service
            .Setup(service => service.GetSummaryAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSummary());
        var vm = CreateViewModel();

        await vm.LoadAsync();

        Assert.True(vm.HasData);
        Assert.False(vm.HasError);
        Assert.False(vm.IsEmpty);
        Assert.Equal(46.50m, vm.AccruedNetProfitValue);
        Assert.Equal(54.50m, vm.RealizedNetProfitValue);
        Assert.NotEmpty(vm.UtilidadDevengadaDisplay);
        Assert.NotEmpty(vm.UtilidadPercibidaDisplay);
        Assert.NotEmpty(vm.UtilidadNetaDisplay);
        Assert.Single(vm.PaymentMethodRows);
        Assert.Single(vm.ProductRows);
        Assert.Single(vm.CategoryRows);
        Assert.Single(vm.SellerRows);
        Assert.Single(vm.Alerts);
        Assert.Equal(2, vm.SeriesBars.Count);
        Assert.True(vm.HasCoverageWarnings);
        Assert.Contains("73", vm.CostCoverageDisplay);
        Assert.Equal("Gastos globales no prorrateados.", vm.GlobalCostsNote);
    }

    [Fact]
    public async Task SetViewMode_AccruedAndRealized_ToggleSeriesVisibility()
    {
        _service
            .Setup(service => service.GetSummaryAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSummary());
        var vm = CreateViewModel();
        await vm.LoadAsync();

        vm.SetViewModeCommand.Execute(ProfitabilityViewMode.Accrued);

        Assert.All(vm.SeriesBars, bar => Assert.True(bar.ShowAccrued));
        Assert.All(vm.SeriesBars, bar => Assert.False(bar.ShowRealized));

        vm.SetViewModeCommand.Execute(ProfitabilityViewMode.Realized);

        Assert.All(vm.SeriesBars, bar => Assert.False(bar.ShowAccrued));
        Assert.All(vm.SeriesBars, bar => Assert.True(bar.ShowRealized));

        vm.SetViewModeCommand.Execute(ProfitabilityViewMode.Both);

        Assert.All(vm.SeriesBars, bar => Assert.True(bar.ShowAccrued && bar.ShowRealized));
    }

    [Fact]
    public async Task LoadAsync_WithEmptyBreakdown_SetsEmptyState()
    {
        _service
            .Setup(service => service.GetSummaryAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfitabilitySummaryClientDto
            {
                ReportCurrencyCode = "USD",
                ReportCurrencySymbol = "$",
                GlobalCostsNote = "Gastos globales no prorrateados."
            });
        var vm = CreateViewModel();

        await vm.LoadAsync();

        Assert.True(vm.HasData);
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.SeriesBars);
        Assert.Empty(vm.PaymentMethodRows);
    }

    [Fact]
    public async Task LoadAsync_WhenServiceFails_SetsErrorState()
    {
        _service
            .Setup(service => service.GetSummaryAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("servidor caído"));
        var vm = CreateViewModel();

        await vm.LoadAsync();

        Assert.True(vm.HasError);
        Assert.Contains("servidor caído", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasData);
    }

    [Fact]
    public async Task ExportCsvCommand_SavesServerBytesThroughPicker()
    {
        _service
            .Setup(service => service.GetSummaryAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSummary());
        _service
            .Setup(service => service.ExportAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                "csv",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfitabilityClientExport(
                new byte[] { 1, 2, 3 },
                "Estado_Resultados.csv",
                "text/csv"));
        var path = Path.Combine(Path.GetTempPath(), $"profitability-test-{Guid.NewGuid():N}.csv");
        _filePicker
            .Setup(picker => picker.PickSaveFilePath(
                "Exportar Rentabilidad",
                It.IsAny<string>(),
                "Estado_Resultados.csv"))
            .Returns(path);
        var vm = CreateViewModel();
        await vm.LoadAsync();

        try
        {
            await vm.ExportCsvCommand.ExecuteAsync(null);

            Assert.True(File.Exists(path));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            _service.Verify(service => service.ExportAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                "csv",
                It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task ExportCsvCommand_WhenPickerCancels_DoesNotThrow()
    {
        _service
            .Setup(service => service.GetSummaryAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSummary());
        _service
            .Setup(service => service.ExportAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                "csv",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfitabilityClientExport(
                new byte[] { 1 },
                "Estado_Resultados.csv",
                "text/csv"));
        _filePicker
            .Setup(picker => picker.PickSaveFilePath(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns((string?)null);
        var vm = CreateViewModel();
        await vm.LoadAsync();

        await vm.ExportCsvCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task SelectPresetCommand_LoadsRequestedPreset()
    {
        ProfitabilityClientFilter? captured = null;
        _service
            .Setup(service => service.GetSummaryAsync(
                It.IsAny<ProfitabilityClientFilter>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProfitabilityClientFilter, CancellationToken>((filter, _) => captured = filter)
            .ReturnsAsync(BuildSummary());
        var vm = CreateViewModel();

        await vm.SelectPresetCommand.ExecuteAsync("Week");

        Assert.NotNull(captured);
        Assert.Equal(ProfitabilityPeriodPresetClient.Week, captured!.Preset);
        Assert.Null(captured.FromDate);
        Assert.Null(captured.ToDate);
    }

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.Driver, false)]
    public void NavigationPolicy_Profitability_MatchesRole(UserRole role, bool expected)
    {
        Assert.Equal(expected, WpfNavigationPolicy.CanAccess(role, WpfNavigationPolicy.Profitability));
        Assert.Equal(expected, WpfNavigationPolicy.CanAccessCategory(role, WpfNavigationCategory.Administration));
    }

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.Driver, false)]
    public void CanViewProfitability_MatchesRole(UserRole role, bool expected)
    {
        var vm = CreateViewModel(role);

        Assert.Equal(expected, vm.CanViewProfitability);
        vm.Dispose();
    }

    [Fact]
    public async Task EnsureLoadedAsync_AsCashier_IsBlockedAndNeverCallsService()
    {
        var vm = CreateViewModel(UserRole.Cashier);

        await vm.EnsureLoadedAsync();

        Assert.False(vm.CanViewProfitability);
        Assert.True(vm.HasError);
        _service.Verify(service => service.GetSummaryAsync(
            It.IsAny<ProfitabilityClientFilter>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ProfitabilitySummaryClientDto BuildSummary() => new()
    {
        FromUtc = new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc),
        ToUtc = new DateTime(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc),
        TimeZoneId = "America/Caracas",
        ReportCurrencyCode = "USD",
        ReportCurrencySymbol = "$",
        IsSingleCurrency = false,
        GlobalCostsNote = "Gastos globales no prorrateados.",
        Accrued = new ProfitabilityAccruedClientDto
        {
            GrossSales = 250m,
            Cancellations = 50m,
            Returns = 10m,
            NetSales = 190m,
            KnownCostRevenue = 140m,
            UnknownCostRevenue = 50m,
            Cogs = 94m,
            GrossProfit = 46m,
            GrossMarginPercent = 32.8571m,
            ServiceCommissions = 10m,
            FxDifferenceInformational = 8m,
            PettyCashExpenses = 5m,
            PaymentMethodFees = 4.5m,
            NetProfit = 46.5m,
            NetMarginPercent = 33.2143m
        },
        Realized = new ProfitabilityRealizedClientDto
        {
            CollectedRevenue = 180m,
            KnownCostCollectedRevenue = 130m,
            UnknownCostCollectedRevenue = 50m,
            RealizedCogs = 84m,
            RealizedGrossProfit = 46m,
            RealizedGrossMarginPercent = 35.3846m,
            ServiceCommissions = 10m,
            FxDifference = 8m,
            PettyCashExpenses = 5m,
            PaymentMethodFees = 4.5m,
            NetProfit = 54.5m,
            NetMarginPercent = 41.9231m
        },
        Coverage = new ProfitabilityCoverageClientDto
        {
            CostCoveragePercent = 73.6842m,
            KnownCostRevenue = 140m,
            KnownCogs = 94m,
            UnknownCostRevenue = 50m,
            UnknownCostLinesCount = 1,
            FeeCoveragePercent = 100m,
            KnownFeePaymentAmount = 650m,
            UnknownFeePaymentAmount = 0m,
            UnknownFeePaymentsCount = 0
        },
        Breakdown = new ProfitabilityBreakdownClientDto
        {
            Series = new List<ProfitabilitySeriesPointClientDto>
            {
                new()
                {
                    Label = "02/11",
                    AccruedNetProfit = 46.5m,
                    RealizedNetProfit = 54.5m,
                    AccruedNetSales = 190m,
                    RealizedCollectedRevenue = 180m
                },
                new()
                {
                    Label = "03/11",
                    AccruedNetProfit = 10m,
                    RealizedNetProfit = -2m,
                    AccruedNetSales = 40m,
                    RealizedCollectedRevenue = 30m
                }
            },
            PaymentMethods = new List<PaymentMethodBreakdownClientDto>
            {
                new()
                {
                    PaymentMethodId = 2,
                    PaymentMethodName = "Punto de Venta",
                    CollectedAmount = 650m,
                    FeeAmount = 4.5m,
                    KnownFeeAmount = 650m,
                    UnknownFeeAmount = 0m,
                    FeeCoveragePercent = 100m
                }
            },
            Products = new List<ProductBreakdownClientDto>
            {
                new()
                {
                    ProductId = 1,
                    ProductName = "Producto A",
                    QuantitySold = 2m,
                    Revenue = 100m,
                    Cogs = 60m,
                    GrossProfit = 40m,
                    GrossMarginPercent = 40m
                }
            },
            Categories = new List<CategoryBreakdownClientDto>
            {
                new()
                {
                    CategoryId = 1,
                    CategoryName = "Categoría A",
                    Revenue = 100m,
                    Cogs = 60m,
                    GrossProfit = 40m,
                    GrossMarginPercent = 40m,
                    ContributionPercent = 100m
                }
            },
            Sellers = new List<SellerBreakdownClientDto>
            {
                new()
                {
                    CashierId = 1,
                    CashierName = "Cajero A",
                    NetSales = 190m,
                    GrossProfit = 46m
                }
            }
        },
        Alerts = new List<ProfitabilityAlertClientDto>
        {
            new()
            {
                Code = "MARGIN_EROSION",
                Severity = ProfitabilityAlertSeverityClient.Warning,
                Message = "El margen bruto cayó 5 puntos frente al período anterior.",
                CurrentValue = 32.8571m,
                PreviousValue = 38m,
                Delta = -5.1429m
            }
        }
    };
}
