using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace DotNetQuery.Blazor;

/// <summary>
/// Forgets everything cached for a user when the signed-in identity changes (sign-out, sign-in as someone else,
/// or - with a custom identity selector - a tenant switch). Without it, a Blazor WebAssembly tab keeps the previous
/// user's cached data in its scoped client after the next user signs in.
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>AddDotNetQueryBlazor().ResetOnUserChange()</c>. It is activated by the first
/// <c>&lt;Query&gt;</c> or <see cref="DotNetQueryComponentBase"/> component (or by resolving it yourself) and listens
/// to <see cref="AuthenticationStateProvider.AuthenticationStateChanged"/>. The identity seen first is recorded
/// without a reset; every later change of identity clears the cache: queries nobody renders are removed, rendered
/// queries are reset and refetched under the new identity (so no stale data is shown even for a moment), and
/// settled mutation runs are forgotten.
/// </para>
/// <para>
/// Data outside the client - persisted caches (localStorage, IndexedDB) and offline mutation queues - belongs to
/// the app: clear or re-key it in a <see cref="UserChanged"/> handler.
/// </para>
/// </remarks>
public sealed class QueryAuthenticationGuard : IDisposable
{
    private readonly IQueryClient _client;
    private readonly AuthenticationStateProvider? _authentication;
    private readonly Func<ClaimsPrincipal, string?> _identity;
    private readonly object _gate = new();
    private bool _known;
    private string? _current;
    private int _disposed;

    /// <summary>Creates a guard over <paramref name="client"/>; inert when <paramref name="authentication"/> is <see langword="null"/>.</summary>
    /// <param name="client">The client whose cache belongs to the signed-in user.</param>
    /// <param name="authentication">The app's authentication state provider.</param>
    /// <param name="identity">
    /// Maps a principal to the identity whose change should reset the cache; <see langword="null"/> means anonymous.
    /// Defaults to <see cref="DefaultIdentity"/>. Include a tenant claim to also reset on tenant switches.
    /// </param>
    public QueryAuthenticationGuard(IQueryClient client, AuthenticationStateProvider? authentication, Func<ClaimsPrincipal, string?>? identity = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _authentication = authentication;
        _identity = identity ?? DefaultIdentity;
        if (_authentication is not null)
        {
            _authentication.AuthenticationStateChanged += OnAuthenticationStateChanged;
            _ = ObserveInitialAsync(_authentication);
        }
    }

    /// <summary>
    /// Raised after the cache was cleared because the identity changed, with the previous and new identity
    /// (<see langword="null"/> = anonymous). Clear persisted caches and offline queues of the previous user here.
    /// Handler exceptions are swallowed.
    /// </summary>
    public event Action<string?, string?>? UserChanged;

    /// <summary>Gets the identity the guard currently attributes the cache to, or <see langword="null"/> when anonymous or unknown.</summary>
    public string? CurrentIdentity
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>
    /// The default identity: for an authenticated principal its <see cref="ClaimTypes.NameIdentifier"/>, else its
    /// <c>sub</c> claim, else its name; <see langword="null"/> for anonymous principals.
    /// </summary>
    public static string? DefaultIdentity(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.Identity.Name
            ?? string.Empty;
    }

    /// <summary>Clears the cache now, regardless of identity (for example from an explicit sign-out handler).</summary>
    public void Reset()
    {
        _client.Clear();
        _client.Cache.Mutations.Clear();
    }

    private async Task ObserveInitialAsync(AuthenticationStateProvider authentication)
    {
        try
        {
            var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
            Apply(_identity(state.User), initial: true);
        }
        catch (Exception)
        {
            // No authentication state yet (for example outside a component scope while prerendering); the first
            // change notification establishes the identity instead.
        }
    }

    private async void OnAuthenticationStateChanged(Task<AuthenticationState> task)
    {
        try
        {
            var state = await task.ConfigureAwait(false);
            Apply(_identity(state.User), initial: false);
        }
        catch (Exception)
        {
            // A failed authentication-state task carries no identity to compare.
        }
    }

    private void Apply(string? identity, bool initial)
    {
        string? previous;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!_known)
            {
                _known = true;
                _current = identity;
                return;
            }

            // The initial read may complete after a change notification; it must not overwrite it.
            if (initial || string.Equals(_current, identity, StringComparison.Ordinal))
            {
                return;
            }

            previous = _current;
            _current = identity;
        }

        Reset();
        try
        {
            UserChanged?.Invoke(previous, identity);
        }
        catch (Exception)
        {
            // Subscriber errors must not break the guard.
        }
    }

    /// <summary>Stops listening for authentication changes.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _authentication is not null)
        {
            _authentication.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        }
    }
}
