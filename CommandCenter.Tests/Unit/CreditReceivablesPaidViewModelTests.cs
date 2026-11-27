using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// CxC WPF (Deudas Pagadas): subsección, modos factura/abono y slide-over de cronología con
/// tasas históricas. La cartera pendiente conserva su flujo.
/// </summary>
public class CreditReceivablesPaidViewModelTests
{
    private static PaidReceivableClientItemDto PaidRow() => new()
    {
        ObligationId = 12,
        InvoiceNumber = 288,
        SaleId = 288,
        CustomerName = "Ana Pérez",
        CustomerDocument = "V-12345678",
        IssuedAt = DateTime.UtcNow.AddDays(-20),
        DueDate = DateTime.UtcNow.AddDays(-5),
        PaidAt = DateTime.UtcNow.AddDays(-2),
        DaysToSettle = 18,
        SettledOnTime = false,
        OriginalAmountUsd = 60m,
        TotalPaidUsd = 60m,
        TotalPaidLocalHistorical = 3120m,
        PaymentsCount = 2,
        PaymentMethodsSummary = "Pago Móvil"
    };

    private static ReceivablePaymentTimelineClientDto Timeline() => new()
    {
        ObligationId = 12,
        InvoiceNumber = 288,
        SaleId = 288,
        CustomerName = "Ana Pérez",
        CustomerDocument = "V-12345678",
        Status = "Pagada",
        OriginalAmountUsd = 60m,
        TotalPaidUsd = 60m,
        BalanceUsd = 0m,
        IssuedAt = DateTime.UtcNow.AddDays(-20),
        DueDate = DateTime.UtcNow.AddDays(-5),
        Payments = new List<ObligationPaymentTimelineItemClientDto>
        {
            new()
            {
                PaymentId = 301,
                SequenceNumber = 1,
                PaidAt = DateTime.UtcNow.AddDays(-2),
                PaymentMethodName = "Pago Móvil",
                PaymentCurrency = "Bs.S",
                AmountPaidInCurrency = 3120m,
                ExchangeRateApplied = 52m,
                AmountCreditedUsd = 60m,
                RunningBalanceAfterUsd = 0m,
                Status = "Aplicado"
            }
        }
    };

    private static (CreditReceivablesViewModel Vm, Mock<ISalesService> Sales) Create()
    {
        var sales = new Mock<ISalesService>();
        var payments = new Mock<IPaymentService>();
        var dialogs = new Mock<IDialogService>();
        payments.Setup(service => service.GetActiveMethodsAsync())
            .ReturnsAsync(new[] { new PaymentMethodDto { Id = 1, Name = "Pago Móvil" } });
        sales.Setup(service => service.GetPaidReceivablesAsync(It.IsAny<PaidReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaidReceivablesClientDto
            {
                Items = new List<PaidReceivableClientItemDto> { PaidRow() },
                Summary = new PaidReceivablesSummaryClientDto
                {
                    PaidDebtsCount = 1,
                    TotalCollectedUsd = 60m,
                    TotalCollectedLocalHistorical = 3120m,
                    TotalPaymentsCount = 2,
                    AverageDaysToSettle = 18m
                },
                TotalCount = 1,
                Page = 1,
                PageSize = 25
            });
        sales.Setup(service => service.GetPaidReceivablePaymentsAsync(It.IsAny<PaidReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaidReceivablePaymentsClientDto
            {
                Items = new List<PaidReceivablePaymentItemClientDto>
                {
                    new()
                    {
                        PaymentId = 301,
                        PaidAt = DateTime.UtcNow.AddDays(-2),
                        CustomerName = "Ana Pérez",
                        CustomerDocument = "V-12345678",
                        PaymentMethodName = "Pago Móvil",
                        PaymentCurrency = "Bs.S",
                        AmountPaidInCurrency = 3120m,
                        AmountCreditedUsd = 60m,
                        ExchangeRateApplied = 52m,
                        ImputationMode = "FIFO",
                        InvoiceSummary = "F00288",
                        Allocations = new List<PaidPaymentAllocationClientDto>
                        {
                            new() { ObligationId = 12, InvoiceNumber = 288, AmountUsd = 60m, AmountBsS = 3120m }
                        }
                    }
                },
                Summary = new PaidReceivablesSummaryClientDto { PaidDebtsCount = 1, TotalCollectedUsd = 60m, TotalPaymentsCount = 1 },
                TotalCount = 1,
                Page = 1,
                PageSize = 25
            });
        sales.Setup(service => service.GetReceivablePaymentTimelineAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Timeline());

        return (new CreditReceivablesViewModel(sales.Object, payments.Object, dialogs.Object), sales);
    }

    [Fact]
    public async Task SelectPaid_LoadsPaidDebtsAndSummary()
    {
        var (vm, _) = Create();

        await vm.SelectPaidCommand.ExecuteAsync(null);

        Assert.True(vm.IsPaidSelected);
        Assert.False(vm.IsPendingSelected);
        Assert.Single(vm.PaidDebts);
        Assert.Equal(1, vm.PaidTotalCount);
        Assert.Equal("1", vm.PaidDebtsCountDisplay);
        Assert.Equal("Pago Móvil", vm.PaidDebts[0].PaymentMethodsSummary);
    }

    [Fact]
    public async Task SetPaidViewMode_Payment_LoadsFlatPaymentsLog()
    {
        var (vm, _) = Create();
        await vm.SelectPaidCommand.ExecuteAsync(null);

        await vm.SetPaidViewModeCommand.ExecuteAsync("payment");

        Assert.True(vm.IsPaidPaymentMode);
        Assert.False(vm.IsPaidInvoiceMode);
        Assert.Single(vm.PaidPayments);
        Assert.Equal(301, vm.PaidPayments[0].PaymentId);
    }

    [Fact]
    public async Task OpenTimeline_LoadsHistoricalPaymentsAndCloses()
    {
        var (vm, _) = Create();
        await vm.SelectPaidCommand.ExecuteAsync(null);
        var row = vm.PaidDebts[0];

        await vm.OpenTimelineCommand.ExecuteAsync(row);

        Assert.True(vm.IsTimelineOpen);
        Assert.NotNull(vm.Timeline);
        Assert.Equal(12, vm.Timeline!.ObligationId);
        Assert.Single(vm.Timeline.Payments);
        Assert.True(vm.Timeline.IsSettled);

        vm.CloseTimelineCommand.Execute(null);

        Assert.False(vm.IsTimelineOpen);
        Assert.Null(vm.Timeline);
    }
}
