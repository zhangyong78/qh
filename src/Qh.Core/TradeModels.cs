using System.Text.Json;

namespace Qh.Core;

public enum TradingFeeMode
{
    Fixed,
    Rate
}

public sealed record TradingConfig(
    decimal InitialEquity = 10000m,
    int Lots = 1,
    decimal Multiplier = 10m,
    decimal MinTick = 1m,
    decimal MarginRate = 0.10m,
    TradingFeeMode FeeMode = TradingFeeMode.Fixed,
    decimal OpenFee = 2m,
    decimal TodayCloseFee = 3m,
    decimal OrdinaryCloseFee = 1m,
    decimal FeeRate = 0m,
    int SlippageTicks = 0,
    decimal? StopLossPrice = null,
    decimal? TakeProfitPrice = null)
{
    public void Validate()
    {
        if (InitialEquity < 0 || Lots < 1 || Multiplier <= 0 || MinTick <= 0 || MarginRate < 0 || MarginRate > 1)
            throw new ArgumentException("交易参数必须为合法的非负值，保证金率需在[0,1]");
        if (SlippageTicks < 0 || OpenFee < 0 || TodayCloseFee < 0 || OrdinaryCloseFee < 0 || FeeRate < 0)
            throw new ArgumentException("手续费与滑点不能为负数");
        if (FeeMode == TradingFeeMode.Rate && FeeRate <= 0) throw new ArgumentException("比例手续费模式需要 FeeRate > 0");
    }
}

public sealed record TradeReplayRequest(
    string SnapshotId,
    string StrategyPath,
    DateTimeOffset StartInclusive,
    DateTimeOffset EndExclusive,
    TradingConfig Config,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null,
    bool Explore = false,
    double StrategyTimeoutSeconds = 2.0,
    bool MainContinuousResearch = false);

public sealed record TradeIntent(
    string RunId,
    string SnapshotId,
    DateTimeOffset SignalBarEnd,
    DateTimeOffset SignalBarStart,
    DateTimeOffset? ExecutionBarEnd,
    string Symbol,
    string Action,
    string Direction,
    int Quantity,
    string Description,
    decimal? ReferencePrice,
    bool IsFormal,
    int Sequence,
    string RawJson);

public sealed record OrderRecord(
    string RunId,
    int IntentSequence,
    string Action,
    string Direction,
    int Quantity,
    DateTimeOffset SignalBarEnd,
    DateTimeOffset? ExecutionBarEnd,
    string Status,
    string? FailureReason);

public sealed record FillRecord(
    string RunId,
    int IntentSequence,
    int FillSequence,
    string Action,
    string Direction,
    int Quantity,
    DateTimeOffset BarEnd,
    DateOnly TradingDay,
    decimal Price,
    decimal Notional,
    decimal Fee,
    string FeeType,
    decimal Slippage,
    string PositionEffect);

public sealed record PositionRecord(
    string RunId,
    DateTimeOffset BarEnd,
    string Direction,
    int Quantity,
    decimal AverageEntryPrice,
    DateOnly EntryTradingDay,
    decimal FloatingPnl,
    decimal ReservedMargin);

public sealed record ClosedTradeRecord(
    string RunId,
    int EntryFillSequence,
    int ExitFillSequence,
    string Direction,
    int Quantity,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal GrossPnl,
    decimal Fees,
    decimal NetPnl,
    DateOnly EntryTradingDay,
    DateOnly ExitTradingDay);

public sealed record EquityPoint(
    string RunId,
    DateTimeOffset BarEnd,
    decimal Equity,
    decimal RealizedGrossPnl,
    decimal Fees,
    decimal FloatingPnl,
    decimal Drawdown);

public sealed record RiskEvent(string RunId, DateTimeOffset BarEnd, string Kind, string Message, int? IntentSequence);

public sealed class TradeRunRecord
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
    public TradingConfig Config { get; init; } = new();
    public DateTimeOffset StartInclusive { get; init; }
    public DateTimeOffset EndExclusive { get; init; }
    public bool Explore { get; init; }
    public bool MainContinuousResearch { get; init; }
    public string BacktestModeDisplayName => MainContinuousResearch ? "主连研究回测（模拟盈亏）" : "具体合约回测";
    public string? ResearchNotice => MainContinuousResearch
        ? "收益按主连连续价格模拟；未模拟真实合约换仓及换仓费用，换月价差会影响指标、止损和盈亏。"
        : null;
    public bool DataIncomplete { get; set; }
    public int InputRows { get; set; }
    public int ProcessedRows { get; set; }
    public int FormalRows { get; set; }
    public int WarmupBarsRequired { get; set; }
    public int WarmupBarsAvailable { get; set; }
    public int FormalIntentCount { get; set; }
    public int FillCount { get; set; }
    public int ClosedTradeCount { get; set; }
    public decimal TotalGrossPnl { get; set; }
    public decimal TotalFees { get; set; }
    public decimal NetPnl { get; set; }
    public decimal FinalEquity { get; set; }
    public decimal ReturnRate { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal? WinRate { get; set; }
    public string? CoverageMessage { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string IndicatorFormulaVersion { get; init; } = "qh-g3-frozen-v1";
    public string SoftwareVersion { get; init; } = "qh-g3";
}

public sealed record TradeReplayProgress(string RunId, int ProcessedRows, int TotalRows, string Stage);

public sealed record TradeReplayResult(
    string RunId,
    string Status,
    IReadOnlyList<TradeIntent> Intents,
    IReadOnlyList<FillRecord> Fills,
    IReadOnlyList<ClosedTradeRecord> ClosedTrades,
    string RunDirectory,
    string? ErrorMessage);
