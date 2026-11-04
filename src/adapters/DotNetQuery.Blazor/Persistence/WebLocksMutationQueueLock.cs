using Microsoft.JSInterop;

namespace DotNetQuery.Persistence;

/// <summary>
/// An <see cref="IMutationQueueLock"/> over the browser's Web Locks API (<c>navigator.locks</c>), shared by every tab of
/// the same origin. Give a <see cref="PersistentMutationQueue"/> stored in IndexedDB this lock
/// (<see cref="PersistentMutationQueueOptions.ReplayLock"/>) so that, with the app open in several tabs, each queued
/// write is replayed by one tab instead of by every tab.
/// </summary>
/// <remarks>
/// Use it only where JS interop is available (after the first interactive render), like the IndexedDB persister. In a
/// browser without the Web Locks API (or outside a secure context) the lock is granted at once, so queues behave as
/// they do without one. The lock is released automatically when its tab closes.
/// </remarks>
public sealed class WebLocksMutationQueueLock : IMutationQueueLock, IAsyncDisposable
{
    /// <summary>The default lock name.</summary>
    public const string DefaultName = "dotnetquery-mutation-queue";

    private readonly IJSRuntime _js;
    private readonly string _name;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;
    private long _ids;

    /// <summary>Creates a lock named <paramref name="name"/> (one name per queue store).</summary>
    public WebLocksMutationQueueLock(IJSRuntime jsRuntime, string? name = null)
    {
        _js = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _name = string.IsNullOrWhiteSpace(name) ? DefaultName : name;
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _ids);
        try
        {
            // The token overload: the wait can outlast the default interop timeout while another tab replays.
            // WaitAsync: give up when cancelled even if the JS runtime ignores the token (cancel() then frees the lock).
            await module.InvokeAsync<bool>("acquire", cancellationToken, _name, id).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelQuietlyAsync(module, id).ConfigureAwait(false);
            throw;
        }

        return new Lease(module, id);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var module = Interlocked.Exchange(ref _module, null);
        if (module is null)
        {
            return;
        }

        try
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
        {
            // The circuit is gone; the browser released everything.
        }
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _module) is { } existing)
        {
            return existing;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _module ??= await _js.InvokeAsync<IJSObjectReference>(
                "import", cancellationToken, "./_content/DotNetQuery.Blazor/dotnetquery-locks.js").ConfigureAwait(false);
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    private static async Task CancelQuietlyAsync(IJSObjectReference module, long id)
    {
        try
        {
            await module.InvokeVoidAsync("cancel", id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or JSException)
        {
            // Nothing left to cancel.
        }
    }

    private sealed class Lease(IJSObjectReference module, long id) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            try
            {
                await module.InvokeVoidAsync("release", id).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
            {
                // The tab or circuit is gone: the browser released the lock with it.
            }
        }
    }
}
