using System.Text.Json;

namespace Qh.Core;

public sealed record StrategyParameter(
    string Type,
    JsonElement Default,
    JsonElement? Min = null,
    JsonElement? Max = null,
    IReadOnlyList<JsonElement>? Choices = null);

public sealed record StrategyDescriptor(
    string Name,
    string Version,
    string Kind,
    int WarmupBars,
    IReadOnlyList<string> WarmupParameterNames,
    IReadOnlyDictionary<string, StrategyParameter> Parameters,
    string StrategyPath,
    string StrategySha256,
    string MetadataJson);

public sealed record SignalReplayRequest(
    string SnapshotId,
    string StrategyPath,
    DateTimeOffset StartInclusive,
    DateTimeOffset EndExclusive,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null,
    bool Explore = false,
    double StrategyTimeoutSeconds = 2.0);

public sealed record SignalEvent(
    string RunId,
    string SnapshotId,
    DateTimeOffset BarEnd,
    DateTimeOffset BarStart,
    string Symbol,
    string Category,
    string Direction,
    string Description,
    decimal? ReferencePrice,
    bool IsFormal,
    int Sequence,
    string RawJson);

public sealed class SignalRunRecord
{
    public string RunId { get; init; } = "";
    public string Status { get; set; } = "created";
    public string? ErrorKind { get; set; }
    public string? ErrorMessage { get; set; }
    public string SnapshotId { get; init; } = "";
    public string StrategyName { get; init; } = "";
    public string StrategyVersion { get; init; } = "";
    public string StrategySha256 { get; init; } = "";
    public string StrategySnapshotPath { get; init; } = "";
    public string StrategyMetadataJson { get; init; } = "{}";
    public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.Ordinal);
    public DateTimeOffset StartInclusive { get; init; }
    public DateTimeOffset EndExclusive { get; init; }
    public bool Explore { get; init; }
    public bool DataIncomplete { get; set; }
    public int InputRows { get; set; }
    public int ProcessedRows { get; set; }
    public int FormalSignalCount { get; set; }
    public int WarmupSignalCount { get; set; }
    public int WarmupBarsRequired { get; set; }
    public int WarmupBarsAvailable { get; set; }
    public int FormalRows { get; set; }
    public string? CoverageMessage { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string IndicatorFormulaVersion { get; init; } = "qh-g2-frozen-v1";
    public string SoftwareVersion { get; init; } = "qh-g2";
}

public sealed record SignalReplayProgress(string RunId, int ProcessedRows, int TotalRows, string Stage);

public sealed record SignalCoverage(
    bool IsReady,
    int WarmupBarsRequired,
    int WarmupBarsAvailable,
    int FormalRows,
    string Message);

public sealed record SignalReplayResult(string RunId, string Status, IReadOnlyList<SignalEvent> Signals, string RunDirectory, string? ErrorMessage);
