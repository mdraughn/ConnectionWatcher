using System.Collections;

namespace ConEngine.Tracking
{
    /// <summary>
    /// A fixed-size circular buffer that maintains the most recent items.
    /// When the buffer is full, adding a new item overwrites the oldest item.
    /// </summary>
    /// <typeparam name="T">The type of items stored in the buffer.</typeparam>
    public class CircularBuffer<T> : IEnumerable<T>
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
        /// Gets the last N items from the buffer in chronological order (oldest to newest).
        /// If N is greater than the current count, returns all available items.
        /// This method efficiently extracts items directly from the circular buffer without
        /// creating intermediate arrays, properly handling wrap-around.
        /// </summary>
        /// <param name="count">The number of items to retrieve.</param>
        /// <returns>A span containing up to N most recent items in chronological order.</returns>
        public ReadOnlySpan<T> GetLastItems(int count)
        {
            if (count <= 0)
                throw new ArgumentException("Count must be greater than zero.", nameof(count));

            lock (_lock)
            {
                if (_count == 0)
                    return ReadOnlySpan<T>.Empty;

                // Get the actual number of items to return (min of requested count and available items)
                int itemsToReturn = Math.Min(count, _count);
                
                // Allocate result array
                var result = new T[itemsToReturn];
                
                // Calculate the starting position for the last N items
                // This is the oldest item in our desired window
                int startIndex = (_head + _count - itemsToReturn + _capacity) % _capacity;
                
                // Copy items from the circular buffer to the result array
                // We need to handle the case where items wrap around the end of the buffer
                int firstSegmentLength;
                int secondSegmentLength;
                
                if (startIndex + itemsToReturn <= _capacity)
                {
                    // No wrap-around: all items are in a contiguous segment
                    Array.Copy(_buffer, startIndex, result, 0, itemsToReturn);
                }
                else
                {
                    // Wrap-around: items are split into two segments
                    // First segment: from startIndex to end of buffer
                    firstSegmentLength = _capacity - startIndex;
                    Array.Copy(_buffer, startIndex, result, 0, firstSegmentLength);
                    
                    // Second segment: from start of buffer to complete the count
                    secondSegmentLength = itemsToReturn - firstSegmentLength;
                    Array.Copy(_buffer, 0, result, firstSegmentLength, secondSegmentLength);
                }
                
                return result;
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
                    return Array.Empty<T>();

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
                    return Array.Empty<T>();

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

        /// <summary>
        /// Returns an enumerator that iterates through the buffer in chronological order.
        /// </summary>
        public IEnumerator<T> GetEnumerator()
        {
            var snapshot = ToArray();
            return ((IEnumerable<T>)snapshot).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}