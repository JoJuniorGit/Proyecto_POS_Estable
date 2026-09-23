using System;
using System.Net.Http;
using System.Threading.Tasks;
using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PairingQrViewModelTests
{
    [Fact]
    public void Constructor_WhenHttpClientNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PairingQrViewModel(null!));
    }

    [Fact]
    public void Constructor_WithValidHttpClient_InitializesDefaultValues()
    {
        using var client = new HttpClient();
        using var vm = new PairingQrViewModel(client);

        Assert.Equal("127.0.0.1", vm.IpAddress);
        Assert.True(vm.UseHttps);
        Assert.Equal(5001, vm.ActivePort);
        Assert.Equal("https://127.0.0.1:5001", vm.FullUrl);
        Assert.Equal("https://127.0.0.1:5001/?paired=true", vm.QrPayload);
        Assert.False(vm.IsIpCopied);
        Assert.False(vm.IsUrlCopied);
    }

    [Fact]
    public void UseHttps_WhenToggledFalse_UpdatesPortAndUrlsToHttp()
    {
        using var client = new HttpClient();
        using var vm = new PairingQrViewModel(client);

        vm.UseHttps = false;

        Assert.Equal(5000, vm.ActivePort);
        Assert.Equal("http://127.0.0.1:5000", vm.FullUrl);
        Assert.Equal("http://127.0.0.1:5000/?paired=true", vm.QrPayload);
    }

    [Fact]
    public void SelectedInterface_WhenChanged_UpdatesIpAndUrls()
    {
        using var client = new HttpClient();
        using var vm = new PairingQrViewModel(client);

        var iface = new NetworkInterfaceItem
        {
            Name = "Wi-Fi",
            IpAddress = "192.168.1.100",
            InterfaceType = "Wireless80211",
            IsPrimary = true
        };

        vm.SelectedInterface = iface;

        Assert.Equal("192.168.1.100", vm.IpAddress);
        Assert.Equal("https://192.168.1.100:5001", vm.FullUrl);
        Assert.Equal("https://192.168.1.100:5001/?paired=true", vm.QrPayload);
    }

    [Fact]
    public async Task CopyIpCommand_WhenExecuted_TriggersClipboardEventAndTransientState()
    {
        using var client = new HttpClient();
        using var vm = new PairingQrViewModel(client);
        string? copiedText = null;
        vm.RequestClipboardCopy += text => copiedText = text;

        var task = vm.CopyIpCommand.ExecuteAsync(null);

        Assert.True(vm.IsIpCopied);
        Assert.Equal(vm.IpAddress, copiedText);

        vm.Dispose();
        await task;
    }

    [Fact]
    public async Task CopyUrlCommand_WhenExecuted_TriggersClipboardEventAndTransientState()
    {
        using var client = new HttpClient();
        using var vm = new PairingQrViewModel(client);
        string? copiedText = null;
        vm.RequestClipboardCopy += text => copiedText = text;

        var task = vm.CopyUrlCommand.ExecuteAsync(null);

        Assert.True(vm.IsUrlCopied);
        Assert.Equal(vm.FullUrl, copiedText);

        vm.Dispose();
        await task;
    }

    [Fact]
    public async Task CopyCommands_WhenTriggeredSequentially_MaintainIndependentTokens()
    {
        using var client = new HttpClient();
        using var vm = new PairingQrViewModel(client);

        var ipTask = vm.CopyIpCommand.ExecuteAsync(null);
        Assert.True(vm.IsIpCopied);

        var urlTask = vm.CopyUrlCommand.ExecuteAsync(null);
        Assert.True(vm.IsUrlCopied);
        Assert.True(vm.IsIpCopied);

        vm.Dispose();
        await Task.WhenAll(ipTask, urlTask);
    }
}
