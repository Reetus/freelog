using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace FreeLog.Relay.Sinks;

public readonly record struct PendingRecord(string Target, JsonObject Record);

/// <summary>Bounded in-memory queue between the HTTP endpoint and the sink writer.</summary>
public sealed class LogQueue
{
    private readonly Channel<PendingRecord> _channel;
    private readonly RelayMetrics _metrics;

    public LogQueue(IOptions<RelayOptions> options, RelayMetrics metrics)
    {
        _metrics = metrics;
        _channel = Channel.CreateBounded<PendingRecord>(new BoundedChannelOptions(options.Value.Queue.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full → we count the drop
            SingleReader = true,
        });
    }

    public ChannelReader<PendingRecord> Reader => _channel.Reader;

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(PendingRecord record)
    {
        if (_channel.Writer.TryWrite(record))
            return true;
        _metrics.Inc(ref _metrics.LogsDroppedQueueFull);
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
