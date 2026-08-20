using System.Collections.Concurrent;

namespace SeaPowerRuntimeBridge;

internal sealed class BoundedConcurrentQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly int _capacity;
    private int _count;

    public BoundedConcurrentQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int Count => Volatile.Read(ref _count);

    public bool TryEnqueue(T item)
    {
        while (true)
        {
            var count = Volatile.Read(ref _count);
            if (count >= _capacity)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _count, count + 1, count) == count)
            {
                _queue.Enqueue(item);
                return true;
            }
        }
    }

    public bool TryDequeue(out T item)
    {
        if (_queue.TryDequeue(out item!))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        item = default!;
        return false;
    }
}