using Microsoft.AspNetCore.SignalR.Client;

namespace DotNetQuery.Demo.Realtime;

/// <summary>
/// Simulates the client's network for the demo: <see cref="Drop"/> fails every request in flight and every new one
/// (the SignalR connection is lost and its automatic reconnect keeps failing) until <see cref="Restore"/>.
/// A real app uses the default handler.
/// </summary>
public sealed class SimulatedNetwork
{
    private CancellationTokenSource _down = new();

    public bool IsDown { get; private set; }

    public void Drop()
    {
        IsDown = true;
        var previous = _down;
        _down = new CancellationTokenSource();
        previous.Cancel();
    }

    public void Restore() => IsDown = false;

    public HttpMessageHandler Wrap(HttpMessageHandler inner) => new Handler(this, inner);

    private sealed class Handler(SimulatedNetwork network, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (network.IsDown)
            {
                throw new HttpRequestException("simulated network outage");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, network._down.Token);
            try
            {
                return await base.SendAsync(request, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("simulated network outage");
            }
        }
    }
}

/// <summary>Retries forever every 200 ms (a real app would back off).</summary>
internal sealed class SteadyRetry : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromMilliseconds(200);
}
