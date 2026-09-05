using System.Threading.Channels;

namespace DialogExtractor.Worker.EventProcessing;

/// <summary>
/// Merges events coming from two independent sources (server.log and
/// client.log) for a single campaign into one timestamp-ordered stream.
///
/// FileSystemWatcher / arrival order is never trusted as event order: each
/// published event is held for <c>orderingDelay</c> after it arrives, then
/// emitted in ascending event-timestamp order together with any other events
/// that became ready in the same flush. This is a bounded buffer, not a
/// guarantee of perfect ordering - the worker never waits indefinitely for a
/// "better" ordering.
/// </summary>
public sealed class EventMerger : IDisposable
{
    private readonly record struct Buffered(WorkerEvent Event, DateTimeOffset ReceivedAt);

    private readonly TimeSpan _orderingDelay;
    private readonly TimeProvider _timeProvider;
    private readonly List<Buffered> _buffer = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly Channel<WorkerEvent> _output = Channel.CreateUnbounded<WorkerEvent>();
    private DateTimeOffset? _scheduledFor;
    private bool _disposed;

    public EventMerger(TimeSpan orderingDelay, TimeProvider? timeProvider = null)
    {
        _orderingDelay = orderingDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = new Timer(_ => Flush());
    }

    public ChannelReader<WorkerEvent> Output => _output.Reader;

    public void Publish(WorkerEvent workerEvent)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_disposed) return;
            _buffer.Add(new Buffered(workerEvent, now));
            ScheduleLocked(now + _orderingDelay);
        }
    }

    /// <summary>Force-emits everything currently buffered, ignoring the ordering delay.</summary>
    public void FlushAll()
    {
        List<WorkerEvent> ready;
        lock (_gate)
        {
            ready = _buffer.OrderBy(b => b.Event.Timestamp).ThenBy(b => b.ReceivedAt).Select(b => b.Event).ToList();
            _buffer.Clear();
            _scheduledFor = null;
        }
        foreach (var e in ready) _output.Writer.TryWrite(e);
    }

    public void Complete() => _output.Writer.TryComplete();

    private void ScheduleLocked(DateTimeOffset dueAt)
    {
        if (_scheduledFor is { } existing && existing <= dueAt) return;
        _scheduledFor = dueAt;
        var delay = dueAt - _timeProvider.GetUtcNow();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        List<WorkerEvent> ready;
        lock (_gate)
        {
            if (_disposed) return;
            var now = _timeProvider.GetUtcNow();
            var readyBuffered = _buffer.Where(b => now - b.ReceivedAt >= _orderingDelay).ToList();
            foreach (var b in readyBuffered) _buffer.Remove(b);

            ready = readyBuffered.OrderBy(b => b.Event.Timestamp).ThenBy(b => b.ReceivedAt).Select(b => b.Event).ToList();

            _scheduledFor = null;
            if (_buffer.Count > 0)
            {
                var nextDue = _buffer.Min(b => b.ReceivedAt) + _orderingDelay;
                ScheduleLocked(nextDue);
            }
        }
        foreach (var e in ready) _output.Writer.TryWrite(e);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        _timer.Dispose();
    }
}
