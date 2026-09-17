namespace ConEngine.Tracking;

/// <summary>
/// A fixed-size circular buffer that keeps the most recent items; adding to
/// a full buffer overwrites the oldest. Not thread-safe by itself: the owner
/// (HostTracker) serializes every access under its own lock.
/// </summary>
/// <typeparam name="T">The type of items stored in the buffer.</typeparam>
public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _head;
    private int _tail;
    private int _count;

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentException("Capacity must be greater than zero.", nameof(capacity));

        _capacity = capacity;
        _buffer = new T[capacity];
    }

    public int Capacity => _capacity;

    public int Count => _count;

    /// <summary>Adds an item; when full, the oldest item is overwritten.</summary>
    public void Add(T item)
    {
        _buffer[_tail] = item;
        _tail = (_tail + 1) % _capacity;

        if (_count < _capacity)
            _count++;
        else
            _head = (_head + 1) % _capacity; // full: drop the oldest
    }

    /// <summary>The most recent item, or default when empty.</summary>
    public T? GetLatest()
    {
        if (_count == 0)
            return default;
        return _buffer[(_tail - 1 + _capacity) % _capacity];
    }

    /// <summary>Items newest to oldest (lazy; enumerate under the owner's lock).</summary>
    public IEnumerable<T> InReverseOrder()
    {
        for (int i = 0; i < _count; i++)
            yield return _buffer[(_tail - 1 - i + _capacity) % _capacity];
    }

    /// <summary>Items oldest to newest, as a snapshot.</summary>
    public T[] ToArray()
    {
        if (_count == 0)
            return [];

        var result = new T[_count];
        for (int i = 0; i < _count; i++)
            result[i] = _buffer[(_head + i) % _capacity];
        return result;
    }

    public void Clear()
    {
        _head = 0;
        _tail = 0;
        _count = 0;
        Array.Clear(_buffer, 0, _capacity);
    }
}
