using System.Threading.Channels;
using Sales.Module.Receipts;

namespace Backend.API.Services;

public sealed class ChannelReceiptPrintQueue : IReceiptPrintQueue
{
    private readonly Channel<SaleReceiptContext> _channel =
        Channel.CreateUnbounded<SaleReceiptContext>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelReader<SaleReceiptContext> Reader => _channel.Reader;

    public void Enqueue(SaleReceiptContext context)
    {
        _channel.Writer.TryWrite(context);
    }
}