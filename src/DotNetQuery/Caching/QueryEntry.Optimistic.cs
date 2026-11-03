namespace DotNetQuery;

/// <summary>
/// Optimistic layers: the stack of pending optimistic writes on an entry, so that when a write in the middle of the
/// stack is rolled back, the writes above it are re-applied (rebased) on its snapshot instead of being lost to a refetch.
/// </summary>
/// <remarks>
/// The stack describes the entry's data only while nothing else wrote to it since the top layer
/// (<c>_version == top.WrittenVersion</c>); a foreign write (fetch, <c>SetQueryData</c>, hydration) makes it stale, and
/// stale stacks fall back to the conflict path (invalidate and refetch). Layers of mutations the server accepted stay
/// (as committed) while pending layers remain below them, because a rebase must re-apply them too.
/// </remarks>
internal sealed partial class QueryEntry<T>
{
    private List<BatchWrite<T>>? _layers;
    private long _layersEpoch;

    /// <summary>Pushes an optimistic write that was just applied. The caller holds <see cref="Gate"/>.</summary>
    internal void AddOptimisticLayerLocked(BatchWrite<T> write)
    {
        _layers ??= [];
        if (_layers.Count > 0 && _layers[^1].WrittenVersion != write.ObservedVersion)
        {
            _layers.Clear(); // something else wrote in between: the old stack no longer describes the data
        }

        _layers.Add(write);
        _layersEpoch++;
    }

    /// <summary>
    /// Rolls back an optimistic write if it is the top layer, or plans a rebase if layers sit above it. The caller holds
    /// <see cref="Gate"/> and drains afterwards.
    /// </summary>
    internal LayerRollback RollbackLayerLocked(BatchWrite<T> write, out RebasePlan? plan)
    {
        plan = null;
        var index = _layers?.IndexOf(write) ?? -1;
        if (index < 0)
        {
            return LayerRollback.NotLayered;
        }

        var layers = _layers!;
        if (_removed || layers[^1].WrittenVersion != _version)
        {
            layers.Clear();
            _layersEpoch++;
            return LayerRollback.Conflict;
        }

        if (index == layers.Count - 1)
        {
            TryRestoreLocked(write.PreviousState, write.PreviousInvalidated, _version);
            layers.RemoveAt(index);
            _layersEpoch++;
            if (layers.Count > 0)
            {
                layers[^1].WrittenVersion = _version; // the restored state is exactly what the new top produced
            }

            TrimCommittedLocked();
            return LayerRollback.Restored;
        }

        plan = new RebasePlan(write, [.. layers.Skip(index + 1)], _layersEpoch, _version);
        return LayerRollback.NeedsRebase;
    }

    /// <summary>
    /// Re-applies the layers above a rolled-back write on that write's snapshot (their updaters run outside the lock),
    /// then commits the result if nothing changed meanwhile. Returns <see langword="false"/> when it raced with another
    /// write or an updater threw; the caller then reconciles by refetching.
    /// </summary>
    internal bool TryRebase(RebasePlan plan)
    {
        var now = Now;
        var rebased = new List<(BatchWrite<T> Layer, QueryState<T> Previous, bool PreviousInvalidated)>(plan.Above.Count);
        var current = plan.Removed.PreviousState;
        var currentInvalidated = plan.Removed.PreviousInvalidated;
        T? value = current.HasData ? current.Data : default;
        try
        {
            foreach (var layer in plan.Above)
            {
                rebased.Add((layer, current, currentInvalidated));
                value = layer.Reapply(value);
                current = new QueryState<T> { Status = QueryStatus.Success, Data = value, DataUpdatedAt = now };
                currentInvalidated = false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        lock (_gate)
        {
            if (_removed || _layers is null || _layersEpoch != plan.Epoch || _version != plan.Version)
            {
                return false;
            }

            ApplyDataLocked(value!, now);
            foreach (var (layer, previous, previousInvalidated) in rebased)
            {
                layer.PreviousState = previous;
                layer.PreviousInvalidated = previousInvalidated;
                layer.WrittenVersion = _version;
            }

            _layers.Remove(plan.Removed);
            _layersEpoch++;
            TrimCommittedLocked();
        }

        Drain();
        return true;
    }

    /// <summary>Marks an optimistic write as accepted by the server (it is never rolled back, but is re-applied by rebases).</summary>
    internal void CommitLayer(BatchWrite<T> write)
    {
        lock (_gate)
        {
            if (_layers is not null && _layers.Contains(write))
            {
                write.Committed = true;
                TrimCommittedLocked();
            }
        }
    }

    // Committed layers at the bottom have nothing below them to roll back: they are plain data now.
    private void TrimCommittedLocked()
    {
        while (_layers is { Count: > 0 } layers && layers[0].Committed)
        {
            layers.RemoveAt(0);
            _layersEpoch++;
        }
    }

    internal enum LayerRollback
    {
        /// <summary>The write is not on the stack (not optimistic, or the stack was reset): use the plain version check.</summary>
        NotLayered,

        /// <summary>The write was the top layer and its snapshot was restored.</summary>
        Restored,

        /// <summary>Layers sit above the write: rebase them with <see cref="TryRebase"/>.</summary>
        NeedsRebase,

        /// <summary>Something else wrote on top: reconcile by refetching.</summary>
        Conflict,
    }

    internal sealed record RebasePlan(BatchWrite<T> Removed, IReadOnlyList<BatchWrite<T>> Above, long Epoch, long Version);
}
