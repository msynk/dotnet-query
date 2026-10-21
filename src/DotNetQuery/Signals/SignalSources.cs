namespace DotNetQuery;

/// <summary>Identifies the environmental trigger that prompts a background refetch.</summary>
public enum RefetchTrigger
{
    /// <summary>The application or window regained focus.</summary>
    Focus,

    /// <summary>Network connectivity was restored.</summary>
    Reconnect,

    /// <summary>The application resumed from the background.</summary>
    Resume,
}

/// <summary>
/// A source of environmental signals (focus, connectivity, resume). Platform adapters raise these;
/// the <see cref="IQueryClient"/> listens and refetches eligible active queries.
/// </summary>
public interface ISignalSource
{
    /// <summary>Raised when the underlying environmental event occurs.</summary>
    event Action? Raised;
}

/// <summary>Signals that the application or window regained focus.</summary>
public interface IFocusManager : ISignalSource;

/// <summary>Signals that network connectivity was restored.</summary>
public interface IOnlineManager : ISignalSource;

/// <summary>Signals that the application resumed from the background.</summary>
public interface IResumeManager : ISignalSource;

/// <summary>
/// Base class for signal managers. Platform adapters call <see cref="Signal"/> when their native
/// event fires (for example a Blazor <c>window.onfocus</c> interop callback).
/// </summary>
public abstract class SignalManager : ISignalSource
{
    /// <inheritdoc />
    public event Action? Raised;

    /// <summary>Raises the signal to all listeners.</summary>
    public void Signal() => Raised?.Invoke();
}

/// <summary>Default, adapter-driven <see cref="IFocusManager"/>.</summary>
public sealed class FocusManager : SignalManager, IFocusManager;

/// <summary>Default, adapter-driven <see cref="IOnlineManager"/>.</summary>
public sealed class OnlineManager : SignalManager, IOnlineManager;

/// <summary>Default, adapter-driven <see cref="IResumeManager"/>.</summary>
public sealed class ResumeManager : SignalManager, IResumeManager;
