using System.Collections;

namespace ConEngine.Tracking
{
    /// <summary>
    /// A fixed-size circular buffer that maintains the most recent items.
    /// When the buffer is full, adding a new item overwrites the oldest item.
    /// </summary>
    /// <typeparam name="T">The type of items stored in the buffer.</typeparam>
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly int _capacity;
        private int _head;
        private int _tail;
        private int _count;
        private readonly Lock _lock = new();

        /// <summary>
        /// Initializes a new instance of the CircularBuffer with the specified capacity.
        /// </summary>
        /// <param name="capacity">The maximum number of items the buffer can hold.</param>
        public CircularBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be greater than zero.", nameof(capacity));

            _capacity = capacity;
            _buffer = new T[capacity];
            _head = 0;
            _tail = 0;
            _count = 0;
        }

        /// <summary>
        /// Gets the capacity of the buffer.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Gets the current number of items in the buffer.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        /// <summary>
        /// Gets whether the buffer is full.
        /// </summary>
        public bool IsFull
        {
            get
            {
                lock (_lock)
                {
                    return _count == _capacity;
                }
            }
        }

        /// <summary>
        /// Gets whether the buffer is empty.
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _count == 0;
                }
            }
        }

        /// <summary>
        /// Adds an item to the buffer. If the buffer is full, the oldest item is overwritten.
        /// </summary>
        /// <param name="item">The item to add.</param>
        public void Add(T item)
        {
            lock (_lock)
            {
                _buffer[_tail] = item;
                _tail = (_tail + 1) % _capacity;

                if (_count < _capacity)
                {
                    _count++;
                }
                else
                {
                    // Buffer is full, move head forward to maintain window size
                    _head = (_head + 1) % _capacity;
                }
            }
        }

        /// <summary>
        /// Gets the most recent item added to the buffer.
        /// </summary>
        /// <returns>The most recent item, or default(T) if the buffer is empty.</returns>
        public T? GetLatest()
        {
            lock (_lock)
            {
                if (_count == 0)
                    return default;

                int latestIndex = (_tail - 1 + _capacity) % _capacity;
                return _buffer[latestIndex];
            }
        }

        /// <summary>
        /// Gets the oldest item in the buffer.
        /// </summary>
        /// <returns>The oldest item, or default(T) if the buffer is empty.</returns>
        public T? GetOldest()
        {
            lock (_lock)
            {
                if (_count == 0)
                    return default;

                return _buffer[_head];
            }
        }

        /// <summary>
        /// Gets all items in the buffer in chronological order (newest to oldest).
        /// </summary>
        /// <returns>An enumerator of items in chronological order.</returns>
        public IEnumerable<T> InOrder()
        {
            for(int i = 0; i < _count; i++)
            {
                yield return _buffer[(_head + i) % _capacity];
            }
        }

        /// <summary>
        /// Gets all items in the buffer in reverse chronological order (newest to oldest).
        /// </summary>
        /// <returns>An enumerator of items in reverse chronological order.</returns>
        public IEnumerable<T> InReverseOrder()
        {
            for(int i = 0; i < _count; i++)
            {
                yield return _buffer[(_tail - 1 - i + _capacity) % _capacity];
            }
        }

        /// <summary>
        /// Gets all items in the buffer in chronological order (oldest to newest).
        /// </summary>
        /// <returns>An array of items in chronological order.</returns>
        public T[] ToArray()
        {
            lock (_lock)
            {
                if (_count == 0)
                    return [];

                var result = new T[_count];
                for (int i = 0; i < _count; i++)
                {
                    result[i] = _buffer[(_head + i) % _capacity];
                }
                return result;
            }
        }

        /// <summary>
        /// Gets all items in the buffer in reverse chronological order (newest to oldest).
        /// </summary>
        /// <returns>An array of items in reverse chronological order.</returns>
        public T[] ToArrayReversed()
        {
            lock (_lock)
            {
                if (_count == 0)
                    return [];

                var result = new T[_count];
                for (int i = 0; i < _count; i++)
                {
                    result[i] = _buffer[(_tail - 1 - i + _capacity) % _capacity];
                }
                return result;
            }
        }

        /// <summary>
        /// Clears all items from the buffer.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
                _count = 0;
                Array.Clear(_buffer, 0, _capacity);
            }
        }
    }
}