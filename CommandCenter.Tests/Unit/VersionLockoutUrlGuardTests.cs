using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class VersionLockoutUrlGuardTests
{
    [Theory]
    [InlineData("http://192.168.1.50/updates/")]
    [InlineData("http://updates.example.com/pos/")]
    [InlineData("ftp://updates.example.com/pos/")]
    public void StartUpdate_RefusesNonSecureUpdateUrl(string url)
    {
        var vm = new VersionLockoutViewModel("1.0.0", "2.0.0", url);

        vm.StartUpdateCommand.Execute(null);

        Assert.False(vm.IsDownloading);
        Assert.Contains("no es segura", vm.StatusMessage);
    }
}
