using System.Threading.Channels;
using Sales.Module.Receipts;

namespace Backend.API.Services;

public sealed class ChannelReceiptPrintQueue : IReceiptPrintQueue
{
    private readonly Channel<SaleReceiptContext> _channel =
        Channel.CreateBounded<SaleReceiptContext>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public ChannelReader<SaleReceiptContext> Reader => _channel.Reader;

    public void Enqueue(SaleReceiptContext context)
    {
        _channel.Writer.TryWrite(context);
    }
}