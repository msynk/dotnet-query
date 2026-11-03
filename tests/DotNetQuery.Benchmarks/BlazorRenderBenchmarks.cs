using BenchmarkDotNet.Attributes;
using Bunit;
using DotNetQuery;
using DotNetQuery.Blazor;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetQuery.Benchmarks;

/// <summary>
/// Rendering cost of the Blazor <c>&lt;Query&gt;</c> component (through bUnit's renderer, so absolute numbers include
/// its overhead): first render from a warm cache, and the re-render a cache write triggers.
/// </summary>
[MemoryDiagnoser]
public class BlazorRenderBenchmarks
{
    private readonly QueryKey _key = QueryKey.Create("render");
    private static readonly Func<CancellationToken, Task<List<int>>> Fetcher = _ => Task.FromResult(Enumerable.Range(0, 20).ToList());
    private TestContext _context = default!;
    private IQueryClient _client = default!;
    private IRenderedComponent<Query<List<int>>> _rendered = default!;
    private int _version;

    [GlobalSetup]
    public void Setup()
    {
        _context = new TestContext();
        _context.Services.AddDotNetQueryBlazor(o => o.DefaultStaleTime = TimeSpan.FromHours(1));
        _client = _context.Services.GetRequiredService<IQueryClient>();
        _client.FetchAsync(_key, Fetcher).GetAwaiter().GetResult();
        _rendered = Render();
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    /// <summary>Mounting a <c>&lt;Query&gt;</c> over cached data (20 rows).</summary>
    [Benchmark]
    public string RenderFromWarmCache()
    {
        var rendered = Render();
        var markup = rendered.Markup;
        rendered.Instance.Dispose();
        return markup;
    }

    /// <summary>A cache write re-rendering a mounted <c>&lt;Query&gt;</c>.</summary>
    [Benchmark]
    public int ReRenderOnCacheWrite()
    {
        var before = _rendered.RenderCount;
        _client.SetQueryData(_key, Enumerable.Range(++_version, 20).ToList());
        _rendered.WaitForState(() => _rendered.RenderCount > before);
        return _rendered.RenderCount;
    }

    private IRenderedComponent<Query<List<int>>> Render() =>
        _context.RenderComponent<Query<List<int>>>(ps => ps
            .Add(p => p.Key, _key)
            .Add(p => p.Fetcher, Fetcher)
            .Add(p => p.ChildContent, result => builder =>
            {
                foreach (var item in result.Data ?? [])
                {
                    builder.OpenElement(0, "li");
                    builder.AddContent(1, item);
                    builder.CloseElement();
                }
            }));
}
