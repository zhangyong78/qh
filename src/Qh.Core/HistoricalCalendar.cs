using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qh.Core;

public sealed record HistoricalCalendarGenerationResult(
    string CalendarPath,
    string AuditPath,
    string SnapshotId,
    string Symbol,
    string Period,
    int BarCount,
    int TradingDayCount,
    int SessionRangeCount,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    string BarsSha256,
    string SourcePath);

public static class HistoricalCalendarResolver
{
    public static IReadOnlyList<string> CandidatePaths(string dataRoot)
    {
        var paths = new List<string>();
        var configured = Environment.GetEnvironmentVariable("QH_TQSDK_CALENDAR_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) paths.Add(Path.GetFullPath(configured.Trim()));
        paths.Add(Path.Combine(AppContext.BaseDirectory, "calendar.json"));
        if (!string.IsNullOrWhiteSpace(dataRoot)) paths.Add(Path.Combine(Path.GetFullPath(dataRoot), "calendar.json"));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool TryLoad(
        string dataRoot,
        string symbol,
        DateTimeOffset start,
        DateTimeOffset end,
        out IReadOnlySet<DateOnly> tradingDays,
        out IReadOnlyList<HistoricalSessionRange> sessionRanges,
        out string source,
        out string calendarPath,
        out string error)
    {
        tradingDays = new HashSet<DateOnly>();
        sessionRanges = [];
        source = "";
        calendarPath = "";
        error = "";
        var existing = new List<string>();
        var failures = new List<string>();
        foreach (var candidate in CandidatePaths(dataRoot))
        {
            if (!File.Exists(candidate)) continue;
            existing.Add(candidate);
            if (HistoricalCalendarMetadataLoader.TryLoad(candidate, symbol, start, end, out tradingDays, out sessionRanges, out source, out var loadError))
            {
                calendarPath = candidate;
                return true;
            }
            failures.Add($"{Path.GetFileName(candidate)}：{loadError}");
        }

        if (existing.Count == 0)
        {
            var candidates = string.Join("；", CandidatePaths(dataRoot).Select(Path.GetFullPath));
            error = $"未找到本机研究日历，不会发起供应商请求。检查顺序：{candidates}。可点击“从当前已验证快照生成本机研究日历”。";
        }
        else error = "找到本机日历，但没有一份适用于当前具体合约和日期范围：" + string.Join("；", failures);
        return false;
    }
}

public static class HistoricalLocalCalendarGenerator
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private static readonly TimeSpan SupportedPeriod = TimeSpan.FromMinutes(15);

    public static bool TryGenerate(
        DataStore store,
        string symbol,
        DateTimeOffset requestedStart,
        DateTimeOffset requestedEnd,
        out HistoricalCalendarGenerationResult? result,
        out string error)
    {
        result = null;
        error = "";
        symbol = symbol.Trim();
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Contains('@') || symbol.Contains("888", StringComparison.OrdinalIgnoreCase) || symbol.Contains("999", StringComparison.OrdinalIgnoreCase))
        {
            error = "只允许为具体合约生成研究日历，不能使用主连、指数、888 或 999。";
            return false;
        }

        var snapshot = store.GetSnapshots()
            .Where(item => string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.Period, "15m", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        if (snapshot is null)
        {
            error = $"当前数据根没有 {symbol} 的已验证 15 分钟快照，不能生成日历；不会猜测节假日或通用交易时段。";
            return false;
        }

        IReadOnlyList<Bar> bars;
        try { bars = store.QuerySnapshot(snapshot.SnapshotId); }
        catch (Exception exception) { error = $"读取已验证快照失败：{exception.Message}"; return false; }
        if (bars.Count == 0 || bars.Any(bar => !bar.IsComplete || !string.Equals(bar.Symbol, symbol, StringComparison.OrdinalIgnoreCase) || !string.Equals(bar.Period, "15m", StringComparison.OrdinalIgnoreCase)))
        {
            error = "快照不是完整、单一的 15 分钟具体合约序列，不能生成研究日历。";
            return false;
        }

        var firstLocalDate = DateOnly.FromDateTime(bars[0].Timestamp.ToOffset(BeijingOffset).DateTime);
        var lastTradingDay = bars.Max(bar => bar.TradingDay);
        var validFrom = new DateTimeOffset(firstLocalDate.ToDateTime(TimeOnly.MinValue), BeijingOffset);
        var validTo = new DateTimeOffset(lastTradingDay.AddDays(1).ToDateTime(TimeOnly.MinValue), BeijingOffset);
        var requestedLocalStart = requestedStart.ToOffset(BeijingOffset);
        var requestedLocalEnd = requestedEnd.ToOffset(BeijingOffset);
        if (requestedLocalStart < validFrom || requestedLocalEnd > validTo)
        {
            error = $"当前请求超出快照覆盖范围：快照日历 {validFrom:yyyy-MM-dd}～{validTo:yyyy-MM-dd}；不会扩展或猜测范围。";
            return false;
        }

        var tradingDays = bars.Select(bar => bar.TradingDay).Distinct().OrderBy(day => day).ToArray();
        var sessionRanges = BuildSessionRanges(bars, symbol);
        if (tradingDays.Length == 0 || sessionRanges.Count == 0)
        {
            error = "已验证快照没有可审计的交易日或连续实际时段，不能生成日历。";
            return false;
        }

        var snapshotDirectory = Path.Combine(store.SnapshotsDirectory, snapshot.SnapshotId);
        var barsPath = Path.Combine(snapshotDirectory, "bars.csv");
        var sourcePath = Path.Combine(snapshotDirectory, "source.txt");
        var metaPath = Path.Combine(snapshotDirectory, "snapshot.meta");
        if (!File.Exists(barsPath) || !File.Exists(sourcePath) || !File.Exists(metaPath))
        {
            error = "已验证快照缺少 bars.csv、source.txt 或 snapshot.meta，不能生成可追溯日历。";
            return false;
        }

        var barsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(barsPath))).ToLowerInvariant();
        var sourceText = File.ReadAllText(sourcePath, Encoding.UTF8).Trim();
        var calendarPath = Path.Combine(store.RootDirectory, "calendar.json");
        var auditPath = Path.Combine(store.RootDirectory, "calendar-generation-audit.json");
        var source = $"local-snapshot:{snapshot.SnapshotId}:derived-from-verified-completed-bars:not-official-provider-calendar";
        var calendar = new
        {
            source,
            official_provider_calendar = false,
            note = "仅由本机已验证、已完成的具体合约 15 分钟快照推导；不代表供应商官方交易日历。",
            symbol_scope = symbol,
            period = snapshot.Period,
            valid_from = validFrom.ToString("O", CultureInfo.InvariantCulture),
            valid_to = validTo.ToString("O", CultureInfo.InvariantCulture),
            trading_days = tradingDays.Select(day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(),
            session_ranges = sessionRanges.Select(range => new { trading_day = range.TradingDay, start = range.Start, end = range.End, symbol_scope = range.SymbolScope }).ToArray(),
            derived_from_snapshot = snapshot.SnapshotId,
            snapshot_content_hash = snapshot.ContentHash,
            bars_sha256 = barsSha256,
            source_file = sourceText,
            generated_at = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
        };
        var audit = new
        {
            action = "generate-local-research-calendar",
            generated_at = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            root = store.RootDirectory,
            calendar_path = calendarPath,
            symbol,
            requested_start = requestedLocalStart.ToString("O", CultureInfo.InvariantCulture),
            requested_end = requestedLocalEnd.ToString("O", CultureInfo.InvariantCulture),
            snapshot_id = snapshot.SnapshotId,
            snapshot_period = snapshot.Period,
            snapshot_rows = bars.Count,
            snapshot_directory = snapshotDirectory,
            source_file = sourceText,
            snapshot_content_hash = snapshot.ContentHash,
            bars_sha256 = barsSha256,
            valid_from = validFrom.ToString("O", CultureInfo.InvariantCulture),
            valid_to = validTo.ToString("O", CultureInfo.InvariantCulture),
            trading_day_count = tradingDays.Length,
            session_range_count = sessionRanges.Count,
            warning = "研究日历由本机已验证快照推导，不是供应商官方日历；生成后仍需显式检查覆盖或下载并发布。"
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(calendarPath, JsonSerializer.Serialize(calendar, options) + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, options) + Environment.NewLine, new UTF8Encoding(false));
        result = new HistoricalCalendarGenerationResult(calendarPath, auditPath, snapshot.SnapshotId, symbol, snapshot.Period, bars.Count, tradingDays.Length, sessionRanges.Count, validFrom, validTo, barsSha256, sourceText);
        return true;
    }

    private static IReadOnlyList<HistoricalSessionRange> BuildSessionRanges(IReadOnlyList<Bar> bars, string symbol)
    {
        var ranges = new List<HistoricalSessionRange>();
        foreach (var dayGroup in bars.GroupBy(bar => bar.TradingDay).OrderBy(group => group.Key))
        {
            var ordered = dayGroup.OrderBy(bar => bar.Timestamp).ToArray();
            var segmentStart = ordered[0].Timestamp.ToOffset(BeijingOffset);
            var previous = ordered[0].Timestamp.ToOffset(BeijingOffset);
            foreach (var bar in ordered.Skip(1))
            {
                var current = bar.Timestamp.ToOffset(BeijingOffset);
                if (current - previous != SupportedPeriod)
                {
                    ranges.Add(CreateRange(dayGroup.Key, segmentStart, previous + SupportedPeriod, symbol));
                    segmentStart = current;
                }
                previous = current;
            }
            ranges.Add(CreateRange(dayGroup.Key, segmentStart, previous + SupportedPeriod, symbol));
        }
        return ranges;
    }

    private static HistoricalSessionRange CreateRange(DateOnly tradingDay, DateTimeOffset start, DateTimeOffset end, string symbol) =>
        new(tradingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), start.ToString("O", CultureInfo.InvariantCulture), end.ToString("O", CultureInfo.InvariantCulture), symbol);
}
