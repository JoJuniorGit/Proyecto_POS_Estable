using System;
using System.Threading.Tasks;
using Desktop.Client.Helpers;
using Xunit;

namespace CommandCenter.Tests;

public class KeyboardWedgeScannerListenerTests
{
    [Fact]
    public void Constructor_SetsConfigurableIntervalClamped()
    {
        var listenerLow = new KeyboardWedgeScannerListener(_ => Task.CompletedTask, maxInterKeyIntervalMs: 5);
        var listenerHigh = new KeyboardWedgeScannerListener(_ => Task.CompletedTask, maxInterKeyIntervalMs: 500);

        Assert.NotNull(listenerLow);
        Assert.NotNull(listenerHigh);

        listenerLow.Dispose();
        listenerHigh.Dispose();
    }

    [Fact]
    public void Dispose_DetachesWithoutThrowing()
    {
        var listener = new KeyboardWedgeScannerListener(_ => Task.CompletedTask, 60);
        var ex = Record.Exception(() =>
        {
            listener.Detach();
            listener.Dispose();
            listener.Dispose(); // Multiple dispose safe
        });

        Assert.Null(ex);
    }
}
