namespace UnifiedMessenger.App.Services.Mail;

internal sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _recency = [];
    private readonly object _gate = new();

    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                value = default!;
                return false;
            }

            _recency.Remove(node);
            _recency.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                existing.Value = new Entry(key, value);
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                return;
            }

            LinkedListNode<Entry> node = _recency.AddFirst(new Entry(key, value));
            _entries.Add(key, node);
            if (_entries.Count <= _capacity)
            {
                return;
            }

            LinkedListNode<Entry> oldest = _recency.Last!;
            _recency.RemoveLast();
            _entries.Remove(oldest.Value.Key);
        }
    }

    public int RemoveWhere(Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            TKey[] keys = _entries.Keys.Where(predicate).ToArray();
            foreach (TKey key in keys)
            {
                LinkedListNode<Entry> node = _entries[key];
                _entries.Remove(key);
                _recency.Remove(node);
            }

            return keys.Length;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
        }
    }

    private sealed record Entry(TKey Key, TValue Value);
}
