using System.Globalization;

namespace Qh.Core;

public sealed record Bar(
    DateTimeOffset Timestamp,
    string Symbol,
    string Exchange,
    string Period,
    DateOnly TradingDay,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal OpenInterest,
    bool IsComplete);

public sealed record CsvIssue(string Code, int? Row, string Message);

public sealed class QualityReport
{
    public int InputRows { get; init; }
    public int PublishedRows { get; init; }
    public int DuplicateRows { get; init; }
    public int OutOfOrderRows { get; init; }
    public int MixedSeriesRows { get; init; }
    public int ConflictRows { get; init; }
    public int InvalidRows { get; init; }
    public int IncompleteRows { get; init; }
    public IReadOnlyList<CsvIssue> Issues { get; init; } = [];
    public IReadOnlyList<string> SuspectedGaps { get; init; } = [];
    public string SessionTableVersion { get; init; } = "unknown";
    public DateTimeOffset? FirstTimestamp { get; init; }
    public DateTimeOffset? LastTimestamp { get; init; }
    public string? Symbol { get; init; }
    public string? Exchange { get; init; }
    public string? Period { get; init; }
    public bool CanPublish => ConflictRows == 0 && InvalidRows == 0 && IncompleteRows == 0 && MixedSeriesRows == 0 && PublishedRows > 0;
}

public sealed record ImportResult(
    bool Published,
    string? SnapshotId,
    string? SnapshotDirectory,
    QualityReport Report,
    string Message);

public sealed record SnapshotInfo(
    string SnapshotId,
    string Directory,
    string Symbol,
    string Exchange,
    string Period,
    DateTimeOffset FirstTimestamp,
    DateTimeOffset LastTimestamp,
    int RowCount,
    string SourcePath,
    string ContentHash,
    DateTimeOffset CreatedAt)
{
    public string PeriodDisplayName => PeriodLabel(Period);
    public bool IsMainContinuous => TradeReplayRunner.IsMainContinuousSymbol(Symbol);
    public string SeriesDisplayName => IsMainContinuous
        ? (string.Equals(Symbol, "KQ.m@CZCE.SR", StringComparison.OrdinalIgnoreCase) ? "白糖主连（研究）" : $"{Symbol}（主连研究）")
        : Symbol;
    public string ContractPeriodDisplayName => $"{SeriesDisplayName} · {PeriodDisplayName} · {FirstTimestamp:yyyy-MM-dd}～{LastTimestamp:yyyy-MM-dd}";

    public string DisplayName =>
        $"{SeriesDisplayName} · {PeriodDisplayName} · {FirstTimestamp:yyyy-MM-dd}～{LastTimestamp:yyyy-MM-dd} · {RowCount:N0} 根";

    private static string PeriodLabel(string period)
    {
        if (string.IsNullOrWhiteSpace(period)) return "未知周期";
        var normalized = period.Trim().ToLowerInvariant();
        if (normalized.EndsWith("m") && int.TryParse(normalized[..^1], out var minutes)) return $"{minutes} 分钟";
        if (normalized.EndsWith("h") && int.TryParse(normalized[..^1], out var hours)) return $"{hours} 小时";
        if (normalized.EndsWith("d") && int.TryParse(normalized[..^1], out var days)) return days == 1 ? "日线" : $"{days} 日";
        if (normalized.EndsWith("s") && int.TryParse(normalized[..^1], out var seconds)) return seconds % 60 == 0 ? $"{seconds / 60} 分钟" : $"{seconds} 秒";
        return period;
    }
}

public sealed record IndicatorPoint(DateTimeOffset Timestamp, decimal? Sma, decimal? Ema, decimal? Atr);

internal static class DecimalParser
{
    public static bool TryParse(string? value, out decimal result) =>
        decimal.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
