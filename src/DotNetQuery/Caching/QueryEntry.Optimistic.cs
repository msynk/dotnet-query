namespace DotNetQuery;

/// <summary>
/// Optimistic layers: the stack of pending optimistic writes on an entry, so that when a write in the middle of the
/// stack is rolled back, the writes above it are re-applied (rebased) on its snapshot instead of being lost to a refetch.
/// </summary>
/// <remarks>
/// <para>
/// The stack describes the entry's data only while nothing else wrote to it since the top layer
/// (<c>_version == top.WrittenVersion</c>); a foreign write (fetch, <c>SetQueryData</c>, hydration) makes it stale, and
/// stale stacks fall back to the conflict path (invalidate and refetch). Layers of mutations the server accepted stay
/// (as committed) while pending layers remain below them, because a rebase must re-apply them too.
/// </para>
/// <para>
/// While a pending layer exists, refreshes of the entry (invalidation, refetches, intervals, focus/reconnect triggers,
/// reads of stale data) are deferred: their result would be server data that does not include the pending writes yet,
/// and would replace them on screen until those mutations settle. The deferral is recorded (<c>_refetchDeferred</c>)
/// and, once the last pending writer has settled, one refetch runs - unless one already started meanwhile.
/// </para>
/// </remarks>
internal sealed partial class QueryEntry<T>
{
    private List<BatchWrite<T>>? _layers;
    private long _layersEpoch;
    private bool _refetchDeferred;

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
    /// Whether optimistic writes of mutations the server has not answered yet are on screen: the stack still describes
    /// the data and holds an uncommitted layer. The caller holds <see cref="Gate"/>.
    /// </summary>
    private bool HasPendingLayersLocked()
    {
        if (_removed || _layers is not { Count: > 0 } layers || layers[^1].WrittenVersion != _version)
        {
            return false;
        }

        foreach (var layer in layers)
        {
            if (!layer.Committed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Called when a mutation that wrote to this entry optimistically has fully settled (committed or rolled back, its
    /// callbacks and invalidations done). Runs the refetch deferred while optimistic writes were pending, once none is
    /// left - unless a refetch already started since (typically the settling mutation's own invalidation).
    /// </summary>
    internal void OnOptimisticWriteSettled()
    {
        lock (_gate)
        {
            if (!_refetchDeferred || HasPendingLayersLocked())
            {
                return;
            }

            _refetchDeferred = false;
            if (_removed || _observers.Count == 0 || _lastFetcher is null || !_lastOptions.Enabled)
            {
                return; // stays invalidated: refetched on its next use
            }
        }

        _ = RefetchAsync(FetchMode.CancelAndRefetch, includeDisabled: false, CancellationToken.None);
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
        var applied = false;
        for (var attempt = 0; attempt < 8 && !applied; attempt++)
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
                DropLayersAfterFailedRebase(plan);
                return false;
            }

            lock (_gate)
            {
                if (!_removed && _layers is { } layers && _version == plan.Version)
                {
                    if (_layersEpoch == plan.Epoch)
                    {
                        ApplyDataLocked(value!, now);
                        foreach (var (layer, previous, previousInvalidated) in rebased)
                        {
                            layer.PreviousState = previous;
                            layer.PreviousInvalidated = previousInvalidated;
                            layer.WrittenVersion = _version;
                        }

                        layers.Remove(plan.Removed);
                        _layersEpoch++;
                        TrimCommittedLocked();
                        applied = true;
                        continue;
                    }

                    // Only the stack's bookkeeping changed (a layer below committed and was trimmed): the data is the same,
                    // so plan again against the current stack.
                    var index = layers.IndexOf(plan.Removed);
                    if (index >= 0 && index < layers.Count - 1)
                    {
                        plan = new RebasePlan(plan.Removed, [.. layers.Skip(index + 1)], _layersEpoch, _version);
                        continue;
                    }
                }

                DropLayersAfterFailedRebaseLocked(plan);
                return false;
            }
        }

        if (!applied)
        {
            DropLayersAfterFailedRebase(plan); // the stack kept changing under us: reconcile by refetching
            return false;
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

    // A rebase that failed leaves data the stack no longer describes (the rolled-back write may still be applied): drop
    // the stack, so the reconciling refetch is not deferred behind layers that no longer match the data.
    private void DropLayersAfterFailedRebase(RebasePlan plan)
    {
        lock (_gate)
        {
            DropLayersAfterFailedRebaseLocked(plan);
        }
    }

    private void DropLayersAfterFailedRebaseLocked(RebasePlan plan)
    {
        if (_layers is not null && _layers.Contains(plan.Removed))
        {
            _layers.Clear();
            _layersEpoch++;
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
