using System.Collections.Concurrent;

namespace ConEngine.Events;

public enum OutboxMode
{
    /// <summary>Drain blocks until no other thread is publishing, so on return the caller's items are
    /// delivered. TargetMonitor: callers (and tests) rely on a transition being out when the call returns.</summary>
    Wait,
    /// <summary>Drain returns at once when another thread is publishing; that thread re-checks the queue
    /// after it lets go and delivers the item. Coordinators: they never wait inside another delivery.</summary>
    Skip,
}

/// <summary>
/// Queue under the owner's lock (queue order is state order), publish after releasing it, one
/// publisher at a time, never under the owner's lock. If publish throws, the exception propagates out of
/// Drain and items still queued are delivered by the next Drain.
///
/// In <see cref="OutboxMode.Skip"/> the publish lock is TryEnter, not Enter: a thread that finds another
/// one draining leaves its item to that thread, which re-checks the queue after releasing the lock —
/// nothing is stranded, and no thread waits on the owner while it may itself be inside another
/// publisher's delivery. In <see cref="OutboxMode.Wait"/> the caller blocks until it has had its turn.
/// </summary>
public sealed class OrderedOutbox<T>(Action<T> publish, OutboxMode mode)
{
    private readonly Lock _publishLock = new();
    private readonly ConcurrentQueue<T> _queue = new();

    /// <summary>Call while holding the owner's lock, so queue order is state order.</summary>
    public void Enqueue(T item) => _queue.Enqueue(item);

    /// <summary>Delivers queued items in order. Call after releasing the owner's lock.</summary>
    public void Drain()
    {
        if (mode == OutboxMode.Wait)
        {
            if (_queue.IsEmpty)
                return;
            lock (_publishLock)
            {
                while (_queue.TryDequeue(out var item))
                    publish(item);
            }
            return;
        }

        while (!_queue.IsEmpty)
        {
            if (!_publishLock.TryEnter())
                return;
            try
            {
                while (_queue.TryDequeue(out var item))
                    publish(item);
            }
            finally
            {
                _publishLock.Exit();
            }
        }
    }
}
