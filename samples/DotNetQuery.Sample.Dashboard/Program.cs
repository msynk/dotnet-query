using DotNetQuery;
using DotNetQuery.Sample.Dashboard;

// A dashboard loads several widgets at once. This shows two DotNetQuery behaviors:
//   1. request deduplication - concurrent requests for the same key share one fetch, and
//   2. background refresh - an observed query auto-polls on an interval.
// Deterministic and self-terminating so it doubles as an end-to-end smoke test (exit 0 == pass).

using var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 });
var api = new DashboardApi();

// --- Concurrent widgets + deduplication -----------------------------------------------------------
Console.WriteLine("=== Dashboard: concurrent widgets ===\n");
var usersKey = QueryKey.Create("users", "active");
var revenueKey = QueryKey.Create("revenue", "today");
var ordersKey = QueryKey.Create("orders", "pending");

// Four widget requests fired at once - but two of them ask for the same [users] key.
var usersA = client.FetchAsync(usersKey, api.GetActiveUsersAsync);
var usersB = client.FetchAsync(usersKey, api.GetActiveUsersAsync);
var revenue = client.FetchAsync(revenueKey, api.GetRevenueAsync);
var orders = client.FetchAsync(ordersKey, api.GetPendingOrdersAsync);
await Task.WhenAll(usersA, usersB, revenue, orders);

Console.WriteLine($"  [users]   {usersA.Result.Data:N0} active");
Console.WriteLine($"  [revenue] {revenue.Result.Data:C}");
Console.WriteLine($"  [orders]  {orders.Result.Data} pending\n");
Console.WriteLine($"4 widget requests fired; the 2 for [users] were deduplicated → the server saw " +
                  $"{api.UsersCalls} users call, {api.RevenueCalls} revenue, {api.OrdersCalls} orders.\n");

// --- Background refresh (auto-polling) ------------------------------------------------------------
Console.WriteLine("=== Background refresh (auto-polling) ===\n");
var visitorsKey = QueryKey.Create("visitors", "live");
using var subscription = client.Watch<int>(visitorsKey); // an observer keeps the interval timer running

await client.FetchAsync(visitorsKey, api.GetLiveVisitorsAsync,
    new QueryOptions { RefetchInterval = TimeSpan.FromMilliseconds(200) });
var before = client.GetQueryData<int>(visitorsKey).Data;
Console.WriteLine($"  [visitors] {before} (initial)");

await Task.Delay(700); // let the 200 ms poll fire a few times
var after = client.GetQueryData<int>(visitorsKey).Data;
Console.WriteLine($"  [visitors] {after} (after background polling)\n");

var ok = api.UsersCalls == 1          // two concurrent [users] requests collapsed into one fetch
    && usersA.Result.Data == 1234
    && revenue.Result.IsSuccess
    && orders.Result.Data == 42
    && after > before;                // the interval refetched in the background
Console.WriteLine(ok ? "DONE (pass)" : "DONE (FAIL)");
return ok ? 0 : 1;
