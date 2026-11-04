using System.Runtime.Versioning;

namespace DotNetQuery.Persistence;

/// <summary>
/// An exclusive lock shared by every <see cref="PersistentMutationQueue"/> that uses the same store - several browser
/// tabs over one IndexedDB store, several processes over one directory or database. A queue configured with one
/// (<see cref="PersistentMutationQueueOptions.ReplayLock"/>) replays only while it holds the lock and first reconciles
/// its pending list with the store, so a queued write is sent by exactly one instance instead of by each of them.
/// </summary>
/// <remarks>
/// Implementations: <see cref="FileMutationQueueLock"/> (processes on one machine), <c>WebLocksMutationQueueLock</c> in
/// the Blazor package (browser tabs, over the Web Locks API), or your own over a distributed lock.
/// </remarks>
public interface IMutationQueueLock
{
    /// <summary>
    /// Waits until this caller holds the lock. Disposing the returned handle releases it. Cancelling the wait gives up
    /// without acquiring.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="IMutationQueueLock"/> over an exclusively opened lock file, for queues in several processes on one
/// machine (for example two windows of a desktop app sharing a <see cref="FileSystemQueryPersister"/> directory). The
/// operating system releases the lock if a process dies while holding it.
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed class FileMutationQueueLock : IMutationQueueLock
{
    private readonly string _path;
    private readonly TimeSpan _pollInterval;

    /// <summary>Creates a lock over <paramref name="path"/> (created on first use; never deleted).</summary>
    /// <param name="path">The lock file. Every participating process must use the same path.</param>
    /// <param name="pollInterval">How often a waiting caller retries. Defaults to 50 ms.</param>
    public FileMutationQueueLock(string path, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _pollInterval = pollInterval is { } interval && interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(50);
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (Path.GetDirectoryName(_path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new Handle(new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
            }
            catch (IOException)
            {
                // Held by another process (or another queue in this one): wait and retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Some platforms report a sharing violation this way.
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Handle(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
