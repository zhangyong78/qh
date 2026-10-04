using Qh.Core;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: <python> <bridge-wrapper.py> <fake-sdk-root> <data-root>");
    return 2;
}

Environment.SetEnvironmentVariable("QH_TQSDK_PATH", Path.GetFullPath(args[2]));
var gateway = new TqSdkGateway(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
var credentials = new TqSdkCredentials("bridge-surrogate", "not-written");
var start = DateTimeOffset.Parse("2026-09-29T09:00:00+08:00");
var dataRoot = Path.GetFullPath(args[3]);
Directory.CreateDirectory(dataRoot);
var calendarPath = Path.Combine(dataRoot, "calendar.json");
File.WriteAllText(calendarPath, JsonSerializer.Serialize(new
{
    source = "surrogate-local-calendar",
    valid_from = "2026-09-29T00:00:00+08:00",
    valid_to = "2026-09-30T00:00:00+08:00",
    trading_days = new[] { "2026-09-29" },
    session_ranges = new[]
    {
        new { trading_day = "2026-09-29", start = "2026-09-29T09:00:00+08:00", end = "2026-09-29T10:15:00+08:00", symbol_scope = "TEST.FAKE" },
        new { trading_day = "2026-09-29", start = "2026-09-29T10:30:00+08:00", end = "2026-09-29T11:30:00+08:00", symbol_scope = "TEST.FAKE" }
    }}));
if (!HistoricalCalendarMetadataLoader.TryLoad(calendarPath, "TEST.FAKE", start, start.AddMinutes(4), out var tradingDays, out var sessionRanges, out var calendarSource, out var calendarError))
{
    Console.Error.WriteLine(calendarError);
    return 1;
}
var completeRequest = new HistoricalMarketRequest("TEST.FAKE", 60, start, start.AddMinutes(4), true, 0,
    ProviderTradingDays: tradingDays, ProviderCalendarSource: calendarSource, ProviderSessionRanges: sessionRanges);
var insufficientRequest = completeRequest with { End = start.AddMinutes(6) };
var store = new DataStore(dataRoot);
var publisher = new MarketHistoryPublisher();
var success = await publisher.DownloadAndPublishAsync(gateway, credentials, completeRequest, store, CancellationToken.None);
var blocked = await publisher.DownloadAndPublishAsync(gateway, credentials, insufficientRequest, store, CancellationToken.None);
var sparseBar = new Bar(start.AddMinutes(2), "TEST.FAKE", "TEST", "1m", DateOnly.Parse("2026-09-29"), 100, 102, 99, 101, 10, 6, true);
var sparse = new HistoricalMarketResult([sparseBar], new HistoricalCoverage(sparseBar.Timestamp, sparseBar.Timestamp, 1, true, true, "provider declared complete"), "sparse-provider-result");
var sparseAccepted = HistoricalMarketValidator.TryValidate(completeRequest, sparse, out var sparseError);
var conflictBar = sparseBar with { TradingDay = DateOnly.Parse("2026-09-30") };
var conflict = new HistoricalMarketResult([conflictBar], new HistoricalCoverage(conflictBar.Timestamp, conflictBar.Timestamp, 1, false, true, "provider trading-day conflict"), "provider-trading-day-conflict");
var conflictAccepted = HistoricalMarketValidator.TryValidate(completeRequest, conflict, out var conflictError);
var report = new
{
    scope = "actual qh_tqsdk_bridge.py -> TqSdkGateway -> MarketHistoryPublisher with SDK-format surrogate; no account login",
    success = new { success.Success, error = success.ErrorKind.ToString(), snapshot = success.Value?.SnapshotId },
    insufficient_coverage = new { blocked.Success, error = blocked.ErrorKind.ToString(), message = blocked.Message },
    provider_complete_but_sparse = new { accepted = sparseAccepted, error = sparseError },
    provider_trading_day_conflict = new { accepted = conflictAccepted, error = conflictError },
    published_snapshot_count = store.GetSnapshots().Count
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return success.Success && !blocked.Success && blocked.ErrorKind == MarketErrorKind.InvalidRequest && !sparseAccepted && !conflictAccepted ? 0 : 1;
