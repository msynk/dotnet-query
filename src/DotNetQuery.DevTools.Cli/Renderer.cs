using DotNetQuery;

namespace DotNetQuery.DevTools.Cli;

/// <summary>Renders a deserialized DevTools export as a console dashboard.</summary>
internal static class Renderer
{
    public static void Render(DevToolsExport export)
    {
        RenderStats(export.Stats);
        RenderQueries(export.Queries);
        RenderMutations(export.Mutations ?? []);
        RenderEvents(export.Events);
    }

    private static void RenderStats(StatsDto s)
    {
        Console.WriteLine("== Cache ==================================================");
        Console.WriteLine($"  queries: {s.TotalQueries}   active: {s.ActiveQueries}   stale: {s.StaleQueries}");
        Console.WriteLine($"  fetches: {s.FetchesStarted} started / {s.FetchesSucceeded} ok / {s.FetchesFailed} failed / {s.FetchesCancelled} cancelled" +
                          $"   (in flight: {s.FetchingQueries})");
        Console.WriteLine($"  mutations: {s.MutationsStarted} started / {s.MutationsCompleted} done / {s.MutationsFailed} failed / {s.PendingMutations} pending");
        Console.WriteLine($"  avg query duration: {s.AverageQueryDurationMs:F1} ms");
        if (s.StatusCounts.Count > 0)
        {
            var parts = s.StatusCounts.Select(kv => $"{StatusName(kv.Key)}={kv.Value}");
            Console.WriteLine($"  status: {string.Join("  ", parts)}");
        }

        Console.WriteLine();
    }

    private static void RenderQueries(QueryDto[] queries)
    {
        Console.WriteLine("== Queries ================================================");
        if (queries.Length == 0)
        {
            Console.WriteLine("  (none)\n");
            return;
        }

        Console.WriteLine($"  {"KEY",-22} {"STATUS",-11} {"TYPE",-12} {"DATA",-5} {"STALE",-6} {"OBS",-4} {"FETCH",-6} UPDATED");
        foreach (var q in queries)
        {
            var updated = q.DataUpdatedAt is { } t ? t.LocalDateTime.ToString("HH:mm:ss") : "-";
            Console.WriteLine($"  {Trunc(q.Key, 22),-22} {q.Status,-11} {Trunc(q.DataTypeName, 12),-12} " +
                              $"{Yes(q.HasData),-5} {Yes(q.IsStale),-6} {q.ObserverCount,-4} {q.FetchCount,-6} {updated}");
            if (!string.IsNullOrEmpty(q.ErrorMessage))
            {
                Console.WriteLine($"      └─ error: {q.ErrorMessage}");
            }
        }

        Console.WriteLine();
    }

    private static void RenderMutations(MutationDto[] mutations)
    {
        Console.WriteLine("== Mutations ==============================================");
        if (mutations.Length == 0)
        {
            Console.WriteLine("  (none)\n");
            return;
        }

        Console.WriteLine($"  {"#",-5} {"KEY",-22} {"STATUS",-8} {"DURATION",-10} ERROR");
        foreach (var m in mutations.Take(15))
        {
            var duration = m.DurationMs is { } d ? $"{d:F0} ms" : "…";
            Console.WriteLine($"  {m.MutationId,-5} {Trunc(m.MutationKey ?? "-", 22),-22} {m.Status,-8} {duration,-10} {m.ErrorMessage}");
        }

        Console.WriteLine();
    }

    private static void RenderEvents(EventDto[] events)
    {
        Console.WriteLine("== Recent events =========================================");
        if (events.Length == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }

        foreach (var e in events.TakeLast(15))
        {
            var time = e.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
            var detail = e.Key is { Length: > 0 } ? e.Key : "";
            var dur = e.DurationMs is { } d ? $" ({d:F0} ms)" : "";
            var err = e.Error is { Length: > 0 } ? $"  ! {e.Error}" : "";
            Console.WriteLine($"  {time}  {e.Type,-18} {detail}{dur}{err}");
        }
    }

    private static string StatusName(string key) =>
        int.TryParse(key, out var n) && Enum.IsDefined((QueryStatus)n) ? ((QueryStatus)n).ToString() : key;

    private static string Yes(bool value) => value ? "yes" : "no";

    private static string Trunc(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
