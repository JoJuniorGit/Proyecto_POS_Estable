using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CleanCodePhase3Tests
{
    [Fact]
    public void SaleRecoveryStore_Save_WhenDirectoryCannotBeCreated_LogsErrorGracefully()
    {
        // An invalid path with illegal characters that will trigger an exception
        string illegalPath = @"Z:\NonExistentDrive_" + Guid.NewGuid().ToString("N") + @"\sub\recovery.json";
        var store = new SaleRecoveryStore(illegalPath);

        var snapshot = new SaleRecoverySnapshot
        {
            SaleId = 1,
            ItemCount = 1,
            TotalUSD = 10m,
            SavedAtUtc = DateTime.UtcNow
        };

        // Must not throw because the catch block logs with ClientStateLogger
        var exception = Record.Exception(() => store.Save(snapshot));
        Assert.Null(exception);
    }

    [Fact]
    public void SaleRecoveryStore_Load_WhenCorruptJson_LogsErrorAndReturnsNullGracefully()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"corrupt_snapshot_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, "INVALID_CORRUPTED_JSON_DATA{{{");
            var store = new SaleRecoveryStore(tempFile);

            var result = store.Load();
            Assert.Null(result);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void SaleRecoveryStore_Clear_WhenFileLockedOrInvalid_DoesNotThrow()
    {
        string illegalPath = @"Z:\NonExistentDrive_" + Guid.NewGuid().ToString("N") + @"\recovery.json";
        var store = new SaleRecoveryStore(illegalPath);

        var exception = Record.Exception(() => store.Clear());
        Assert.Null(exception);
    }

    [Fact]
    public async Task PendingOrdersViewModel_EditarCommand_WhenReleaseThrows_SwallowsGracefullyWithLog()
    {
        var salesMock = new Mock<ISalesService>();
        var rateMock = new Mock<IExchangeRateService>();
        var paymentMock = new Mock<IPaymentService>();
        var dialogMock = new Mock<IDialogService>();

        var sale = new SaleDto { Id = 88, Status = "Pending" };

        salesMock.Setup(s => s.ClaimSaleAsync(sale.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale);
        salesMock.Setup(s => s.ReleaseSaleAsync(sale.Id, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("Network socket timeout during release"));

        dialogMock.Setup(d => d.ShowEditSaleDialogAsync(sale, It.IsAny<decimal>()))
            .ReturnsAsync((false, null!));

        var vm = new PendingOrdersViewModel(
            salesMock.Object,
            rateMock.Object,
            paymentMock.Object,
            dialogMock.Object,
            userSession: null);

        // Execute Editar command
        var cmd = vm.EditarCommand;
        Assert.True(cmd.CanExecute(sale));

        var exception = await Record.ExceptionAsync(() => cmd.ExecuteAsync(sale));
        Assert.Null(exception);

        // Verify Release was indeed attempted
        salesMock.Verify(s => s.ReleaseSaleAsync(sale.Id, false, It.IsAny<CancellationToken>()), Times.Once);
    }
}
