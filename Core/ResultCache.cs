namespace GitReview.Core;

/// <summary>
/// A small most-recently-used cache. Reviewing means moving back and forth over the same
/// handful of commits and files, and every miss costs a git process (about 80-90 ms on a
/// large repository), so keeping the last few results makes revisiting free.
/// </summary>
internal sealed class ResultCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _items = [];
    private readonly LinkedList<TKey> _order = new();   // most recent first
    private readonly Lock _gate = new();                // prefetch runs off the UI thread

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out value!)) return false;

            Touch(key);
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            _items[key] = value;
            Touch(key);

            while (_order.Count > capacity)
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _items.Remove(oldest);
            }
        }
    }

    private void Touch(TKey key)
    {
        _order.Remove(key);
        _order.AddFirst(key);
    }
}
