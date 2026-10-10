using System.Net.WebSockets;
using System.Threading.Channels;

namespace YHDE.Server.Gateway;

// What one connection still has to receive, sent by its own task
// (reliability.md "Slow clients"). Nobody waits for another person's network:
// the commit loop, presence and chat only put messages in the queue. A
// connection whose queue fills up, or that takes longer than SendTimeout to
// take one message, is cut off; the editor reconnects and catches up from the
// log, which loses nothing.
public sealed class Outbox(TimeSpan? sendTimeout = null)
{
    // Bytes queued for one connection before it counts as too slow. A
    // catch-up page is at most a few MiB, so this is many pages.
    public const long MaxQueuedBytes = 64L * 1024 * 1024;
    // Cursor updates beyond this many queued messages are dropped: a newer
    // one follows, and only the latest position matters.
    public const int PresenceDropThreshold = 256;
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _sendTimeout = sendTimeout ?? SendTimeout;
    private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private long _bytes;
    private int _count;

    public long QueuedBytes => Interlocked.Read(ref _bytes);
    public int QueuedMessages => Volatile.Read(ref _count);

    // Queues a message without waiting. False when the connection is too slow
    // (the caller cuts it off). A droppable message (presence) is skipped, not
    // refused, when the queue is long.
    public bool TryPost(byte[] wire, bool droppable = false)
    {
        if (droppable && QueuedMessages > PresenceDropThreshold) return true;
        if (QueuedBytes + wire.Length > MaxQueuedBytes) return false;
        Interlocked.Add(ref _bytes, wire.Length);
        Interlocked.Increment(ref _count);
        if (_queue.Writer.TryWrite(wire)) return true;
        Interlocked.Add(ref _bytes, -wire.Length);
        Interlocked.Decrement(ref _count);
        return true; // the connection is closing: nothing more to send
    }

    // Queues a reply to this connection's own request, waiting while its queue
    // is full: a client that does not read slows only itself.
    public async Task PostAsync(byte[] wire, CancellationToken ct)
    {
        while (QueuedBytes > 0 && QueuedBytes + wire.Length > MaxQueuedBytes)
            await Task.Delay(25, ct);
        TryPost(wire);
    }

    public void Complete() => _queue.Writer.TryComplete();

    // Sends everything queued until Complete() or the socket closes. Writes go
    // through `writeLock`, which others also take to close the socket.
    public async Task RunAsync(WebSocket socket, SemaphoreSlim writeLock, Action<string> tooSlow, CancellationToken ct)
    {
        try
        {
            await foreach (var wire in _queue.Reader.ReadAllAsync(ct))
            {
                Interlocked.Add(ref _bytes, -wire.Length);
                Interlocked.Decrement(ref _count);
                if (socket.State != WebSocketState.Open) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_sendTimeout);
                await writeLock.WaitAsync(ct);
                try
                {
                    await socket.SendAsync(wire, WebSocketMessageType.Binary, true, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    tooSlow("a message took longer than " + _sendTimeout.TotalSeconds + " s to send");
                    return;
                }
                catch (WebSocketException)
                {
                    return; // gone; the read loop cleans up
                }
                finally
                {
                    writeLock.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
