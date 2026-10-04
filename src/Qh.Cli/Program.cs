using Qh.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

switch (args[0])
{
    case "indicator-check":
        return IndicatorCheck();
    case "quality-check":
        return QualityCheck(args);
    case "import":
        return await ImportAsync(args);
    case "describe":
        return await DescribeAsync(args);
    case "replay":
        return await ReplayAsync(args);
    case "read-run":
        return ReadRun(args);
    case "export-signals":
        return ExportSignals(args);
    case "trade-replay":
        return await TradeReplayAsync(args);
    case "read-trade-run":
        return ReadTradeRun(args);
    case "export-trades":
        return ExportTrades(args);
    case "g4-fixture-check":
        return await G4FixtureCheckAsync(args);
    case "g4-gateway-check":
        return await G4GatewayCheckAsync();
    case "g5a-fixture-check":
        return G5ANotifyFixtureCheck(args);
    case "g5b-fixture-check":
        return await G5BStrategyFixtureCheckAsync(args);
    case "download-form-check":
        return DownloadFormCheck();
    case "local-calendar-check":
        return LocalCalendarCheck(args);
    default:
        PrintUsage();
        return 2;
}

static int IndicatorCheck()
{
    var closes = new[] { 10m, 11m, 12m, 11m, 10m, 9m };
    var bars = closes.Select((close, index) => new Bar(DateTimeOffset.Parse($"2026-10-01T09:{index:00}:00+08:00"), "TEST", "TEST", "1m", new DateOnly(2026, 10, 1), close, close + 1, close - 1, close, 1, 1, true)).ToArray();
    var full = Indicators.Calculate(bars, 3);
    var prefix = Indicators.Calculate(bars[..3], 3);
    var prefixInvariant = full[2].Sma == prefix[2].Sma;
    Console.WriteLine($"sma_full_bar3={full[2].Sma}; sma_prefix_bar3={prefix[2].Sma}; prefix_invariance={prefixInvariant}");
    Console.WriteLine($"sma={string.Join(',', full.Select(x => x.Sma?.ToString(CultureInfo.InvariantCulture) ?? "NA"))}");
    return prefixInvariant && full[2].Sma == 11m && full[3].Sma == 11.333333333333333333333333333m && full[4].Sma == 11m ? 0 : 4;
}

static int DownloadFormCheck()
{
    var whiteSugar = "CZCE.SR701";
    var manualA = HistoryFormSelection.ResolveContract(whiteSugar, "SHFE.cu2412", manualMode: false);
    var manualB = HistoryFormSelection.ResolveContract(whiteSugar, "SHFE.rb2410", manualMode: true);
    var dateOk = HistoryFormSelection.TryBuildDateRange(new DateTime(2026, 10, 1), new DateTime(2026, 10, 3), "", "", out var start, out var end, out var dateError);
    var maxDateRejected = !HistoryFormSelection.TryBuildDateRange(new DateTime(2026, 10, 1), DateTime.MaxValue, "", "", out _, out _, out var maxDateError);
    Console.WriteLine($"dropdown_after_manual_A={manualA}; expected={whiteSugar}");
    Console.WriteLine($"manual_mode_B={manualB}; expected=SHFE.rb2410");
    Console.WriteLine($"date_range_ok={dateOk}; start={start:O}; end={end:O}; error={dateError}");
    Console.WriteLine($"max_date_rejected={maxDateRejected}; error={maxDateError}");
    return manualA == whiteSugar && manualB == "SHFE.rb2410" && dateOk && start == new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(8)) && end == new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.FromHours(8)) && maxDateRejected ? 0 : 6;
}

static int LocalCalendarCheck(string[] arguments)
{
    if (arguments.Length < 5) { PrintUsage(); return 2; }
    try
    {
        var root = Path.GetFullPath(arguments[1]);
        var symbol = arguments[2];
        var start = DateTimeOffset.Parse(arguments[3], CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(arguments[4], CultureInfo.InvariantCulture);
        var store = new DataStore(root);
        var generated = HistoricalLocalCalendarGenerator.TryGenerate(store, symbol, start, end, out var generation, out var generationError);
        IReadOnlySet<DateOnly> tradingDays = new HashSet<DateOnly>();
        IReadOnlyList<HistoricalSessionRange> ranges = [];
        var source = "";
        var path = "";
        var resolveError = "";
        var resolved = generated && HistoricalCalendarResolver.TryLoad(root, symbol, start, end, out tradingDays, out ranges, out source, out path, out resolveError);
        var otherSymbolRejected = !HistoricalLocalCalendarGenerator.TryGenerate(store, "SHFE.CU9999", start, end, out _, out _);
        var outsideRejected = !HistoricalLocalCalendarGenerator.TryGenerate(store, symbol, start.AddDays(-1), end, out _, out _);
        var otherCalendarRejected = !HistoricalCalendarResolver.TryLoad(root, "SHFE.CU2412", start, end, out _, out _, out _, out _, out _);
        var outsideCalendarRejected = !HistoricalCalendarResolver.TryLoad(root, symbol, start.AddDays(-1), end, out _, out _, out _, out _, out _);
        Console.WriteLine($"generated={generated}; snapshot={generation?.SnapshotId}; calendar={generation?.CalendarPath}; audit={generation?.AuditPath}");
        Console.WriteLine($"resolved={resolved}; trading_days={(resolved ? tradingDays!.Count : 0)}; session_ranges={(resolved ? ranges!.Count : 0)}; source={(resolved ? source : resolveError)}");
        Console.WriteLine($"other_symbol_rejected={otherSymbolRejected}; outside_range_rejected={outsideRejected}; other_calendar_rejected={otherCalendarRejected}; outside_calendar_rejected={outsideCalendarRejected}");
        if (!generated) Console.WriteLine($"generation_error={generationError}");
        return generated && resolved && otherSymbolRejected && outsideRejected && otherCalendarRejected && outsideCalendarRejected ? 0 : 7;
    }
    catch (Exception ex) { Console.Error.WriteLine($"local_calendar_check_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static int QualityCheck(string[] arguments)
{
    if (arguments.Length < 3) { PrintUsage(); return 2; }
    try
    {
        var store = new DataStore(Path.GetFullPath(arguments[1]));
        var quality = store.ReadQualityReport(arguments[2]);
        Console.WriteLine($"quality_rows={quality.PublishedRows}; gaps={quality.SuspectedGaps.Count}; issues={quality.Issues.Count}; session_table={quality.SessionTableVersion}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"quality_check_failed={ex.GetType().Name}: {ex.Message}");
        return 5;
    }
}

static async Task<int> ImportAsync(string[] arguments)
{
    if (arguments.Length < 2) { PrintUsage(); return 2; }
    var source = Path.GetFullPath(arguments[1]);
    var root = arguments.Length >= 3 ? Path.GetFullPath(arguments[2]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "cli-data");
    var store = new DataStore(root);
    var result = await new CsvImporter().ImportAsync(source, store, new Progress<string>(Console.WriteLine));
    Console.WriteLine($"published={result.Published}; snapshot={result.SnapshotId}; directory={result.SnapshotDirectory}");
    Console.WriteLine($"rows={result.Report.PublishedRows}; duplicates={result.Report.DuplicateRows}; out_of_order={result.Report.OutOfOrderRows}; mixed_series={result.Report.MixedSeriesRows}; conflicts={result.Report.ConflictRows}; invalid={result.Report.InvalidRows}; incomplete={result.Report.IncompleteRows}");
    Console.WriteLine($"session_table={result.Report.SessionTableVersion}; suspected_gaps={result.Report.SuspectedGaps.Count}; issues={result.Report.Issues.Count}");
    if (!result.Published) return 1;
    var rows = store.QuerySnapshot(result.SnapshotId!);
    var points = Indicators.Calculate(rows);
    Console.WriteLine($"duckdb_rows={rows.Count}; first={rows[0].Timestamp:O}; last={rows[^1].Timestamp:O}; sma_ready={points.Count(x => x.Sma.HasValue)}");
    return rows.Count == result.Report.PublishedRows ? 0 : 3;
}

static async Task<int> DescribeAsync(string[] arguments)
{
    if (arguments.Length < 2) { PrintUsage(); return 2; }
    try
    {
        var descriptor = await StrategyCatalog.DescribeAsync(arguments[1], arguments.Length >= 3 ? arguments[2] : DefaultWorkerPath());
        Console.WriteLine(JsonSerializer.Serialize(descriptor, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"describe_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static async Task<int> ReplayAsync(string[] arguments)
{
    if (arguments.Length < 6) { PrintUsage(); return 2; }
    try
    {
        var root = Path.GetFullPath(arguments[1]);
        var snapshotId = arguments[2];
        var strategy = Path.GetFullPath(arguments[3]);
        var start = DateTimeOffset.Parse(arguments[4], CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(arguments[5], CultureInfo.InvariantCulture);
        var explore = false;
        var worker = DefaultWorkerPath();
        var cancelAfterMs = (int?)null;
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var argument in arguments.Skip(6))
        {
            if (argument == "--explore") { explore = true; continue; }
            if (argument.StartsWith("--worker=", StringComparison.Ordinal)) { worker = Path.GetFullPath(argument[9..]); continue; }
            if (argument.StartsWith("--cancel-after-ms=", StringComparison.Ordinal)) { cancelAfterMs = int.Parse(argument[18..], CultureInfo.InvariantCulture); continue; }
            var separator = argument.IndexOf('=');
            if (separator <= 0) throw new ArgumentException($"参数格式应为 name=value：{argument}");
            parameters[argument[..separator]] = ParseParameter(argument[(separator + 1)..]);
        }
        var store = new DataStore(root);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        if (cancelAfterMs is { } delayMs) _ = Task.Run(async () => { await Task.Delay(delayMs); cancellation.Cancel(); });
        var progress = new Progress<SignalReplayProgress>(item => Console.WriteLine($"run={item.RunId}; processed={item.ProcessedRows}/{item.TotalRows}; stage={item.Stage}"));
        var result = await new SignalReplayRunner(store, worker).ReplayAsync(new SignalReplayRequest(snapshotId, strategy, start, end, parameters, explore), progress, cancellation.Token);
        Console.WriteLine($"run_id={result.RunId}; status={result.Status}; signals={result.Signals.Count}; directory={result.RunDirectory}");
        if (result.ErrorMessage is not null) Console.WriteLine($"error={result.ErrorMessage}");
        return result.Status == "completed" ? 0 : 1;
    }
    catch (Exception ex) { Console.Error.WriteLine($"replay_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static int ReadRun(string[] arguments)
{
    if (arguments.Length < 3) { PrintUsage(); return 2; }
    try
    {
        var store = new SignalRunStore(Path.GetFullPath(arguments[1]));
        var record = store.ReadRecord(arguments[2]);
        var signals = store.Filter(arguments[2], arguments.Length >= 4 ? arguments[3] : null, arguments.Length >= 5 ? arguments[4] : null);
        Console.WriteLine(JsonSerializer.Serialize(new { record, signals }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"read_run_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static int ExportSignals(string[] arguments)
{
    if (arguments.Length < 4) { PrintUsage(); return 2; }
    try
    {
        var store = new SignalRunStore(Path.GetFullPath(arguments[1]));
        store.ExportCsv(arguments[2], arguments[3], arguments.Length >= 5 ? arguments[4] : null, arguments.Length >= 6 ? arguments[5] : null);
        Console.WriteLine($"exported={Path.GetFullPath(arguments[3])}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"export_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static async Task<int> TradeReplayAsync(string[] arguments)
{
    if (arguments.Length < 6) { PrintUsage(); return 2; }
    try
    {
        var root = Path.GetFullPath(arguments[1]);
        var snapshotId = arguments[2];
        var strategy = Path.GetFullPath(arguments[3]);
        var start = DateTimeOffset.Parse(arguments[4], CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(arguments[5], CultureInfo.InvariantCulture);
        var config = new TradingConfig();
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var explore = false;
        var cancelAfterMs = (int?)null;
        foreach (var argument in arguments.Skip(6))
        {
            if (argument == "--explore") { explore = true; continue; }
            if (argument.StartsWith("--cancel-after-ms=", StringComparison.Ordinal)) { cancelAfterMs = int.Parse(argument[18..], CultureInfo.InvariantCulture); continue; }
            if (argument.StartsWith("--initial-equity=", StringComparison.Ordinal)) { config = config with { InitialEquity = ParseDecimal(argument[17..]) }; continue; }
            if (argument.StartsWith("--lots=", StringComparison.Ordinal)) { config = config with { Lots = int.Parse(argument[7..], CultureInfo.InvariantCulture) }; continue; }
            if (argument.StartsWith("--multiplier=", StringComparison.Ordinal)) { config = config with { Multiplier = ParseDecimal(argument[13..]) }; continue; }
            if (argument.StartsWith("--min-tick=", StringComparison.Ordinal)) { config = config with { MinTick = ParseDecimal(argument[11..]) }; continue; }
            if (argument.StartsWith("--margin-rate=", StringComparison.Ordinal)) { config = config with { MarginRate = ParseDecimal(argument[14..]) }; continue; }
            if (argument.StartsWith("--fee-mode=", StringComparison.Ordinal)) { config = config with { FeeMode = Enum.Parse<TradingFeeMode>(argument[11..], true) }; continue; }
            if (argument.StartsWith("--open-fee=", StringComparison.Ordinal)) { config = config with { OpenFee = ParseDecimal(argument[11..]) }; continue; }
            if (argument.StartsWith("--today-fee=", StringComparison.Ordinal)) { config = config with { TodayCloseFee = ParseDecimal(argument[12..]) }; continue; }
            if (argument.StartsWith("--ordinary-fee=", StringComparison.Ordinal)) { config = config with { OrdinaryCloseFee = ParseDecimal(argument[15..]) }; continue; }
            if (argument.StartsWith("--fee-rate=", StringComparison.Ordinal)) { config = config with { FeeRate = ParseDecimal(argument[11..]) }; continue; }
            if (argument.StartsWith("--slippage-ticks=", StringComparison.Ordinal)) { config = config with { SlippageTicks = int.Parse(argument[17..], CultureInfo.InvariantCulture) }; continue; }
            if (argument.StartsWith("--stop-loss=", StringComparison.Ordinal)) { config = config with { StopLossPrice = ParseDecimal(argument[12..]) }; continue; }
            if (argument.StartsWith("--take-profit=", StringComparison.Ordinal)) { config = config with { TakeProfitPrice = ParseDecimal(argument[14..]) }; continue; }
            var separator = argument.IndexOf('=');
            if (separator <= 0) throw new ArgumentException($"参数格式应为 name=value 或 --option=value：{argument}");
            parameters[argument[..separator]] = ParseParameter(argument[(separator + 1)..]);
        }
        var store = new DataStore(root);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        if (cancelAfterMs is { } delayMs) _ = Task.Run(async () => { await Task.Delay(delayMs); cancellation.Cancel(); });
        var progress = new Progress<TradeReplayProgress>(item =>
        {
            if (item.ProcessedRows == 0 || item.ProcessedRows == item.TotalRows || item.ProcessedRows % 1000 == 0)
                Console.WriteLine($"run={item.RunId}; processed={item.ProcessedRows}/{item.TotalRows}; stage={item.Stage}");
        });
        var result = await new TradeReplayRunner(store, DefaultWorkerPath()).ReplayAsync(new TradeReplayRequest(snapshotId, strategy, start, end, config, parameters, explore), progress, cancellation.Token);
        Console.WriteLine($"run_id={result.RunId}; status={result.Status}; intents={result.Intents.Count}; fills={result.Fills.Count}; closed_trades={result.ClosedTrades.Count}; directory={result.RunDirectory}");
        if (result.ErrorMessage is not null) Console.WriteLine($"error={result.ErrorMessage}");
        if (File.Exists(Path.Combine(result.RunDirectory, "run.json"))) Console.WriteLine(File.ReadAllText(Path.Combine(result.RunDirectory, "run.json")));
        return result.Status == "completed" ? 0 : 1;
    }
    catch (Exception ex) { Console.Error.WriteLine($"trade_replay_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static int ReadTradeRun(string[] arguments)
{
    if (arguments.Length < 3) { PrintUsage(); return 2; }
    try
    {
        var store = new TradeRunStore(Path.GetFullPath(arguments[1]));
        var runId = arguments[2];
        var result = new
        {
            record = store.ReadRecord(runId),
            intents = store.ReadMany<TradeIntent>(runId, "intents.jsonl"),
            orders = store.ReadMany<OrderRecord>(runId, "orders.jsonl"),
            fills = store.ReadMany<FillRecord>(runId, "fills.jsonl"),
            trades = store.ReadMany<ClosedTradeRecord>(runId, "closed-trades.jsonl"),
            equity = store.ReadMany<EquityPoint>(runId, "equity.jsonl"),
            unfilled = store.ReadMany<JsonElement>(runId, "unfilled.jsonl")
        };
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"read_trade_run_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static int ExportTrades(string[] arguments)
{
    if (arguments.Length < 4) { PrintUsage(); return 2; }
    try { var store = new TradeRunStore(Path.GetFullPath(arguments[1])); store.ExportFillsCsv(arguments[2], arguments[3]); Console.WriteLine($"exported={Path.GetFullPath(arguments[3])}"); return 0; }
    catch (Exception ex) { Console.Error.WriteLine($"export_trades_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static async Task<int> G4FixtureCheckAsync(string[] arguments)
{
    var root = arguments.Length >= 2 ? Path.GetFullPath(arguments[1]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "g4-fixture-data");
    try
    {
        using var report = await G4FixtureVerifier.RunAsync(root, CancellationToken.None);
        var json = report.RootElement.GetRawText();
        Console.WriteLine(json);
        if (arguments.Length >= 3) await File.WriteAllTextAsync(Path.GetFullPath(arguments[2]), json, Encoding.UTF8);
        return report.RootElement.GetProperty("all_passed").GetBoolean() ? 0 : 1;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("g4_fixture_cancelled"); return 1; }
    catch (Exception ex) { Console.Error.WriteLine($"g4_fixture_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static async Task<int> G4GatewayCheckAsync()
{
    var python = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
    var bridge = Path.Combine(AppContext.BaseDirectory, "worker", "qh_tqsdk_bridge.py");
    var started = Stopwatch.GetTimestamp();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var result = await new TqSdkGateway(python, bridge).RuntimeCheckAsync(cancellation.Token);
    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    Console.WriteLine(JsonSerializer.Serialize(new { result.Success, result.ErrorKind, result.Message, phase = "dependency_prepare", elapsed_ms = Math.Round(elapsedMs, 1), runtime = result.Value, bridge_exists = File.Exists(bridge), python_exists = File.Exists(python) }));
    return result.Success ? 0 : 1;
}

static int G5ANotifyFixtureCheck(string[] arguments)
{
    var root = arguments.Length >= 2 ? Path.GetFullPath(arguments[1]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "g5a-notify-fixture");
    try
    {
        using var report = G5ANotifyFixtureVerifier.Run(root);
        var json = report.RootElement.GetRawText();
        Console.WriteLine(json);
        if (arguments.Length >= 3) File.WriteAllText(Path.GetFullPath(arguments[2]), json, Encoding.UTF8);
        return report.RootElement.GetProperty("all_passed").GetBoolean() ? 0 : 1;
    }
    catch (Exception ex) { Console.Error.WriteLine($"g5a_fixture_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static async Task<int> G5BStrategyFixtureCheckAsync(string[] arguments)
{
    var root = arguments.Length >= 2 ? Path.GetFullPath(arguments[1]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "g5b-strategy-fixture");
    var strategy = arguments.Length >= 3 ? Path.GetFullPath(arguments[2]) : Path.Combine(AppContext.BaseDirectory, "strategies", "sma_ema_cross.py");
    var worker = arguments.Length >= 4 ? Path.GetFullPath(arguments[3]) : DefaultWorkerPath();
    try
    {
        using var report = await G5BStrategyFixtureVerifier.RunAsync(root, strategy, worker);
        var json = report.RootElement.GetRawText();
        Console.WriteLine(json);
        if (arguments.Length >= 5) File.WriteAllText(Path.GetFullPath(arguments[4]), json, Encoding.UTF8);
        return report.RootElement.GetProperty("all_passed").GetBoolean() ? 0 : 1;
    }
    catch (Exception ex) { Console.Error.WriteLine($"g5b_fixture_failed={ex.GetType().Name}: {ex.Message}"); return 5; }
}

static JsonElement ParseParameter(string value)
{
    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonSerializer.SerializeToElement(integer);
    if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return JsonSerializer.SerializeToElement(number);
    if (bool.TryParse(value, out var boolean)) return JsonSerializer.SerializeToElement(boolean);
    return JsonSerializer.SerializeToElement(value);
}

static decimal ParseDecimal(string value) => decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

static string DefaultWorkerPath()
{
    var bundled = Path.Combine(AppContext.BaseDirectory, "worker", "qh_signal_worker.py");
    return File.Exists(bundled) ? bundled : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "worker", "qh_signal_worker.py"));
}

static void PrintUsage() => Console.Error.WriteLine("用法：download-form-check | local-calendar-check <data-root> <symbol> <start> <end> | import <csv> [data-root] | quality-check <data-root> <snapshot-id> | describe <strategy.py> [worker.py] | replay <data-root> <snapshot-id> <strategy.py> <start> <end> [--explore] [--cancel-after-ms=N] [--worker=worker.py] [name=value] | trade-replay <data-root> <snapshot-id> <trade.py> <start> <end> [--explore] [--option=value] [name=value] | read-run <data-root> <run-id> [category] [direction] | export-signals <data-root> <run-id> <csv> [category] [direction] | read-trade-run <data-root> <run-id> | export-trades <data-root> <run-id> <csv> | g4-fixture-check [data-root] [report.json] | g4-gateway-check | g5a-fixture-check [data-root] [report.json] | g5b-fixture-check [data-root] [strategy.py] [worker.py] [report.json]");
