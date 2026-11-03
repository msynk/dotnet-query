using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using DotNetQuery;
using DotNetQuery.Blazor;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class AuthenticationGuardTests : TestContext
{
    private readonly TestAuthorizationContext _auth;
    private string _user = "alice";
    private int _fetches;

    public AuthenticationGuardTests()
    {
        Services.AddDotNetQueryBlazor(o =>
        {
            o.DefaultRetryCount = 0;
            o.DefaultStaleTime = TimeSpan.FromMinutes(5);
        }).ResetOnUserChange();
        _auth = this.AddTestAuthorization();
        _auth.SetAuthorized("alice");
    }

    private IRenderedComponent<Query<string>> RenderProfile() =>
        RenderComponent<Query<string>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("me"))
            .Add(p => p.Fetcher, (CancellationToken _) =>
            {
                Interlocked.Increment(ref _fetches);
                return Task.FromResult($"profile of {_user}");
            })
            .Add(p => p.ChildContent, r => $"<span>{r.Data}</span>"));

    [Fact]
    public void Switching_users_drops_hidden_data_and_reloads_rendered_queries()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var cut = RenderProfile();
        cut.WaitForAssertion(() => Assert.Contains("profile of alice", cut.Markup));
        client.SetQueryData(QueryKey.Create("alice", "inbox"), "private mail");
        var guard = Services.GetRequiredService<QueryAuthenticationGuard>();
        (string? From, string? To)? changed = null;
        guard.UserChanged += (from, to) => changed = (from, to);

        _user = "bob";
        _auth.SetAuthorized("bob");

        cut.WaitForAssertion(() => Assert.Contains("profile of bob", cut.Markup));
        Assert.False(client.GetQueryData<string>(QueryKey.Create("alice", "inbox")).HasData);
        Assert.Equal(("alice", "bob"), changed);
        Assert.Equal("bob", guard.CurrentIdentity);
    }

    [Fact]
    public void Signing_out_clears_the_cache()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var cut = RenderProfile();
        cut.WaitForAssertion(() => Assert.Contains("profile of alice", cut.Markup));

        _user = "anonymous";
        _auth.SetNotAuthorized();

        cut.WaitForAssertion(() => Assert.Contains("profile of anonymous", cut.Markup));
        Assert.Null(Services.GetRequiredService<QueryAuthenticationGuard>().CurrentIdentity);
        Assert.DoesNotContain("alice", cut.Markup);
        Assert.Single(client.Cache.Keys);
    }

    [Fact]
    public void Re_authenticating_the_same_user_keeps_the_cache()
    {
        var cut = RenderProfile();
        cut.WaitForAssertion(() => Assert.Contains("profile of alice", cut.Markup));
        var fetches = Volatile.Read(ref _fetches);

        _auth.SetAuthorized("alice"); // for example a token refresh

        cut.WaitForAssertion(() => Assert.Contains("profile of alice", cut.Markup));
        Assert.Equal(fetches, Volatile.Read(ref _fetches));
    }

    [Fact]
    public async Task Custom_identity_resets_on_tenant_switch()
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 });
        using var inert = new QueryAuthenticationGuard(client, authentication: null);
        Assert.Null(inert.CurrentIdentity);

        static ClaimsPrincipal Member(string tenant) =>
            new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant", tenant)], "test"));

        var provider = new SwitchableAuthentication(Member("a"));
        using var guard = new QueryAuthenticationGuard(
            client,
            provider,
            user => $"{QueryAuthenticationGuard.DefaultIdentity(user)}|{user.FindFirst("tenant")?.Value}");
        await TestWait.UntilAsync(() => guard.CurrentIdentity == "alice|a");
        client.SetQueryData(QueryKey.Create("orders"), "tenant a orders");

        provider.Switch(Member("b"));

        await TestWait.UntilAsync(() => !client.GetQueryData<string>(QueryKey.Create("orders")).HasData);
        Assert.Equal("alice|b", guard.CurrentIdentity);
        Assert.Null(QueryAuthenticationGuard.DefaultIdentity(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class SwitchableAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        private ClaimsPrincipal _user = user;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_user));

        public void Switch(ClaimsPrincipal user)
        {
            _user = user;
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }

    private static class TestWait
    {
        public static async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException();
                }

                await Task.Delay(5);
            }
        }
    }
}
