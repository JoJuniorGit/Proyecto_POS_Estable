using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Logging;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.152 / CLEAN-06 (S2): los catches operativos de BaseViewModel, InventoryViewModel,
/// InventoryViewModel.Operations y ProductDialogViewModel dejan rastro en
/// ClientStateLogger y conservan/proveen la notificación al operador. Los swallows de
/// ciclo de vida (cancelación/dispose de CTS) permanecen silenciosos por diseño.
/// </summary>
public class ViewModelCatchLoggingTests
{
    private const string MetadataWarningTitle = "Error de Metadatos";
    private const string MetadataWarningMessage = "No se pudieron cargar los datos auxiliares del producto. Verifique la conexión.";

    // ---------------------------------------------------------------------
    // InventoryViewModel
    // ---------------------------------------------------------------------

    [Fact]
    public async Task InventoryViewModel_Refresh_WhenExchangeRateFails_LogsAndKeepsErrorDialog()
    {
        string marker = $"refresh-boom-{Guid.NewGuid():N}";
        var productService = new Mock<IProductService>();
        var rateService = new Mock<IExchangeRateService>();
        rateService.SetupGet(r => r.CurrentRate).Returns(36.50m);
        rateService.Setup(r => r.GetCurrentRateAsync()).ThrowsAsync(new InvalidOperationException(marker));
        var dialog = new Mock<IDialogService>();

        using var vm = new InventoryViewModel(productService.Object, rateService.Object, new UserSession(), dialog.Object);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        await vm.RefreshCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowError("Error de Actualización", It.Is<string>(s => s.Contains(marker))), Times.Once);
        Assert.False(vm.IsRefreshing);

        var log = ReadResilienceLogUntil(marker);
        Assert.Contains(marker, log);
        Assert.Contains(nameof(InventoryViewModel), log);
    }

    // ---------------------------------------------------------------------
    // InventoryViewModel.Operations
    // ---------------------------------------------------------------------

    [Fact]
    public async Task InventoryViewModel_TogglePauseProduct_WhenStatusUpdateFails_LogsAndKeepsErrorDialog()
    {
        string marker = $"pause-boom-{Guid.NewGuid():N}";
        var productService = new Mock<IProductService>();
        productService.Setup(p => p.SetStatusAsync(7, It.IsAny<bool>(), It.IsAny<bool>()))
                      .ThrowsAsync(new InvalidOperationException(marker));
        var rateService = new Mock<IExchangeRateService>();
        rateService.SetupGet(r => r.CurrentRate).Returns(36.50m);
        var dialog = new Mock<IDialogService>();

        using var vm = new InventoryViewModel(productService.Object, rateService.Object, new UserSession(), dialog.Object);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        var item = new ProductItemViewModel(
            new ProductDto { Id = 7, Name = "Cafe", SKU = "CAF-7", IsActive = true },
            rateService.Object);

        await vm.TogglePauseProductCommand.ExecuteAsync(item);

        dialog.Verify(d => d.ShowError("Error de Estado", It.Is<string>(s => s.Contains(marker))), Times.Once);

        var log = ReadResilienceLogUntil(marker);
        Assert.Contains(marker, log);
        Assert.Contains(nameof(InventoryViewModel), log);
    }

    // ---------------------------------------------------------------------
    // ProductDialogViewModel
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ProductDialogViewModel_WhenMetadataLoadFails_LogsErrorAndShowsExactWarning()
    {
        string marker = $"metadata-boom-{Guid.NewGuid():N}";
        var productService = new Mock<IProductService>();
        productService.Setup(p => p.GetParentsAsync()).ThrowsAsync(new InvalidOperationException(marker));
        var rateService = new Mock<IExchangeRateService>();
        rateService.SetupGet(r => r.CurrentRate).Returns(36.50m);
        var dialog = new Mock<IDialogService>();

        using var vm = new ProductDialogViewModel(productService.Object, rateService.Object, null, null, dialog.Object);

        await vm.LoadMetadataAsync();

        dialog.Verify(d => d.ShowWarning(MetadataWarningTitle, MetadataWarningMessage), Times.AtLeastOnce);
        Assert.False(vm.IsLoadingMetadata);

        var log = ReadResilienceLogUntil(marker);
        Assert.Contains(marker, log);
        Assert.Contains(nameof(ProductDialogViewModel), log);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string ReadResilienceLogUntil(params string[] markers)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        string content = string.Empty;

        while (true)
        {
            try
            {
                if (File.Exists(ClientStateLogger.ResilienceLogPath))
                {
                    using var stream = new FileStream(ClientStateLogger.ResilienceLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    content = reader.ReadToEnd();
                }
            }
            catch (IOException)
            {
                content = string.Empty;
            }

            if (markers.All(m => content.Contains(m, StringComparison.Ordinal)) || DateTime.UtcNow >= deadline)
            {
                return content;
            }

            Thread.Sleep(100);
        }
    }

    private static string ReadResilienceLogNow()
    {
        try
        {
            if (!File.Exists(ClientStateLogger.ResilienceLogPath))
            {
                return string.Empty;
            }

            using var stream = new FileStream(ClientStateLogger.ResilienceLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
