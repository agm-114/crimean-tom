namespace CrimeanTom.Protocol;

public enum PriorityEnqueueResult
{
    Enqueued,
    Full,
    InvalidMessageId,
    InvalidPriority,
    DuplicateMessageId,
}

public sealed class BoundedPriorityEnvelopeQueue
{
    private static readonly int[] DequeueSchedule =
    {
        CommandPriorities.Control,
        CommandPriorities.Control,
        CommandPriorities.Control,
        CommandPriorities.Control,
        CommandPriorities.Normal,
        CommandPriorities.Normal,
        CommandPriorities.Bulk,
    };

    private readonly object _sync = new();
    private readonly Dictionary<int, LinkedList<WireEnvelope>> _queues = new()
    {
        [CommandPriorities.Control] = new LinkedList<WireEnvelope>(),
        [CommandPriorities.Normal] = new LinkedList<WireEnvelope>(),
        [CommandPriorities.Bulk] = new LinkedList<WireEnvelope>(),
    };
    private readonly Dictionary<string, QueueEntry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private int _scheduleIndex;

    public BoundedPriorityEnvelopeQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public PriorityEnqueueResult TryEnqueue(WireEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.MessageId))
        {
            return PriorityEnqueueResult.InvalidMessageId;
        }

        if (!CommandPriorities.IsDefined(envelope.Priority))
        {
            return PriorityEnqueueResult.InvalidPriority;
        }

        lock (_sync)
        {
            if (_entries.ContainsKey(envelope.MessageId))
            {
                return PriorityEnqueueResult.DuplicateMessageId;
            }

            if (_entries.Count >= _capacity)
            {
                return PriorityEnqueueResult.Full;
            }

            var queue = _queues[envelope.Priority];
            var node = queue.AddLast(envelope);
            _entries.Add(envelope.MessageId, new QueueEntry(envelope.Priority, node));
            return PriorityEnqueueResult.Enqueued;
        }
    }

    public bool TryCancel(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_entries.Remove(messageId, out var entry))
            {
                return false;
            }

            _queues[entry.Priority].Remove(entry.Node);
            return true;
        }
    }

    public int RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }

        lock (_sync)
        {
            var removed = 0;
            foreach (var queue in _queues.Values)
            {
                var node = queue.First;
                while (node is not null)
                {
                    var next = node.Next;
                    if (string.Equals(
                        node.Value.SessionId,
                        sessionId,
                        StringComparison.Ordinal))
                    {
                        queue.Remove(node);
                        _entries.Remove(node.Value.MessageId);
                        removed++;
                    }

                    node = next;
                }
            }

            return removed;
        }
    }

    public bool TryDequeue(out WireEnvelope envelope)
    {
        lock (_sync)
        {
            for (var offset = 0; offset < DequeueSchedule.Length; offset++)
            {
                var index = (_scheduleIndex + offset) % DequeueSchedule.Length;
                var priority = DequeueSchedule[index];
                var queue = _queues[priority];
                if (queue.First is null)
                {
                    continue;
                }

                var node = queue.First;
                queue.RemoveFirst();
                _entries.Remove(node.Value.MessageId);
                _scheduleIndex = (index + 1) % DequeueSchedule.Length;
                envelope = node.Value;
                return true;
            }
        }

        envelope = null!;
        return false;
    }

    private sealed class QueueEntry
    {
        public QueueEntry(int priority, LinkedListNode<WireEnvelope> node)
        {
            Priority = priority;
            Node = node;
        }

        public int Priority { get; }

        public LinkedListNode<WireEnvelope> Node { get; }
    }
}
