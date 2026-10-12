using System.Collections;

namespace Pulse.Core;

/// <summary>Fixed-capacity ring buffer that keeps only the most recent samples.</summary>
public sealed class BoundedHistory<T> : IEnumerable<T>
{
    public const int DefaultCapacity = 60;

    private readonly T[] _items;
    private int _start;

    public BoundedHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    /// <summary>Gets the sample at <paramref name="index"/>, where 0 is the oldest.</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_start + index) % _items.Length];
        }
    }

    public void Add(T value)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = value;
            Count++;
        }
        else
        {
            _items[_start] = value;
            _start = (_start + 1) % _items.Length;
        }
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
