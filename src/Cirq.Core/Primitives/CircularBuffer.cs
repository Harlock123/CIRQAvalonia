using System.Collections;

namespace Cirq.Core.Primitives;

/// <summary>
/// Fixed-capacity ring buffer that overwrites its oldest entry once full. Reads and writes are
/// guarded by an internal lock so a simulation thread may append while the UI thread snapshots.
/// </summary>
public sealed class CircularBuffer<T> : IReadOnlyList<T>
{
    private readonly T[] _items;
    private readonly Lock _gate = new();
    private int _head;
    private int _count;

    public CircularBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _items = new T[capacity];
    }

    public int Capacity { get; }

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public bool IsFull
    {
        get { lock (_gate) return _count == Capacity; }
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            _items[_head] = item;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        lock (_gate)
        {
            foreach (var item in items)
            {
                _items[_head] = item;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Indexes from the oldest retained item (0) to the newest (<see cref="Count"/> - 1).</summary>
    public T this[int index]
    {
        get
        {
            lock (_gate)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
                return _items[(_head - _count + index + Capacity) % Capacity];
            }
        }
    }

    /// <summary>Copies the retained items, oldest first, into a new array.</summary>
    public T[] ToArray()
    {
        lock (_gate)
        {
            var result = new T[_count];
            for (var i = 0; i < _count; i++)
                result[i] = _items[(_head - _count + i + Capacity) % Capacity];
            return result;
        }
    }

    /// <summary>Copies at most <paramref name="max"/> of the newest items, oldest first.</summary>
    public T[] ToArrayNewest(int max)
    {
        lock (_gate)
        {
            var take = Math.Min(max, _count);
            var result = new T[take];
            var start = _count - take;
            for (var i = 0; i < take; i++)
                result[i] = _items[(_head - _count + start + i + Capacity) % Capacity];
            return result;
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
