using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qh.Core;

public sealed class TradeReplayRunner
{
    private static readonly JsonSerializerOptions ProtocolJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan ProtocolTimeout = TimeSpan.FromSeconds(3);
    private readonly DataStore _dataStore;
    private readonly TradeRunStore _runStore;
    private readonly string _workerPath;

    public TradeReplayRunner(DataStore dataStore, string workerPath)
    {
        _dataStore = dataStore;
        _runStore = new TradeRunStore(dataStore.RootDirectory);
        _workerPath = Path.GetFullPath(workerPath);
    }

    public async Task<TradeReplayResult> ReplayAsync(TradeReplayRequest request, IProgress<TradeReplayProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.EndExclusive <= request.StartInclusive) throw new ArgumentException("正式区间必须满足 startInclusive < endExclusive");
        request.Config.Validate();
        var descriptor = await StrategyCatalog.DescribeAsync(request.StrategyPath, _workerPath, cancellationToken);
        if (!string.Equals(descriptor.Kind, "trade", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("交易回放只接受 kind=trade 策略");
        var parameters = SignalReplayRunner.ValidateParameters(descriptor, request.Parameters);
        var runDirectory = _runStore.CreateRunDirectory();
        var runId = Path.GetFileName(runDirectory);
        var strategySnapshot = Path.Combine(runDirectory, "strategy.py");
        File.Copy(descriptor.StrategyPath, strategySnapshot, false);
        var quality = _dataStore.ReadQualityReport(request.SnapshotId);
        var bars = _dataStore.QuerySnapshot(request.SnapshotId, cancellationToken).Where(bar => bar.Timestamp < request.EndExclusive).ToArray();
        var coverage = SignalReplayRunner.EvaluateCoverage(descriptor, parameters, bars, request.StartInclusive, request.EndExclusive);
        var contractIssue = ValidateTradeSeries(bars, request.MainContinuousResearch);
        var formalRows = coverage.FormalRows;
        var record = new TradeRunRecord
        {
            RunId = runId, SnapshotId = request.SnapshotId, StrategyName = descriptor.Name, StrategyVersion = descriptor.Version,
            StrategySha256 = descriptor.StrategySha256, StrategySnapshotPath = strategySnapshot, StrategyMetadataJson = descriptor.MetadataJson,
            Parameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal), Config = request.Config,
            StartInclusive = request.StartInclusive, EndExclusive = request.EndExclusive, Explore = request.Explore,
            MainContinuousResearch = request.MainContinuousResearch,
            DataIncomplete = quality.SuspectedGaps.Count > 0 || quality.IncompleteRows > 0, InputRows = bars.Length, FormalRows = formalRows,
            WarmupBarsRequired = coverage.WarmupBarsRequired, WarmupBarsAvailable = coverage.WarmupBarsAvailable,
            CoverageMessage = coverage.Message, CreatedAt = DateTimeOffset.UtcNow
        };
        _runStore.WriteRecord(runDirectory, record);
        File.WriteAllText(Path.Combine(runDirectory, "quality.json"), JsonSerializer.Serialize(quality, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        WriteAssumptions(runDirectory, bars, record);
        if (!request.Explore && (quality.SuspectedGaps.Count > 0 || !quality.CanPublish))
            return await FinishBlockedAsync(record, runDirectory, "blocked_quality", "quality", $"严格模式拒绝质量不完整数据：gaps={quality.SuspectedGaps.Count}, issues={quality.Issues.Count}, incomplete={quality.IncompleteRows}");
        if (contractIssue is not null) return await FinishBlockedAsync(record, runDirectory, "blocked_contract", "contract", contractIssue);
        if (!coverage.IsReady) return await FinishBlockedAsync(record, runDirectory, "blocked_coverage", "coverage", record.CoverageMessage!);

        var pending = new List<PendingIntent>();
        var intents = new List<TradeIntent>();
        var fills = new List<FillRecord>();
        var closedTrades = new List<ClosedTradeRecord>();
        var signals = new List<SignalEvent>();
        var state = new AccountState(request.Config, runId, _runStore, runDirectory, fills, closedTrades);
        try
        {
            record.Status = "running"; _runStore.WriteRecord(runDirectory, record);
            progress?.Report(new TradeReplayProgress(runId, 0, bars.Length, "启动交易策略 Worker"));
            var sessionRanges = ResolveSessionRanges(bars, request.StartInclusive, request.EndExclusive);
            await RunWorkerAsync(runId, runDirectory, strategySnapshot, parameters, bars, sessionRanges, request, record, pending, intents, signals, state, progress, cancellationToken);
            foreach (var orphan in pending)
            {
                _runStore.Append(runDirectory, "orders.jsonl", new OrderRecord(runId, orphan.Intent.Sequence, orphan.Intent.Action, orphan.Intent.Direction, orphan.Intent.Quantity,
                    orphan.Intent.SignalBarEnd, null, "unfilled", "no_next_valid_bar"));
                _runStore.Append(runDirectory, "unfilled.jsonl", new { runId, intentSequence = orphan.Intent.Sequence, reason = "no_next_valid_bar", signalBarEnd = orphan.Intent.SignalBarEnd });
            }
            state.WriteFinalEquity();
            UpdateSummary(record, request.Config, state, intents, fills, closedTrades);
            record.Status = "completed"; record.FinishedAt = DateTimeOffset.UtcNow; _runStore.WriteRecord(runDirectory, record); _runStore.CloseRun(runDirectory);
            progress?.Report(new TradeReplayProgress(runId, record.ProcessedRows, bars.Length, "完成"));
            return new TradeReplayResult(runId, record.Status, intents, fills, closedTrades, runDirectory, null);
        }
        catch (OperationCanceledException)
        {
            MarkPendingNotAttempted(runDirectory, pending, "cancelled_not_attempted");
            state.WriteFinalEquity(); UpdateSummary(record, request.Config, state, intents, fills, closedTrades);
            record.Status = "cancelled"; record.ErrorKind = "cancelled"; record.ErrorMessage = "交易回放已取消"; record.FinishedAt = DateTimeOffset.UtcNow; _runStore.WriteRecord(runDirectory, record); _runStore.CloseRun(runDirectory);
            return new TradeReplayResult(runId, record.Status, intents, fills, closedTrades, runDirectory, record.ErrorMessage);
        }
        catch (ReplayWorkerException ex)
        {
            MarkPendingNotAttempted(runDirectory, pending, "failed_not_attempted");
            state.WriteFinalEquity(); UpdateSummary(record, request.Config, state, intents, fills, closedTrades);
            record.Status = "failed"; record.ErrorKind = ex.Kind; record.ErrorMessage = ex.Message; record.FinishedAt = DateTimeOffset.UtcNow; _runStore.WriteRecord(runDirectory, record); _runStore.CloseRun(runDirectory);
            return new TradeReplayResult(runId, record.Status, intents, fills, closedTrades, runDirectory, ex.Message);
        }
        catch (Exception ex)
        {
            MarkPendingNotAttempted(runDirectory, pending, "failed_not_attempted");
            state.WriteFinalEquity(); UpdateSummary(record, request.Config, state, intents, fills, closedTrades);
            record.Status = "failed"; record.ErrorKind = ex.GetType().Name; record.ErrorMessage = ex.Message; record.FinishedAt = DateTimeOffset.UtcNow; _runStore.WriteRecord(runDirectory, record); _runStore.CloseRun(runDirectory);
            return new TradeReplayResult(runId, record.Status, intents, fills, closedTrades, runDirectory, ex.Message);
        }
    }

    private static void UpdateSummary(TradeRunRecord record, TradingConfig config, AccountState state, IReadOnlyList<TradeIntent> intents, IReadOnlyList<FillRecord> fills, IReadOnlyList<ClosedTradeRecord> closedTrades)
    {
        record.FormalIntentCount = intents.Count; record.FillCount = fills.Count; record.ClosedTradeCount = closedTrades.Count;
        record.TotalGrossPnl = state.RealizedGross; record.TotalFees = state.TotalFees; record.FinalEquity = state.FinalEquity;
        record.NetPnl = state.FinalEquity - config.InitialEquity; record.ReturnRate = config.InitialEquity == 0 ? 0 : record.NetPnl / config.InitialEquity * 100m;
        record.WinRate = closedTrades.Count == 0 ? null : closedTrades.Count(trade => trade.NetPnl > 0) * 100m / closedTrades.Count;
        record.MaxDrawdown = state.MaxDrawdown;
    }

    private void MarkPendingNotAttempted(string directory, IEnumerable<PendingIntent> pending, string reason)
    {
        foreach (var item in pending)
        {
            _runStore.Append(directory, "orders.jsonl", new OrderRecord(item.Intent.RunId, item.Intent.Sequence, item.Intent.Action, item.Intent.Direction, item.Intent.Quantity, item.Intent.SignalBarEnd, null, "unfilled", reason));
            _runStore.Append(directory, "unfilled.jsonl", new { runId = item.Intent.RunId, intentSequence = item.Intent.Sequence, reason, signalBarEnd = item.Intent.SignalBarEnd });
        }
    }

    private async Task RunWorkerAsync(string runId, string runDirectory, string strategyPath, IReadOnlyDictionary<string, JsonElement> parameters,
        IReadOnlyList<Bar> bars, IReadOnlyDictionary<DateOnly, (DateTimeOffset Start, DateTimeOffset End)[]> sessionRanges,
        TradeReplayRequest request, TradeRunRecord record, List<PendingIntent> pending, List<TradeIntent> intents,
        List<SignalEvent> signals, AccountState state, IProgress<TradeReplayProgress>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = PythonRuntime.ResolveExecutable(), UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.Environment["PYTHONIOENCODING"] = "utf-8"; start.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; start.ArgumentList.Add(_workerPath); start.ArgumentList.Add("--strategy"); start.ArgumentList.Add(strategyPath);
        start.ArgumentList.Add("--timeout"); start.ArgumentList.Add(request.StrategyTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new ReplayWorkerException("worker_start", "无法启动交易策略 Worker");
        var lineReader = new BoundedStrategyLineReader(process.StandardOutput);
        var stderrPath = Path.Combine(runDirectory, "worker-stderr.log");
        var outputBudget = new StrategyProcessOutputBudget();
        var stderrTask = StrategyProcessOutput.DrainStandardErrorAsync(process.StandardError, stderrPath, outputBudget, process, "worker_stderr");
        await using var log = new SegmentedStrategyLog(Path.Combine(runDirectory, "worker.log"), "worker_log", "worker_log", StrategyProcessOutputLimits.MaxWorkerLogBytes, outputBudget, diagnosticOnly: true);
        var replayTimer = Stopwatch.StartNew();
        double protocolRoundTripMs = 0, accountingMs = 0, strategyMs = 0, strategyBodyMs = 0, strategyWaitOverheadMs = 0;
        var protocolBudget = new ProtocolBudget();
        var duplicateBars = 0; var producedIntents = 0; var maxPending = 0;
        await log.AppendLineAsync($"worker_pid={process.Id}; parent_pid={Environment.ProcessId}", cancellationToken);
        var nextIntentSequence = 1;
        try
        {
            await SendAsync(process, new { command = "start", parameters }, cancellationToken, null, outputBudget, "initialize", stderrPath); await ExpectAsync(process, lineReader, log, stderrPath, "started", cancellationToken, outputBudget, "initialize");
            for (var index = 0; index < bars.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var bar = bars[index];
                if (!bar.IsComplete) throw new ReplayWorkerException("incomplete_bar", $"策略输入包含未完成 K 线：{bar.Timestamp:O}");
                var barEnd = GetBarEnd(bar, sessionRanges).ToOffset(bar.Timestamp.Offset); var formal = bar.Timestamp >= request.StartInclusive && bar.Timestamp < request.EndExclusive;
                var accountingStarted = Stopwatch.GetTimestamp();
                while (pending.Count > 0)
                {
                    var scheduled = pending[0]; pending.RemoveAt(0);
                    ExecuteIntent(scheduled.Intent, bar, barEnd, state, request.Config, runDirectory);
                }
                // Only a stop submitted on an earlier completed bar may use this
                // bar's OHLC. A newly tightened stop cannot act retrospectively.
                var protectiveExit = state.CreateTriggeredProtection(bar, barEnd, nextIntentSequence);
                if (protectiveExit is not null)
                {
                    nextIntentSequence++;
                    intents.Add(protectiveExit); _runStore.Append(runDirectory, "intents.jsonl", protectiveExit);
                    record.FormalIntentCount++;
                    ExecuteIntent(protectiveExit, bar, barEnd, state, request.Config, runDirectory);
                }
                accountingMs += ElapsedMilliseconds(accountingStarted);
                maxPending = Math.Max(maxPending, pending.Count);
                var hasEffectiveExitIntentThisBar = false;
                var protocolStarted = Stopwatch.GetTimestamp();
                var barBudget = TimeSpan.FromSeconds(request.StrategyTimeoutSeconds + 0.25);
                var barDeadlineTimestamp = Stopwatch.GetTimestamp() + (long)(barBudget.TotalSeconds * Stopwatch.Frequency);
                using var barDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                barDeadline.CancelAfter(barBudget);
                var barContext = $"bar_start={bar.Timestamp:O}; bar_end={barEnd:O}; strategy={strategyPath}; phase=bar_request";
                try
                {
                    await SendAsync(process, new { command = "bar", bar = ToWireBar(bar), bar_end = barEnd.ToString("O", CultureInfo.InvariantCulture), formal, simulation = state.ReadOnlyContext(bar, barEnd) }, barDeadline.Token, protocolBudget, outputBudget, "bar_request", stderrPath, barContext);
                    var completed = false;
                    var duplicate = false;
                    var lastStrategyElapsedMs = 0.0;
                    while (!completed)
                    {
                        var remaining = RemainingDeadline(barDeadlineTimestamp);
                        using var response = await ReadResponseAsync(process, lineReader, log, stderrPath, barDeadline.Token, remaining, barContext, protocolBudget, outputBudget, "bar_request");
                        var type = response.RootElement.GetProperty("type").GetString();
                        if (type == "signal")
                        {
                            var signal = ParseSignal(runId, request.SnapshotId, bar, response, signals.Count + 1);
                            if (signal.IsFormal) { signals.Add(signal); _runStore.Append(runDirectory, "signals.jsonl", signal); }
                        }
                        else if (type == "intent")
                        {
                            var intent = ParseIntent(runId, request.SnapshotId, bar, response, nextIntentSequence++);
                            if (intent.IsFormal)
                            {
                                intents.Add(intent); _runStore.Append(runDirectory, "intents.jsonl", intent);
                                pending.Add(new PendingIntent(intent));
                                record.FormalIntentCount++;
                                hasEffectiveExitIntentThisBar |= state.IsEffectiveExitIntent(intent, request.Config);
                            }
                        }
                        else if (type == "bar_done")
                        {
                            completed = true;
                            duplicate = response.RootElement.TryGetProperty("duplicate", out var duplicateElement) && duplicateElement.GetBoolean();
                            if (response.RootElement.TryGetProperty("strategy_elapsed_ms", out var strategyElapsed)) lastStrategyElapsedMs = strategyElapsed.GetDouble();
                        }
                        else if (type == "error") throw WorkerError(response.RootElement, stderrPath, bar.Timestamp, barEnd);
                        else if (type is "stopped" or "cancelled") throw new ReplayWorkerException("worker_exit", $"Worker 提前结束：{type}");
                    }
                    protocolRoundTripMs += ElapsedMilliseconds(protocolStarted);
                    strategyMs += lastStrategyElapsedMs;
                    if (duplicate) { duplicateBars++; continue; }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && barDeadline.IsCancellationRequested)
                {
                    throw new ReplayWorkerException("strategy_timeout", $"策略整根 deadline 超时；{barContext}; budget={barBudget.TotalSeconds:0.##}s", stderrPath);
                }
                accountingStarted = Stopwatch.GetTimestamp();
                state.OnCompletedBar(bar, barEnd, request.Config, record, runDirectory);
                if (!hasEffectiveExitIntentThisBar && state.ShouldScheduleThresholdClose(bar, request.Config))
                {
                    var threshold = state.CreateThresholdIntent(runId, request.SnapshotId, bar, barEnd, nextIntentSequence++);
                    intents.Add(threshold); _runStore.Append(runDirectory, "intents.jsonl", threshold); pending.Add(new PendingIntent(threshold)); record.FormalIntentCount++;
                }
                accountingMs += ElapsedMilliseconds(accountingStarted);
                producedIntents = intents.Count;
                maxPending = Math.Max(maxPending, pending.Count);
                record.ProcessedRows++;
                if (record.ProcessedRows % 1000 == 0 || record.ProcessedRows == bars.Count) _runStore.WriteRecord(runDirectory, record);
                if (record.ProcessedRows % 100 == 0 || record.ProcessedRows == bars.Count) progress?.Report(new TradeReplayProgress(runId, record.ProcessedRows, bars.Count, formal ? "正式交易回放" : "预热"));
            }
            await SendAsync(process, new { command = "finish" }, cancellationToken, null, outputBudget, "finish", stderrPath);
            using (var completedResponse = await ReadResponseAsync(process, lineReader, log, stderrPath, cancellationToken, null, null, null, outputBudget, "finish"))
            {
                var completedType = completedResponse.RootElement.GetProperty("type").GetString();
                if (completedType == "error") throw WorkerError(completedResponse.RootElement, stderrPath, null, null);
                if (!string.Equals(completedType, "completed", StringComparison.Ordinal)) throw new ReplayWorkerException("worker_exit", $"Worker 完成阶段返回：{completedType}");
                if (completedResponse.RootElement.TryGetProperty("strategy_execution_ms", out var completedStrategy)) strategyMs = completedStrategy.GetDouble();
                if (completedResponse.RootElement.TryGetProperty("strategy_body_ms", out var completedBody)) strategyBodyMs = completedBody.GetDouble();
                if (completedResponse.RootElement.TryGetProperty("strategy_wait_overhead_ms", out var completedWait)) strategyWaitOverheadMs = completedWait.GetDouble();
            }
            await process.WaitForExitAsync().WaitAsync(ProtocolTimeout, cancellationToken);
            EnsureOutputBudget(outputBudget, "finish", stderrPath);
            if (process.ExitCode != 0) throw new ReplayWorkerException("worker_exit", $"Worker 退出码：{process.ExitCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendAsync(process, new { command = "cancel" }, CancellationToken.None, null, outputBudget, "cancel", stderrPath);
                using var cancelWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await ExpectAsync(process, lineReader, log, stderrPath, "cancelled", cancelWait.Token, outputBudget, "cancel");
            }
            catch { StrategyCatalog.TryKill(process); }
            throw;
        }
        finally
        {
            if (!process.HasExited) { StrategyCatalog.TryKill(process); try { process.WaitForExit(500); } catch { } }
            try { await stderrTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
            try { process.WaitForExit(0); } catch { }
            try { await log.AppendLineAsync($"exit={(process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "killed")}; stderr_tail={ReadStderrTail(stderrPath)}", CancellationToken.None); } catch (StrategyOutputLimitException) { }
            replayTimer.Stop();
            File.WriteAllText(Path.Combine(runDirectory, "replay-metrics.json"), JsonSerializer.Serialize(new
            {
                process_id = process.Id,
                parent_process_id = Environment.ProcessId,
                elapsed_ms = replayTimer.ElapsedMilliseconds,
                protocol_round_trip_ms = protocolRoundTripMs,
                strategy_execution_ms = strategyMs,
                strategy_body_ms = strategyBodyMs > 0 ? strategyBodyMs : (double?)null,
                strategy_wait_overhead_ms = strategyWaitOverheadMs > 0 ? strategyWaitOverheadMs : (double?)null,
                protocol_budget_enabled = protocolBudget.Enabled,
                protocol_serialize_ms = protocolBudget.SerializeMs,
                protocol_write_flush_ms = protocolBudget.WriteFlushMs,
                protocol_read_wait_ms = protocolBudget.ReadWaitMs,
                protocol_parse_ms = protocolBudget.ParseMs,
                protocol_log_ms = protocolBudget.LogMs,
                strategy_timing_note = "strategy_execution_ms is synchronous on_bar wall time; the parent enforces each strategy deadline while waiting for the response and kills the isolated worker on timeout",
                accounting_and_journal_ms = accountingMs,
                processed_rows = record.ProcessedRows,
                produced_intents = producedIntents,
                max_pending = maxPending,
                duplicate_bars = duplicateBars,
                exit_code = process.HasExited ? process.ExitCode : -1
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
    }

    private void ExecuteIntent(TradeIntent intent, Bar bar, DateTimeOffset barEnd, AccountState state, TradingConfig config, string directory)
    {
        var order = new OrderRecord(intent.RunId, intent.Sequence, intent.Action, intent.Direction, intent.Quantity, intent.SignalBarEnd, barEnd, "received", null);
        _runStore.Append(directory, "orders.jsonl", order);
        var result = intent.Action switch
        {
            "open" => state.Open(intent, bar, barEnd, config, directory),
            "open_limit" => state.OpenLimit(intent, bar, barEnd, config, directory),
            "close" => state.Close(intent, bar, barEnd, config, directory),
            "protect" => state.ArmProtection(intent, barEnd),
            "close_stop" => state.CloseStop(intent, bar, barEnd, config, directory),
            "reverse" => state.Reverse(intent, bar, barEnd, config, directory),
            _ => "unsupported_action"
        };
        var accepted = result is "filled" or "working";
        _runStore.Append(directory, "orders.jsonl", order with { Status = accepted ? result : "unfilled", FailureReason = accepted ? null : result });
        if (!accepted) _runStore.Append(directory, "unfilled.jsonl", new { runId = intent.RunId, intentSequence = intent.Sequence, reason = result, executionBarEnd = barEnd });
    }

    private async Task<TradeReplayResult> FinishBlockedAsync(TradeRunRecord record, string directory, string status, string kind, string message)
    {
        record.Status = status; record.ErrorKind = kind; record.ErrorMessage = message; record.FinishedAt = DateTimeOffset.UtcNow; _runStore.WriteRecord(directory, record);
        return new TradeReplayResult(record.RunId, status, [], [], [], directory, message);
    }

    public static bool IsMainContinuousSymbol(string symbol) =>
        Regex.IsMatch(symbol.Trim(), @"^KQ\.m@[A-Z]+\.[A-Z]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? ValidateTradeSeries(IReadOnlyList<Bar> bars, bool mainContinuousResearch)
    {
        if (bars.Count == 0) return null;
        var symbol = bars[0].Symbol.Trim();
        var exchange = bars[0].Exchange.Trim();
        var identity = $"{symbol}/{exchange}";
        if (mainContinuousResearch)
            return IsMainContinuousSymbol(symbol) ? null : $"主连研究模式只接受 KQ.m@ 格式的主连行情，不能使用具体合约或指数：{identity}";
        var lowered = identity.ToLowerInvariant();
        var blocked = new[] { "主连", "连续", "指数", "index", "continuous" };
        var kqContinuous = lowered.StartsWith("kq.m@", StringComparison.Ordinal) || lowered.StartsWith("kq.i@", StringComparison.Ordinal);
        var conventionalContinuous = symbol.Split(['.', '_', '-', '@'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token is "888" or "999");
        if (kqContinuous || conventionalContinuous || blocked.Any(marker => lowered.Contains(marker, StringComparison.Ordinal)))
            return $"交易回放只接受单个具体合约；已识别为主连/指数研究序列：{identity}";
        return null;
    }

    private static void WriteAssumptions(string directory, IReadOnlyList<Bar> bars, TradeRunRecord record)
    {
        var symbol = bars.FirstOrDefault()?.Symbol ?? "";
        var exchange = bars.FirstOrDefault()?.Exchange ?? "";
        var explicitSynthetic = new[] { "test", "synth", "synthetic", "模拟", "虚构" }.Any(marker =>
            (symbol + "/" + exchange).Contains(marker, StringComparison.OrdinalIgnoreCase));
        var assumptions = new
        {
            backtest_mode = record.BacktestModeDisplayName,
            main_continuous_research = record.MainContinuousResearch,
            contract_scope = record.MainContinuousResearch
                ? "continuous main series used for research simulation only; no actual contract rollover execution"
                : "single concrete contract only; continuous/main/index series are rejected for trade replay",
            series = new { symbol, exchange, explicitly_marked_synthetic_acceptance = explicitSynthetic },
            execution = "market intent from completed bar executes next valid bar open; protect intent becomes a persistent stop next valid bar: long min(open,stop), short max(open,stop), plus adverse slippage; updates never act on their signal bar; no queue-book model; no end-of-run forced close",
            accounting = "fixed or rate fee from TradingConfig; margin is a risk check only; no forced liquidation or dynamic settlement",
            research_limit = record.ResearchNotice ?? "unknown contract metadata is not guessed; user-supplied synthetic acceptance series must be explicitly marked"
        };
        File.WriteAllText(Path.Combine(directory, "assumptions.json"), JsonSerializer.Serialize(assumptions, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static object ToWireBar(Bar bar) => new { timestamp = bar.Timestamp.ToString("O", CultureInfo.InvariantCulture), symbol = bar.Symbol, exchange = bar.Exchange, period = bar.Period,
        trading_day = bar.TradingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), open = bar.Open, high = bar.High, low = bar.Low, close = bar.Close, volume = bar.Volume, open_interest = bar.OpenInterest, is_complete = bar.IsComplete };

    private static TradeIntent ParseIntent(string runId, string snapshotId, Bar bar, JsonDocument response, int sequence)
    {
        var root = response.RootElement; var intent = root.GetProperty("intent"); var barEnd = DateTimeOffset.Parse(root.GetProperty("bar_end").GetString()!, CultureInfo.InvariantCulture);
        decimal? reference = intent.TryGetProperty("reference_price", out var referenceElement) && referenceElement.ValueKind == JsonValueKind.Number ? referenceElement.GetDecimal() : null;
        var quantity = intent.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetInt32(out var parsed) ? parsed : 1;
        return new TradeIntent(runId, snapshotId, barEnd, bar.Timestamp, null, bar.Symbol, intent.GetProperty("action").GetString()!, intent.GetProperty("direction").GetString()!, quantity,
            intent.TryGetProperty("description", out var description) ? description.GetString() ?? intent.GetProperty("action").GetString()! : intent.GetProperty("action").GetString()!, reference,
            root.GetProperty("formal").GetBoolean(), sequence, intent.GetRawText());
    }

    private static SignalEvent ParseSignal(string runId, string snapshotId, Bar bar, JsonDocument response, int sequence)
    {
        var root = response.RootElement; var signal = root.GetProperty("signal"); decimal? reference = signal.TryGetProperty("reference_price", out var price) && price.ValueKind == JsonValueKind.Number ? price.GetDecimal() : null;
        var barEnd = DateTimeOffset.Parse(root.GetProperty("bar_end").GetString()!, CultureInfo.InvariantCulture);
        return new SignalEvent(runId, snapshotId, barEnd, bar.Timestamp, bar.Symbol, signal.GetProperty("category").GetString()!, signal.GetProperty("direction").GetString()!, signal.GetProperty("description").GetString()!, reference, root.GetProperty("formal").GetBoolean(), sequence, signal.GetRawText());
    }

    private static async Task SendAsync(Process process, object payload, CancellationToken token, ProtocolBudget? budget, StrategyProcessOutputBudget outputBudget, string phase, string stderrPath, string? context = null)
    {
        EnsureOutputBudget(outputBudget, phase, stderrPath, context);
        var serializeStarted = budget?.Enabled == true ? Stopwatch.GetTimestamp() : 0;
        var text = JsonSerializer.Serialize(payload, ProtocolJson);
        if (budget?.Enabled == true) budget.SerializeMs += ElapsedMilliseconds(serializeStarted);
        var writeStarted = budget?.Enabled == true ? Stopwatch.GetTimestamp() : 0;
        try
        {
            await process.StandardInput.WriteLineAsync(text.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        catch (StrategyOutputLimitException exception)
        {
            outputBudget.RegisterLimit(exception); StrategyCatalog.TryKill(process); throw CreateOutputLimitException(exception, phase, stderrPath, context);
        }
        catch
        {
            EnsureOutputBudget(outputBudget, phase, stderrPath, context); throw;
        }
        EnsureOutputBudget(outputBudget, phase, stderrPath, context);
        if (budget?.Enabled == true) budget.WriteFlushMs += ElapsedMilliseconds(writeStarted);
    }
    private static async Task<JsonDocument> ReadResponseAsync(Process process, BoundedStrategyLineReader lineReader, SegmentedStrategyLog log, string stderrPath, CancellationToken token, TimeSpan? responseTimeout, string? timeoutContext, ProtocolBudget? budget, StrategyProcessOutputBudget outputBudget, string phase)
    {
        EnsureOutputBudget(outputBudget, phase, stderrPath, timeoutContext);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); string? line;
        if (responseTimeout <= TimeSpan.Zero) throw new ReplayWorkerException("strategy_timeout", $"策略整根 deadline 已耗尽{(timeoutContext is null ? "" : $"；{timeoutContext}")}", stderrPath);
        deadline.CancelAfter(responseTimeout ?? ProtocolTimeout);
        var readStarted = budget?.Enabled == true ? Stopwatch.GetTimestamp() : 0;
        try { line = await lineReader.ReadLineAsync(phase, deadline.Token); }
        catch (StrategyOutputLimitException exception)
        {
            outputBudget.RegisterLimit(exception); StrategyCatalog.TryKill(process); throw CreateOutputLimitException(exception, phase, stderrPath, timeoutContext);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var kind = responseTimeout is null ? "protocol_timeout" : "strategy_timeout"; var seconds = (responseTimeout ?? ProtocolTimeout).TotalSeconds;
            throw new ReplayWorkerException(kind, $"Worker 响应等待超过 {seconds:0.##}s{(timeoutContext is null ? "" : $"；{timeoutContext}")}; stderr={ReadStderrTail(stderrPath)}", stderrPath);
        }
        if (budget?.Enabled == true) budget.ReadWaitMs += ElapsedMilliseconds(readStarted);
        EnsureOutputBudget(outputBudget, phase, stderrPath, timeoutContext);
        if (line is null) throw new ReplayWorkerException("worker_exit", $"Worker 无输出，退出码：{(process.HasExited ? process.ExitCode : -1)}; stderr={ReadStderrTail(stderrPath)}", stderrPath);
        var logStarted = budget?.Enabled == true ? Stopwatch.GetTimestamp() : 0;
        try { await log.AppendLineAsync(line, token); }
        catch (StrategyOutputLimitException exception)
        {
            outputBudget.RegisterLimit(exception); StrategyCatalog.TryKill(process); throw CreateOutputLimitException(exception, phase, stderrPath, timeoutContext);
        }
        if (budget?.Enabled == true) budget.LogMs += ElapsedMilliseconds(logStarted);
        var parseStarted = budget?.Enabled == true ? Stopwatch.GetTimestamp() : 0;
        var document = JsonDocument.Parse(line);
        if (budget?.Enabled == true) budget.ParseMs += ElapsedMilliseconds(parseStarted);
        return document;
    }
    private static TimeSpan RemainingDeadline(long deadlineTimestamp) { var seconds = (deadlineTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency; return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds); }
    private static async Task ExpectAsync(Process process, BoundedStrategyLineReader lineReader, SegmentedStrategyLog log, string stderrPath, string expected, CancellationToken token, StrategyProcessOutputBudget outputBudget, string phase)
    {
        while (true) { using var response = await ReadResponseAsync(process, lineReader, log, stderrPath, token, null, null, null, outputBudget, phase); var type = response.RootElement.GetProperty("type").GetString(); if (type == expected) return; if (type == "error") throw WorkerError(response.RootElement, stderrPath, null, null); }
    }
    private static ReplayWorkerException WorkerError(JsonElement root, string stderrPath, DateTimeOffset? barStart, DateTimeOffset? barEnd)
    {
        var location = barStart is null ? "" : $"; bar_start={barStart:O}; bar_end={barEnd:O}"; var traceback = root.TryGetProperty("traceback", out var trace) ? trace.GetString() : null;
        return new ReplayWorkerException(root.GetProperty("kind").GetString() ?? "worker", $"{root.GetProperty("message").GetString() ?? "Worker 错误"}{location}; traceback={traceback}; stderr={ReadStderrTail(stderrPath)}");
    }
    private static string ReadStderrTail(string path) => StrategyProcessOutput.ReadTail(path);
    private static void EnsureOutputBudget(StrategyProcessOutputBudget outputBudget, string phase, string stderrPath, string? context = null)
    {
        var exception = outputBudget.LimitException;
        if (exception is not null) throw CreateOutputLimitException(exception, phase, stderrPath, context);
    }
    private static ReplayWorkerException CreateOutputLimitException(StrategyOutputLimitException exception, string phase, string stderrPath, string? context = null) =>
        new("output_limit", $"策略输出超限；{(context is null ? $"phase={phase}" : context)}; {exception.Message}; stderr={ReadStderrTail(stderrPath)}", stderrPath);
    private IReadOnlyDictionary<DateOnly, (DateTimeOffset Start, DateTimeOffset End)[]> ResolveSessionRanges(
        IReadOnlyList<Bar> bars, DateTimeOffset start, DateTimeOffset end)
    {
        if (bars.Count == 0 || !HistoricalCalendarResolver.TryLoad(
                _dataStore.RootDirectory, bars[0].Symbol, start, end,
                out _, out var ranges, out _, out _, out _))
            return new Dictionary<DateOnly, (DateTimeOffset Start, DateTimeOffset End)[]>();

        return ranges
            .Select(range => (
                TradingDay: DateOnly.Parse(range.TradingDay, CultureInfo.InvariantCulture),
                Start: DateTimeOffset.Parse(range.Start, CultureInfo.InvariantCulture),
                End: DateTimeOffset.Parse(range.End, CultureInfo.InvariantCulture)))
            .GroupBy(range => range.TradingDay)
            .ToDictionary(
                group => group.Key,
                group => group.Select(range => (range.Start, range.End)).OrderBy(range => range.Start).ToArray());
    }

    private static DateTimeOffset GetBarEnd(Bar bar, IReadOnlyDictionary<DateOnly, (DateTimeOffset Start, DateTimeOffset End)[]> sessionRanges)
    {
        var nominalEnd = bar.Timestamp + ParsePeriod(bar.Period);
        if (!sessionRanges.TryGetValue(bar.TradingDay, out var ranges)) return nominalEnd;
        foreach (var range in ranges)
        {
            var start = range.Start.ToOffset(bar.Timestamp.Offset);
            var end = range.End.ToOffset(bar.Timestamp.Offset);
            if (bar.Timestamp >= start && bar.Timestamp < end && end < nominalEnd) return end;
        }
        return nominalEnd;
    }
    private static double ElapsedMilliseconds(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    private static TimeSpan ParsePeriod(string period) => period.Length >= 2 && int.TryParse(period[..^1], out var value) && value > 0 ? char.ToLowerInvariant(period[^1]) switch { 'm' => TimeSpan.FromMinutes(value), 'h' => TimeSpan.FromHours(value), 'd' => TimeSpan.FromDays(value), _ => throw new InvalidDataException($"不支持的周期：{period}") } : throw new InvalidDataException($"不支持的周期：{period}");
    private sealed record PendingIntent(TradeIntent Intent);
    private sealed class ProtocolBudget
    {
        public bool Enabled { get; } = string.Equals(Environment.GetEnvironmentVariable("QH_PROTOCOL_BREAKDOWN"), "1", StringComparison.Ordinal);
        public double SerializeMs { get; set; }
        public double WriteFlushMs { get; set; }
        public double ReadWaitMs { get; set; }
        public double ParseMs { get; set; }
        public double LogMs { get; set; }
    }
    private sealed class ReplayWorkerException(string kind, string message, string? stderrPath = null) : Exception(message) { public string Kind { get; } = kind; public string? StderrPath { get; } = stderrPath; }

    private sealed class AccountState
    {
        private readonly TradingConfig _config; private readonly string _runId; private readonly TradeRunStore _store; private readonly string _directory; private readonly List<FillRecord> _fills; private readonly List<ClosedTradeRecord> _closed;
        private Position? _position; private int _fillSequence; private decimal _realizedGross; private decimal _fees; private decimal _floating; private decimal _peak; private decimal _lastEquity;
        private TradeIntent? _protection;
        public decimal RealizedGross => _realizedGross; public decimal TotalFees => _fees; public decimal FinalEquity { get; private set; } public decimal MaxDrawdown { get; private set; }
        public AccountState(TradingConfig config, string runId, TradeRunStore store, string directory, List<FillRecord> fills, List<ClosedTradeRecord> closed) { _config = config; _runId = runId; _store = store; _directory = directory; _fills = fills; _closed = closed; _peak = config.InitialEquity; _lastEquity = config.InitialEquity; }
        public object ReadOnlyContext(Bar bar, DateTimeOffset barEnd) => new
        {
            position = _position is null ? (object?)null : new { direction = _position.Direction, quantity = _position.Quantity, entry_price = _position.EntryPrice, entry_trading_day = _position.EntryTradingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            account = new { equity = Equity(bar.Close), realized_gross = _realizedGross, fees = _fees, floating_pnl = FloatingAtPrice(bar.Close), valuation_bar_end = barEnd.ToString("O", CultureInfo.InvariantCulture), last_processed_bar_end = barEnd.ToString("O", CultureInfo.InvariantCulture) },
            costs = new { fee_mode = _config.FeeMode == TradingFeeMode.Rate ? "rate" : "fixed", multiplier = _config.Multiplier, fee_rate = _config.FeeRate, open_fee = _config.OpenFee, today_close_fee = _config.TodayCloseFee, ordinary_close_fee = _config.OrdinaryCloseFee }
        };
        public bool IsEffectiveExitIntent(TradeIntent intent, TradingConfig config)
        {
            if (_position is null) return false;
            var quantity = intent.Quantity * config.Lots;
            return intent.Action switch
            {
                "close" => string.Equals(_position.Direction, intent.Direction, StringComparison.OrdinalIgnoreCase) && quantity == _position.Quantity,
                "reverse" => !string.Equals(_position.Direction, intent.Direction, StringComparison.OrdinalIgnoreCase) && quantity > 0,
                _ => false
            };
        }
        public string Open(TradeIntent intent, Bar bar, DateTimeOffset barEnd, TradingConfig config, string directory)
        {
            if (_position is not null) return "position_already_open"; var quantity = intent.Quantity * config.Lots; var effectiveIntent = intent with { Quantity = quantity }; var price = FillPrice(bar.Open, "open", intent.Direction, config);
            if (price <= 0) { Risk(barEnd, "invalid_fill_price", $"开仓尝试价 {price} 非法，拒绝成交", intent.Sequence); return "invalid_fill_price"; }
            var fee = Fee(price, quantity, true, config, null, bar.TradingDay); var margin = price * config.Multiplier * quantity * config.MarginRate;
            if (EquityAtPrice(price) - fee < margin) { Risk(barEnd, "margin_exceeded", $"开仓保证金 {margin} 超过可用权益，未模拟强平", intent.Sequence); Risk(barEnd, "insufficient_funds", $"需要保证金 {margin} 加费用 {fee}，可用权益不足", intent.Sequence); return "insufficient_funds"; }
            var fill = AddFill(effectiveIntent, barEnd, bar.TradingDay, price, fee, "open", "opened", config, Math.Abs(price - bar.Open), config.FeeMode == TradingFeeMode.Rate ? "rate" : "fixed_open"); _position = new Position(intent.Direction, quantity, price, bar.TradingDay, fill.FillSequence, margin, fee); return "filled";
        }
        public string OpenLimit(TradeIntent intent, Bar bar, DateTimeOffset barEnd, TradingConfig config, string directory)
        {
            if (_position is not null) return "position_already_open";
            if (intent.Direction is not ("long" or "short")) return "invalid_direction";
            if (intent.ReferencePrice is not { } limitPrice || limitPrice <= 0) return "invalid_limit_price";
            var quantity = intent.Quantity * config.Lots;
            var effectiveIntent = intent with { Quantity = quantity };
            var isLong = intent.Direction == "long";
            var marketableAtOpen = isLong ? bar.Open <= limitPrice : bar.Open >= limitPrice;
            var limitTouched = isLong ? bar.Low <= limitPrice : bar.High >= limitPrice;
            if (!marketableAtOpen && !limitTouched) return "limit_not_touched";
            var price = marketableAtOpen
                ? isLong ? Math.Min(FillPrice(bar.Open, "open", intent.Direction, config), limitPrice)
                         : Math.Max(FillPrice(bar.Open, "open", intent.Direction, config), limitPrice)
                : limitPrice;
            if (price <= 0) { Risk(barEnd, "invalid_fill_price", $"限价单成交价 {price} 非法，拒绝成交", intent.Sequence); return "invalid_fill_price"; }
            var fee = Fee(price, quantity, true, config, null, bar.TradingDay);
            var margin = price * config.Multiplier * quantity * config.MarginRate;
            if (EquityAtPrice(price) - fee < margin) { Risk(barEnd, "margin_exceeded", $"限价开仓保证金 {margin} 超过可用权益，未模拟强平", intent.Sequence); Risk(barEnd, "insufficient_funds", $"限价开仓需要保证金 {margin} 加费用 {fee}，可用权益不足", intent.Sequence); return "insufficient_funds"; }
            var fill = AddFill(effectiveIntent, barEnd, bar.TradingDay, price, fee, "open", "opened", config, Math.Abs(price - (marketableAtOpen ? bar.Open : limitPrice)), config.FeeMode == TradingFeeMode.Rate ? "rate" : "fixed_open");
            _position = new Position(intent.Direction, quantity, price, bar.TradingDay, fill.FillSequence, margin, fee);
            return "filled";
        }
        public string Close(TradeIntent intent, Bar bar, DateTimeOffset barEnd, TradingConfig config, string directory)
            => CloseAt(intent, bar, barEnd, config, bar.Open);
        public string CloseStop(TradeIntent intent, Bar bar, DateTimeOffset barEnd, TradingConfig config, string directory)
            => CloseAt(intent, bar, barEnd, config, intent.ReferencePrice!.Value);
        private string CloseAt(TradeIntent intent, Bar bar, DateTimeOffset barEnd, TradingConfig config, decimal executionBase)
        {
            if (_position is null) return "no_position"; if (!string.Equals(_position.Direction, intent.Direction, StringComparison.OrdinalIgnoreCase)) return "direction_mismatch";
            var position = _position; var requestedQuantity = intent.Quantity * config.Lots; if (requestedQuantity != position.Quantity) return "close_quantity_mismatch_full_position_only";
            var effectiveIntent = intent with { Quantity = position.Quantity }; var price = FillPrice(executionBase, "close", position.Direction, config);
            if (price <= 0) { Risk(barEnd, "invalid_fill_price", $"平仓尝试价 {price} 非法，拒绝成交", intent.Sequence); return "invalid_fill_price"; }
            var fee = Fee(price, position.Quantity, false, config, position.EntryTradingDay, bar.TradingDay); var fill = AddFill(effectiveIntent, barEnd, bar.TradingDay, price, fee, "close", "closed", config, Math.Abs(price - executionBase), config.FeeMode == TradingFeeMode.Rate ? "rate" : (position.EntryTradingDay == bar.TradingDay ? "fixed_today_close" : "fixed_ordinary_close"));
            var gross = position.Direction == "long" ? (price - position.EntryPrice) * config.Multiplier * position.Quantity : (position.EntryPrice - price) * config.Multiplier * position.Quantity;
            var closedFees = position.EntryFee + fee; var closed = new ClosedTradeRecord(_runId, position.EntryFillSequence, fill.FillSequence, position.Direction, position.Quantity, position.EntryPrice, price, gross, closedFees, gross - closedFees, position.EntryTradingDay, bar.TradingDay);
            _closed.Add(closed); _store.Append(_directory, "closed-trades.jsonl", closed); _realizedGross += gross;
            FinishProtection("cancelled", barEnd, "position_closed"); _position = null; return "filled";
        }
        public string ArmProtection(TradeIntent intent, DateTimeOffset barEnd)
        {
            if (_position is null) return "no_position";
            if (intent.Direction != _position.Direction) return "direction_mismatch";
            if (intent.Quantity * _config.Lots != _position.Quantity) return "close_quantity_mismatch_full_position_only";
            if (intent.ReferencePrice is not { } stop || stop <= 0 || stop % _config.MinTick != 0) return "invalid_stop_price";
            if (_protection?.ReferencePrice is { } previous && (_position.Direction == "long" ? stop < previous : stop > previous)) return "stop_would_loosen";
            FinishProtection("replaced", barEnd, "protection_updated"); _protection = intent; return "working";
        }
        public TradeIntent? CreateTriggeredProtection(Bar bar, DateTimeOffset barEnd, int sequence)
        {
            if (_position is null || _protection?.ReferencePrice is not { } stop) return null;
            var isLong = _position.Direction == "long";
            if (isLong ? bar.Low > stop : bar.High < stop) return null;
            var source = _protection;
            var executionBase = isLong ? Math.Min(bar.Open, stop) : Math.Max(bar.Open, stop);
            var gap = executionBase != stop;
            var description = $"{source.Description}；{(gap ? "开盘跳过保护价，按较差开盘价" : "盘中触及保护价，按保护价")} {executionBase:0.####} {(isLong ? "减" : "加")}滑点{_config.SlippageTicks * _config.MinTick:0.####}点成交";
            FinishProtection("triggered", barEnd, null);
            return source with { Action = "close_stop", SignalBarStart = bar.Timestamp, SignalBarEnd = barEnd, Sequence = sequence, ReferencePrice = executionBase, Description = description,
                RawJson = JsonSerializer.Serialize(new { source = "previous_bar_protection", protectionIntentSequence = source.Sequence, protectedStop = stop, gapAtOpen = gap }) };
        }
        private void FinishProtection(string status, DateTimeOffset barEnd, string? reason)
        {
            if (_protection is not { } intent) return;
            _store.Append(_directory, "orders.jsonl", new OrderRecord(_runId, intent.Sequence, intent.Action, intent.Direction, intent.Quantity, intent.SignalBarEnd, barEnd, status, reason));
            _protection = null;
        }
        public string Reverse(TradeIntent intent, Bar bar, DateTimeOffset barEnd, TradingConfig config, string directory)
        {
            if (_position is not null && string.Equals(_position.Direction, intent.Direction, StringComparison.OrdinalIgnoreCase)) return "position_already_open";
            if (_position is not null) { var closeIntent = intent with { Action = "close", Direction = _position.Direction, Quantity = _position.Quantity / config.Lots }; var closeResult = Close(closeIntent, bar, barEnd, config, directory); if (closeResult != "filled") return closeResult; }
            var openIntent = intent with { Action = "open" }; return Open(openIntent, bar, barEnd, config, directory);
        }
        public void OnCompletedBar(Bar bar, DateTimeOffset barEnd, TradingConfig config, TradeRunRecord record, string directory)
        {
            _floating = FloatingAtPrice(bar.Close);
            var equity = Equity(bar.Close); _peak = Math.Max(_peak, equity); var drawdown = _peak - equity; MaxDrawdown = Math.Max(MaxDrawdown, drawdown);
            _lastEquity = equity; var point = new EquityPoint(_runId, barEnd, equity, _realizedGross, _fees, _floating, drawdown); _store.Append(directory, "equity.jsonl", point);
            if (_position is not null) _store.Append(directory, "positions.jsonl", new PositionRecord(_runId, barEnd, _position.Direction, _position.Quantity, _position.EntryPrice, _position.EntryTradingDay, _floating, _position.Margin));
            if (_position is not null && equity < _position.Margin && !_position.MarginRiskRecorded)
            {
                Risk(barEnd, "margin_exceeded", $"完成K线收盘估值 {equity} 低于持仓初始保证金 {_position.Margin}；仅记录风险，不模拟强平", null);
                _position = _position with { MarginRiskRecorded = true };
            }
            var stopReached = config.StopLossPrice is not null && ((_position?.Direction == "long" && bar.Close <= config.StopLossPrice) || (_position?.Direction == "short" && bar.Close >= config.StopLossPrice));
            var takeReached = config.TakeProfitPrice is not null && ((_position?.Direction == "long" && bar.Close >= config.TakeProfitPrice) || (_position?.Direction == "short" && bar.Close <= config.TakeProfitPrice));
            if (_position is not null && (stopReached || takeReached))
                _store.Append(directory, "risk-events.jsonl", new RiskEvent(_runId, barEnd, "threshold_reached", "完成K线收盘达到止损/止盈阈值，等待下一根开盘执行", null));
        }
        public bool ShouldScheduleThresholdClose(Bar bar, TradingConfig config)
        {
            if (_position is null) return false;
            return (config.StopLossPrice is not null && ((_position.Direction == "long" && bar.Close <= config.StopLossPrice) || (_position.Direction == "short" && bar.Close >= config.StopLossPrice))) ||
                (config.TakeProfitPrice is not null && ((_position.Direction == "long" && bar.Close >= config.TakeProfitPrice) || (_position.Direction == "short" && bar.Close <= config.TakeProfitPrice)));
        }
        public TradeIntent CreateThresholdIntent(string runId, string snapshotId, Bar bar, DateTimeOffset barEnd, int sequence) =>
            new(runId, snapshotId, barEnd, bar.Timestamp, null, bar.Symbol, "close", _position!.Direction, _position.Quantity / _config.Lots, "完成K线止损/止盈", bar.Close, true, sequence, "{\"source\":\"threshold\"}");
        public void WriteFinalEquity() { FinalEquity = _config.InitialEquity + _realizedGross - _fees + _floating; }
        private decimal Equity(decimal close) => _config.InitialEquity + _realizedGross - _fees + FloatingAtPrice(close);
        private decimal FloatingAtPrice(decimal price) => _position is null ? 0 : (_position.Direction == "long" ? price - _position.EntryPrice : _position.EntryPrice - price) * _config.Multiplier * _position.Quantity;
        private decimal EquityAtPrice(decimal price) => _config.InitialEquity + _realizedGross - _fees + (_position is null ? 0 : (_position.Direction == "long" ? price - _position.EntryPrice : _position.EntryPrice - price) * _config.Multiplier * _position.Quantity);
        private FillRecord AddFill(TradeIntent intent, DateTimeOffset barEnd, DateOnly tradingDay, decimal price, decimal fee, string action, string effect, TradingConfig config, decimal slippage, string feeType)
        {
            var fill = new FillRecord(_runId, intent.Sequence, ++_fillSequence, action, intent.Direction, intent.Quantity, barEnd, tradingDay, price, price * config.Multiplier * intent.Quantity, fee, feeType, slippage, effect);
            _fills.Add(fill); _store.Append(_directory, "fills.jsonl", fill); _fees += fee; return fill;
        }
        private decimal FillPrice(decimal open, string action, string direction, TradingConfig config) => open + (action == "open" ? (direction == "long" ? 1 : -1) : (direction == "long" ? -1 : 1)) * config.MinTick * config.SlippageTicks;
        private static decimal Fee(decimal price, int quantity, bool opening, TradingConfig config, DateOnly? entryDay, DateOnly closeDay) => config.FeeMode == TradingFeeMode.Rate ? Math.Abs(price * config.Multiplier * quantity) * config.FeeRate : (opening ? config.OpenFee : (entryDay == closeDay ? config.TodayCloseFee : config.OrdinaryCloseFee)) * quantity;
        private void Risk(DateTimeOffset barEnd, string kind, string message, int? sequence) => _store.Append(_directory, "risk-events.jsonl", new RiskEvent(_runId, barEnd, kind, message, sequence));
        private sealed record Position(string Direction, int Quantity, decimal EntryPrice, DateOnly EntryTradingDay, int EntryFillSequence, decimal Margin, decimal EntryFee, bool MarginRiskRecorded = false);
    }
}
