using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Qh.Core;

public sealed class CsvImporter
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    public const string SessionTableVersion = "cn-futures-v2-czce-sr-2300";
    private static readonly string[] Required = ["timestamp", "symbol", "exchange", "period", "trading_day", "open", "high", "low", "close", "volume", "open_interest", "is_complete"];
    private static readonly TradingSession[] Sessions =
    [
        new("day-am", new TimeOnly(9, 0), new TimeOnly(10, 15)),
        new("day-mid", new TimeOnly(10, 30), new TimeOnly(11, 30)),
        new("day-pm", new TimeOnly(13, 30), new TimeOnly(15, 0)),
        new("night-early", new TimeOnly(21, 0), new TimeOnly(23, 0)),
        new("night-late", new TimeOnly(23, 0), new TimeOnly(2, 30))
    ];

    public async Task<ImportResult> ImportAsync(
        string sourcePath,
        DataStore store,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到 CSV 文件", sourcePath);

        progress?.Report("读取 CSV");
        var lines = await File.ReadAllLinesAsync(sourcePath, Encoding.UTF8, cancellationToken);
        if (lines.Length == 0)
            return Failed("CSV 为空", new CsvIssue("empty", null, "文件没有内容"));

        var headers = ParseLine(lines[0]).Select(NormalizeHeader).ToArray();
        var indexes = Required.ToDictionary(x => x, x => Array.IndexOf(headers, x), StringComparer.OrdinalIgnoreCase);
        var missing = Required.Where(x => indexes[x] < 0).ToArray();
        if (missing.Length > 0)
            return Failed("缺少必要字段: " + string.Join(", ", missing), new CsvIssue("missing_column", 1, string.Join(", ", missing)));

        var issues = new List<CsvIssue>();
        var bars = new Dictionary<DateTimeOffset, Bar>();
        var inputRows = 0;
        var duplicateRows = 0;
        var outOfOrderRows = 0;
        var conflictRows = 0;
        var mixedSeriesRows = 0;
        var invalidRows = 0;
        var incompleteRows = 0;
        DateTimeOffset? maxSeenTimestamp = null;
        string? expectedSymbol = null;
        string? expectedExchange = null;
        string? expectedPeriod = null;

        for (var lineNumber = 1; lineNumber < lines.Length; lineNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[lineNumber])) continue;
            inputRows++;
            var fields = ParseLine(lines[lineNumber]);
            if (fields.Length != headers.Length)
            {
                invalidRows++;
                issues.Add(new("column_count", lineNumber + 1, $"列数 {fields.Length}，预期 {headers.Length}"));
                continue;
            }

            if (!TryParseBar(fields, indexes, out var bar, out var error))
            {
                invalidRows++;
                issues.Add(new("invalid_row", lineNumber + 1, error!));
                continue;
            }

            if (expectedSymbol is null)
            {
                expectedSymbol = bar.Symbol;
                expectedExchange = bar.Exchange;
                expectedPeriod = bar.Period;
            }
            else if (!string.Equals(expectedSymbol, bar.Symbol, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(expectedExchange, bar.Exchange, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(expectedPeriod, bar.Period, StringComparison.OrdinalIgnoreCase))
            {
                mixedSeriesRows++;
                issues.Add(new("mixed_series", lineNumber + 1, $"单次导入必须是单一合约/交易所/周期，收到 {bar.Symbol}/{bar.Exchange}/{bar.Period}，预期 {expectedSymbol}/{expectedExchange}/{expectedPeriod}"));
                continue;
            }

            if (maxSeenTimestamp.HasValue && bar.Timestamp < maxSeenTimestamp.Value) outOfOrderRows++;
            if (!maxSeenTimestamp.HasValue || bar.Timestamp > maxSeenTimestamp.Value) maxSeenTimestamp = bar.Timestamp;

            if (!bar.IsComplete)
            {
                incompleteRows++;
                issues.Add(new("incomplete_bar", lineNumber + 1, "未完成 K 线不能进入严格发布快照"));
            }

            if (bars.TryGetValue(bar.Timestamp, out var existing))
            {
                if (existing == bar) duplicateRows++;
                else
                {
                    conflictRows++;
                    issues.Add(new("conflict", lineNumber + 1, $"时间 {bar.Timestamp:O} 存在内容冲突"));
                }
                continue;
            }
            bars[bar.Timestamp] = bar;
        }

        var sorted = bars.Values.OrderBy(x => x.Timestamp).ToArray();
        var gaps = FindSuspectedGaps(sorted);
        if (sorted.Length == 0)
            return new ImportResult(false, null, null, BuildReport(), "没有可用 K 线");

        var outOfOrder = bars.Keys.SequenceEqual(sorted.Select(x => x.Timestamp)) ? 0 : 0; // 排序后发布，乱序由报告时间线保留
        _ = outOfOrder;
        progress?.Report("生成质量报告");

        QualityReport BuildReport() => new()
        {
            InputRows = inputRows,
            PublishedRows = sorted.Length,
            DuplicateRows = duplicateRows,
            OutOfOrderRows = outOfOrderRows,
            ConflictRows = conflictRows,
            InvalidRows = invalidRows,
            IncompleteRows = incompleteRows,
            MixedSeriesRows = mixedSeriesRows,
            SessionTableVersion = SessionTableVersion,
            Issues = issues,
            SuspectedGaps = gaps,
            FirstTimestamp = sorted.FirstOrDefault()?.Timestamp,
            LastTimestamp = sorted.LastOrDefault()?.Timestamp,
            Symbol = sorted.FirstOrDefault()?.Symbol,
            Exchange = sorted.FirstOrDefault()?.Exchange,
            Period = sorted.FirstOrDefault()?.Period
        };

        var report = BuildReport();
        if (!report.CanPublish)
            return new ImportResult(false, null, null, report, "质量检查未通过，未发布数据快照");

        progress?.Report("发布内容寻址数据快照");
        var canonical = CanonicalText(sorted);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var snapshotId = $"snapshot-{hash[..16]}";
        var snapshotDirectory = await store.PublishAsync(snapshotId, sourcePath, canonical, hash, sorted, report, progress, cancellationToken);
        return new ImportResult(true, snapshotId, snapshotDirectory, report, "导入并发布成功");
    }

    private static ImportResult Failed(string message, CsvIssue issue) =>
        new(false, null, null, new QualityReport { Issues = [issue], InvalidRows = 1 }, message);

    private static bool TryParseBar(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> indexes, out Bar bar, out string? error)
    {
        bar = default!;
        error = null;
        var timestampText = fields[indexes["timestamp"]].Trim();
        DateTimeOffset timestamp;
        var hasExplicitOffset = Regex.IsMatch(timestampText, @"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (hasExplicitOffset && DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset))
            timestamp = withOffset.ToOffset(ChinaOffset);
        else if (DateTime.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withoutOffset))
            timestamp = new DateTimeOffset(DateTime.SpecifyKind(withoutOffset, DateTimeKind.Unspecified), ChinaOffset);
        else { error = "timestamp 无法解析"; return false; }

        if (!DateOnly.TryParse(fields[indexes["trading_day"]].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var tradingDay))
        { error = "trading_day 无法解析"; return false; }

        if (!DecimalParser.TryParse(fields[indexes["open"]], out var open) ||
            !DecimalParser.TryParse(fields[indexes["high"]], out var high) ||
            !DecimalParser.TryParse(fields[indexes["low"]], out var low) ||
            !DecimalParser.TryParse(fields[indexes["close"]], out var close) ||
            !DecimalParser.TryParse(fields[indexes["volume"]], out var volume) ||
            !DecimalParser.TryParse(fields[indexes["open_interest"]], out var openInterest))
        { error = "OHLC/volume/open_interest 存在非法数值"; return false; }

        if (open <= 0 || high <= 0 || low <= 0 || close <= 0 || low > high || open < low || open > high || close < low || close > high || volume < 0 || openInterest < 0)
        { error = "OHLC 范围或成交量/持仓量不合法"; return false; }

        if (!bool.TryParse(fields[indexes["is_complete"]].Trim(), out var isComplete))
        { error = "is_complete 必须为 true/false"; return false; }

        bar = new Bar(timestamp, fields[indexes["symbol"]].Trim(), fields[indexes["exchange"]].Trim(), fields[indexes["period"]].Trim(), tradingDay, open, high, low, close, volume, openInterest, isComplete);
        if (string.IsNullOrWhiteSpace(bar.Symbol) || string.IsNullOrWhiteSpace(bar.Exchange) || string.IsNullOrWhiteSpace(bar.Period))
        { error = "symbol/exchange/period 不能为空"; return false; }
        return true;
    }

    private static string[] ParseLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { values.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        values.Add(current.ToString());
        return values.ToArray();
    }

    private static string NormalizeHeader(string header) => header.Trim().Trim('\uFEFF').ToLowerInvariant().Replace(" ", "_");

    private static IReadOnlyList<string> FindSuspectedGaps(IReadOnlyList<Bar> bars)
    {
        if (bars.Count < 2) return [];
        var result = new List<string>();
        var minutes = bars[0].Period.EndsWith("m", StringComparison.OrdinalIgnoreCase) && int.TryParse(bars[0].Period[..^1], out var parsed) ? parsed : 0;
        if (minutes <= 0) return result;
        var expected = TimeSpan.FromMinutes(minutes);
        for (var i = 1; i < bars.Count; i++)
        {
            var previous = bars[i - 1];
            var current = bars[i];
            var delta = current.Timestamp - previous.Timestamp;
            if (previous.TradingDay != current.TradingDay) continue;
            if (delta <= expected) continue;
            var missing = new List<DateTimeOffset>();
            for (var candidate = previous.Timestamp + expected; candidate < current.Timestamp; candidate += expected)
            {
                if (IsTradableTimestamp(candidate, previous.Symbol, minutes)) missing.Add(candidate);
            }
            if (missing.Count == 0) continue;
            var segments = new List<string>();
            var segmentStart = missing[0];
            var segmentEnd = missing[0];
            for (var index = 1; index < missing.Count; index++)
            {
                if (missing[index] - segmentEnd == expected) segmentEnd = missing[index];
                else
                {
                    segments.Add($"{segmentStart:MM-dd HH:mm}～{segmentEnd:MM-dd HH:mm}");
                    segmentStart = segmentEnd = missing[index];
                }
            }
            segments.Add($"{segmentStart:MM-dd HH:mm}～{segmentEnd:MM-dd HH:mm}");
            result.Add($"{previous.Timestamp:yyyy-MM-dd HH:mm} 至 {current.Timestamp:yyyy-MM-dd HH:mm}，有效时段缺少 {missing.Count} 根（{string.Join("；", segments)}）");
        }
        return result;
    }

    internal static bool IsTradableTimestamp(DateTimeOffset timestamp, string? symbol = null, int periodMinutes = 15)
    {
        var local = timestamp.ToOffset(ChinaOffset);
        var time = TimeOnly.FromDateTime(local.DateTime);
        var duration = TimeSpan.FromMinutes(periodMinutes);
        // CZCE white sugar closes at 23:00; Friday's night session belongs to
        // Monday, but Saturday/Sunday are not missing trading sessions.
        // Source: CZCE white sugar business rules, article 8 (2024-06-26).
        if (symbol is not null && Regex.IsMatch(symbol, @"^(CZCE\.SR\d+|KQ\.m@CZCE\.SR)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            return Sessions.Take(4).Any(session => session.ContainsFullBar(time, duration));
        }
        return Sessions.Any(session => session.ContainsFullBar(time, duration));
    }

    private sealed record TradingSession(string Id, TimeOnly Start, TimeOnly End)
    {
        public bool Contains(TimeOnly time) => Start < End ? time >= Start && time < End : time >= Start || time < End;

        public bool ContainsFullBar(TimeOnly time, TimeSpan duration)
        {
            if (Start < End)
                return time >= Start && time < End && time.ToTimeSpan() + duration <= End.ToTimeSpan();

            if (time >= Start)
                return (TimeSpan.FromDays(1) - time.ToTimeSpan()) + End.ToTimeSpan() >= duration;
            return time < End && End.ToTimeSpan() - time.ToTimeSpan() >= duration;
        }
    }

    private static string CanonicalText(IEnumerable<Bar> bars) => string.Join('\n', bars.Select(x => string.Join(',',
        x.Timestamp.ToString("O", CultureInfo.InvariantCulture), x.Symbol, x.Exchange, x.Period, x.TradingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        x.Open.ToString(CultureInfo.InvariantCulture), x.High.ToString(CultureInfo.InvariantCulture), x.Low.ToString(CultureInfo.InvariantCulture), x.Close.ToString(CultureInfo.InvariantCulture),
        x.Volume.ToString(CultureInfo.InvariantCulture), x.OpenInterest.ToString(CultureInfo.InvariantCulture), x.IsComplete ? "true" : "false")));
}
