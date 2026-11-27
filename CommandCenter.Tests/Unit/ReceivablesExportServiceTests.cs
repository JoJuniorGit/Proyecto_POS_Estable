using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Moq;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Exportación server-side de Deudas Pagadas y constancia de cancelación: CSV/XLSX/PDF con los
/// snapshots históricos, sin recalcular tasas.
/// </summary>
public class ReceivablesExportServiceTests
{
    private static Mock<ICreditService> CreateCreditService()
    {
        var credit = new Mock<ICreditService>();
        credit.Setup(service => service.GetPaidReceivablesAsync(It.IsAny<PaidReceivablesFilterDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaidReceivablesListDto
            {
                Items = new List<PaidReceivableItemDto>
                {
                    new()
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
                    }
                },
                Summary = new PaidReceivablesSummaryDto
                {
                    PaidDebtsCount = 1,
                    TotalCollectedUsd = 60m,
                    TotalCollectedLocalHistorical = 3120m,
                    TotalPaymentsCount = 2,
                    AverageDaysToSettle = 18m
                },
                TotalCount = 1,
                Page = 1,
                PageSize = 25,
                HasMore = false
            });

        credit.Setup(service => service.GetReceivablePaymentTimelineAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceivablePaymentTimelineDto
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
                Payments = new List<ObligationPaymentTimelineItemDto>
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
            });

        return credit;
    }

    [Fact]
    public async Task ExportPaid_Csv_ContainsHistoricalHeaderAndRow()
    {
        var service = new ReceivablesExportService(CreateCreditService().Object);

        var file = await service.ExportPaidAsync(new PaidReceivablesFilterDto(), "invoice", "csv");

        string text = Encoding.UTF8.GetString(file.Content);
        Assert.Contains("Total pagado USD", text);
        Assert.Contains("F00288", text);
        Assert.Contains("Ana Pérez", text);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
    }

    [Fact]
    public async Task ExportPaid_Xlsx_And_Pdf_AreBinaryDocuments()
    {
        var service = new ReceivablesExportService(CreateCreditService().Object);

        var xlsx = await service.ExportPaidAsync(new PaidReceivablesFilterDto(), "invoice", "xlsx");
        var pdf = await service.ExportPaidAsync(new PaidReceivablesFilterDto(), "invoice", "pdf");

        Assert.True(xlsx.Content.Length > 0);
        Assert.Equal((byte)'P', xlsx.Content[0]);
        Assert.Equal((byte)'K', xlsx.Content[1]);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Content, 0, 4));
        Assert.Equal("application/pdf", pdf.ContentType);
    }

    [Fact]
    public async Task ExportStatement_ReturnsCancellationPdfWithPayments()
    {
        var service = new ReceivablesExportService(CreateCreditService().Object);

        var file = await service.ExportStatementAsync(12);

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(file.Content, 0, 4));
        Assert.Contains("Constancia", file.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("application/pdf", file.ContentType);
    }
}
