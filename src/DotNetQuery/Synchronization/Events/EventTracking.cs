namespace DotNetQuery.Synchronization;

/// <summary>
/// A bounded set of ids that expire after a window: duplicate detection and local-origin correlation. Memory is bounded
/// by <c>capacity</c> (the oldest ids are evicted first). Not thread-safe; callers serialize access.
/// </summary>
internal sealed class ExpiringIdSet(int capacity, TimeSpan window, TimeProvider time)
{
    private readonly Dictionary<string, long> _expiries = new(StringComparer.Ordinal);
    private readonly Queue<(string Id, long Expiry)> _order = new();
    private readonly long _windowTicks = window.Ticks;

    public int Count => _expiries.Count;

    public bool Contains(string id)
    {
        if (capacity == 0)
        {
            return false;
        }

        var now = time.GetUtcNow().UtcTicks;
        Prune(now);
        return _expiries.TryGetValue(id, out var expiry) && expiry > now;
    }

    /// <summary>Remembers <paramref name="id"/> for the window. An id already remembered keeps its original expiry.</summary>
    public void Add(string id)
    {
        if (capacity == 0)
        {
            return;
        }

        var now = time.GetUtcNow().UtcTicks;
        Prune(now);
        if (_expiries.TryGetValue(id, out var existing) && existing > now)
        {
            return;
        }

        var expiry = now + _windowTicks;
        _expiries[id] = expiry;
        _order.Enqueue((id, expiry));
        while (_expiries.Count > capacity && _order.TryDequeue(out var oldest))
        {
            Forget(oldest);
        }
    }

    public void Clear()
    {
        _expiries.Clear();
        _order.Clear();
    }

    private void Prune(long now)
    {
        while (_order.TryPeek(out var oldest) && oldest.Expiry <= now)
        {
            _order.Dequeue();
            Forget(oldest);
        }
    }

    private void Forget((string Id, long Expiry) entry)
    {
        // A later Add of the same id (after it expired) owns the dictionary slot.
        if (_expiries.TryGetValue(entry.Id, out var current) && current == entry.Expiry)
        {
            _expiries.Remove(entry.Id);
        }
    }
}

/// <summary>
/// The last applied version of each entity, bounded by <c>capacity</c> (the entities tracked longest are evicted first;
/// an evicted entity's next event is accepted). Not thread-safe; used by the processing loop only.
/// </summary>
internal sealed class VersionTracker(int capacity)
{
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public int Count => _versions.Count;

    public bool IsStale(string entityId, long version) =>
        capacity > 0 && _versions.TryGetValue(entityId, out var applied) && version <= applied;

    public void Record(string entityId, long version)
    {
        if (capacity == 0)
        {
            return;
        }

        if (_versions.TryGetValue(entityId, out var applied))
        {
            if (version > applied)
            {
                _versions[entityId] = version;
            }

            return;
        }

        _versions[entityId] = version;
        _order.Enqueue(entityId);
        while (_versions.Count > capacity && _order.TryDequeue(out var oldest))
        {
            _versions.Remove(oldest);
        }
    }

    public void Clear()
    {
        _versions.Clear();
        _order.Clear();
    }
}

/// <summary>The verdict on an event's stream sequence.</summary>
internal enum SequenceCheck
{
    Accept,
    Stale,
    Gap,
}

/// <summary>Per-stream sequence checkpoints and gap detection. Thread-safe.</summary>
internal sealed class SequenceTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);

    public SequenceCheck Check(string stream, long sequence, bool contiguous)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var state))
            {
                return SequenceCheck.Accept; // the first sequenced event of a stream is its baseline
            }

            if (sequence <= state.Checkpoint)
            {
                return SequenceCheck.Stale;
            }

            return contiguous && !state.Baseline && sequence > state.Checkpoint + 1 ? SequenceCheck.Gap : SequenceCheck.Accept;
        }
    }

    public void Advance(string stream, long sequence)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var state))
            {
                _streams[stream] = new StreamState { Checkpoint = sequence, HighestSeen = sequence };
                return;
            }

            state.Checkpoint = Math.Max(state.Checkpoint, sequence);
            state.HighestSeen = Math.Max(state.HighestSeen, sequence);
            state.Baseline = false;
        }
    }

    /// <summary>Records a sequence seen past a gap (not applied).</summary>
    public void RecordGap(string stream, long sequence)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue(stream, out var state))
            {
                state.HighestSeen = Math.Max(state.HighestSeen, sequence);
            }
        }
    }

    /// <summary>Whether a contiguous stream still misses events past its checkpoint.</summary>
    public bool HasGap
    {
        get
        {
            lock (_gate)
            {
                foreach (var state in _streams.Values)
                {
                    if (state.HighestSeen > state.Checkpoint)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// After the cache was reconciled with the server some other way (invalidation, snapshot): everything seen so far is
    /// accounted for, and the next event of each stream starts a new baseline (it may jump), but must still be newer.
    /// </summary>
    public void ResetBaseline()
    {
        lock (_gate)
        {
            foreach (var state in _streams.Values)
            {
                state.Checkpoint = Math.Max(state.Checkpoint, state.HighestSeen);
                state.HighestSeen = state.Checkpoint;
                state.Baseline = true;
            }
        }
    }

    public long? Get(string stream)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(stream, out var state) ? state.Checkpoint : null;
        }
    }

    public void Set(string stream, long sequence)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var state))
            {
                _streams[stream] = new StreamState { Checkpoint = sequence, HighestSeen = sequence };
                return;
            }

            state.Checkpoint = sequence;
            state.HighestSeen = Math.Max(state.HighestSeen, sequence);
            state.Baseline = false;
        }
    }

    public Dictionary<string, long> Snapshot()
    {
        lock (_gate)
        {
            var copy = new Dictionary<string, long>(_streams.Count, StringComparer.Ordinal);
            foreach (var (stream, state) in _streams)
            {
                copy[stream] = state.Checkpoint;
            }

            return copy;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _streams.Clear();
        }
    }

    private sealed class StreamState
    {
        public long Checkpoint { get; set; }

        public long HighestSeen { get; set; }

        public bool Baseline { get; set; }
    }
}
