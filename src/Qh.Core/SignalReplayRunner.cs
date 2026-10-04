using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Qh.Core;

public sealed class SignalReplayRunner
{
    private static readonly JsonSerializerOptions ProtocolJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan ProtocolTimeout = TimeSpan.FromSeconds(3);
    private readonly DataStore _dataStore;
    private readonly SignalRunStore _runStore;
    private readonly string _workerPath;

    public SignalReplayRunner(DataStore dataStore, string workerPath)
    {
        _dataStore = dataStore;
        _runStore = new SignalRunStore(dataStore.RootDirectory);
        _workerPath = Path.GetFullPath(workerPath);
    }

    public async Task<SignalReplayResult> ReplayAsync(SignalReplayRequest request, IProgress<SignalReplayProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.EndExclusive <= request.StartInclusive) throw new ArgumentException("正式区间必须满足 startInclusive < endExclusive");
        var descriptor = await StrategyCatalog.DescribeAsync(request.StrategyPath, _workerPath, cancellationToken);
        var parameters = ValidateParameters(descriptor, request.Parameters);
        var runDirectory = _runStore.CreateRunDirectory();
        var runId = Path.GetFileName(runDirectory);
        var strategySnapshot = Path.Combine(runDirectory, "strategy.py");
        File.Copy(descriptor.StrategyPath, strategySnapshot, overwrite: false);
        var quality = _dataStore.ReadQualityReport(request.SnapshotId);
        var bars = _dataStore.QuerySnapshot(request.SnapshotId, cancellationToken)
            .Where(bar => bar.Timestamp < request.EndExclusive)
            .ToArray();
        var coverage = EvaluateCoverage(descriptor, parameters, bars, request.StartInclusive, request.EndExclusive);
        var record = new SignalRunRecord
        {
            RunId = runId,
            Status = "created",
            SnapshotId = request.SnapshotId,
            StrategyName = descriptor.Name,
            StrategyVersion = descriptor.Version,
            StrategySha256 = descriptor.StrategySha256,
            StrategySnapshotPath = strategySnapshot,
            StrategyMetadataJson = descriptor.MetadataJson,
            Parameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            StartInclusive = request.StartInclusive,
            EndExclusive = request.EndExclusive,
            Explore = request.Explore,
            DataIncomplete = quality.SuspectedGaps.Count > 0 || quality.IncompleteRows > 0,
            InputRows = bars.Length,
            WarmupBarsRequired = coverage.WarmupBarsRequired,
            WarmupBarsAvailable = coverage.WarmupBarsAvailable,
            FormalRows = coverage.FormalRows,
            CoverageMessage = coverage.Message,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _runStore.WriteRecord(runDirectory, record);
        File.WriteAllText(Path.Combine(runDirectory, "quality.json"), JsonSerializer.Serialize(quality, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        if (!request.Explore && (quality.SuspectedGaps.Count > 0 || !quality.CanPublish))
        {
            record.Status = "blocked_quality";
            record.ErrorKind = "quality";
            record.ErrorMessage = $"严格模式拒绝质量不完整数据：gaps={quality.SuspectedGaps.Count}, issues={quality.Issues.Count}, incomplete={quality.IncompleteRows}";
            record.FinishedAt = DateTimeOffset.UtcNow;
            _runStore.WriteRecord(runDirectory, record);
            return new SignalReplayResult(runId, record.Status, [], runDirectory, record.ErrorMessage);
        }

        if (!coverage.IsReady)
        {
            record.Status = "blocked_coverage";
            record.ErrorKind = "coverage";
            record.ErrorMessage = coverage.Message;
            record.FinishedAt = DateTimeOffset.UtcNow;
            _runStore.WriteRecord(runDirectory, record);
            return new SignalReplayResult(runId, record.Status, [], runDirectory, record.ErrorMessage);
        }

        try
        {
            record.Status = "running";
            _runStore.WriteRecord(runDirectory, record);
            progress?.Report(new SignalReplayProgress(runId, 0, bars.Length, "启动策略 Worker"));
            var signals = await RunWorkerAsync(runId, runDirectory, strategySnapshot, parameters, bars, request, record, progress, cancellationToken);
            record.Status = "completed";
            record.FinishedAt = DateTimeOffset.UtcNow;
            _runStore.WriteRecord(runDirectory, record);
            progress?.Report(new SignalReplayProgress(runId, record.ProcessedRows, bars.Length, "完成"));
            return new SignalReplayResult(runId, record.Status, signals, runDirectory, null);
        }
        catch (OperationCanceledException)
        {
            record.Status = "cancelled";
            record.ErrorKind = "cancelled";
            record.ErrorMessage = "回放已取消";
            record.FinishedAt = DateTimeOffset.UtcNow;
            _runStore.WriteRecord(runDirectory, record);
            return new SignalReplayResult(runId, record.Status, _runStore.ReadSignals(runId), runDirectory, record.ErrorMessage);
        }
        catch (ReplayWorkerException ex)
        {
            record.Status = "failed";
            record.ErrorKind = ex.Kind;
            record.ErrorMessage = ex.Message + (ex.StderrPath is null ? "" : $"; stderr_final={ReadStderrTail(ex.StderrPath)}");
            record.FinishedAt = DateTimeOffset.UtcNow;
            _runStore.WriteRecord(runDirectory, record);
            return new SignalReplayResult(runId, record.Status, _runStore.ReadSignals(runId), runDirectory, ex.Message);
        }
        catch (Exception ex)
        {
            record.Status = "failed";
            record.ErrorKind = ex.GetType().Name;
            record.ErrorMessage = ex.Message;
            record.FinishedAt = DateTimeOffset.UtcNow;
            _runStore.WriteRecord(runDirectory, record);
            return new SignalReplayResult(runId, record.Status, _runStore.ReadSignals(runId), runDirectory, ex.Message);
        }
    }

    public static IReadOnlyDictionary<string, JsonElement> ValidateParameters(StrategyDescriptor descriptor, IReadOnlyDictionary<string, JsonElement>? supplied)
    {
        supplied ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var unknown = supplied.Keys.Except(descriptor.Parameters.Keys, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw new ArgumentException("未知策略参数：" + string.Join(',', unknown));
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in descriptor.Parameters)
        {
            var value = supplied.TryGetValue(pair.Key, out var specified) ? specified : pair.Value.Default;
            var spec = pair.Value;
            var validType = spec.Type switch
            {
                "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
                "float" => value.ValueKind == JsonValueKind.Number,
                "str" => value.ValueKind == JsonValueKind.String,
                "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!validType) throw new ArgumentException($"参数 {pair.Key} 类型无效，应为 {spec.Type}");
            if (spec.Min is { } min && value.ValueKind == JsonValueKind.Number && value.GetDecimal() < min.GetDecimal()) throw new ArgumentException($"参数 {pair.Key} 小于最小值");
            if (spec.Max is { } max && value.ValueKind == JsonValueKind.Number && value.GetDecimal() > max.GetDecimal()) throw new ArgumentException($"参数 {pair.Key} 大于最大值");
            if (spec.Choices is { } choices && !choices.Any(choice => choice.GetRawText() == value.GetRawText())) throw new ArgumentException($"参数 {pair.Key} 不在允许值内");
            result[pair.Key] = value.Clone();
        }
        return result;
    }

    public static int RequiredWarmupBars(StrategyDescriptor descriptor, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var required = descriptor.WarmupBars;
        foreach (var name in descriptor.WarmupParameterNames)
        {
            if (parameters.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var period))
                required = Math.Max(required, period);
        }
        return required;
    }

    public static SignalCoverage EvaluateCoverage(StrategyDescriptor descriptor, IReadOnlyDictionary<string, JsonElement> parameters, IReadOnlyList<Bar> bars, DateTimeOffset startInclusive, DateTimeOffset endExclusive)
    {
        var required = RequiredWarmupBars(descriptor, parameters);
        var warmupAvailable = bars.Count(bar => bar.IsComplete && bar.Timestamp < startInclusive);
        var formalRows = bars.Count(bar => bar.IsComplete && bar.Timestamp >= startInclusive && bar.Timestamp < endExclusive);
        if (formalRows == 0)
            return new SignalCoverage(false, required, warmupAvailable, formalRows, "正式区间没有已完成 K 线覆盖");
        if (warmupAvailable < required)
            return new SignalCoverage(false, required, warmupAvailable, formalRows, $"预热不足：需要 {required} 根，正式开始前只有 {warmupAvailable} 根已完成 K 线");
        return new SignalCoverage(true, required, warmupAvailable, formalRows, $"覆盖就绪：预热 {warmupAvailable}/{required} 根，正式 {formalRows} 根");
    }

    private async Task<IReadOnlyList<SignalEvent>> RunWorkerAsync(string runId, string runDirectory, string strategyPath, IReadOnlyDictionary<string, JsonElement> parameters, IReadOnlyList<Bar> bars, SignalReplayRequest request, SignalRunRecord record, IProgress<SignalReplayProgress>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = PythonRuntime.ResolveExecutable(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.ArgumentList.Add(_workerPath);
        start.ArgumentList.Add("--strategy");
        start.ArgumentList.Add(strategyPath);
        start.ArgumentList.Add("--timeout");
        start.ArgumentList.Add(request.StrategyTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new ReplayWorkerException("worker_start", "无法启动策略 Worker");
        var lineReader = new BoundedStrategyLineReader(process.StandardOutput);
        var stderrPath = Path.Combine(runDirectory, "worker-stderr.log");
        var outputBudget = new StrategyProcessOutputBudget();
        var stderrTask = StrategyProcessOutput.DrainStandardErrorAsync(process.StandardError, stderrPath, outputBudget, process, "worker_stderr");
        await using var log = new SegmentedStrategyLog(Path.Combine(runDirectory, "worker.log"), "worker_log", "worker_log", StrategyProcessOutputLimits.MaxWorkerLogBytes, outputBudget, diagnosticOnly: true);
        await log.AppendLineAsync($"pid={process.Id}; started={DateTimeOffset.UtcNow:O}", cancellationToken);
        var signals = new List<SignalEvent>();
        try
        {
            await SendAsync(process, new { command = "start", parameters }, cancellationToken, outputBudget, "initialize", stderrPath);
            await ExpectAsync(process, lineReader, log, stderrPath, "started", cancellationToken, outputBudget, "initialize");
            for (var index = 0; index < bars.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bar = bars[index];
                var barEnd = GetBarEnd(bar).ToOffset(bar.Timestamp.Offset);
                var formal = bar.Timestamp >= request.StartInclusive && bar.Timestamp < request.EndExclusive;
                var barBudget = TimeSpan.FromSeconds(request.StrategyTimeoutSeconds + 0.25);
                var barDeadlineTimestamp = Stopwatch.GetTimestamp() + (long)(barBudget.TotalSeconds * Stopwatch.Frequency);
                using var barDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                barDeadline.CancelAfter(barBudget);
                var barContext = $"bar_start={bar.Timestamp:O}; bar_end={barEnd:O}; strategy={strategyPath}; phase=bar_request";
                try
                {
                    await SendAsync(process, new
                    {
                        command = "bar",
                        bar = new { timestamp = bar.Timestamp.ToString("O", CultureInfo.InvariantCulture), symbol = bar.Symbol, exchange = bar.Exchange, period = bar.Period, trading_day = bar.TradingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), open = bar.Open, high = bar.High, low = bar.Low, close = bar.Close, volume = bar.Volume, open_interest = bar.OpenInterest, is_complete = bar.IsComplete },
                        bar_end = barEnd.ToString("O", CultureInfo.InvariantCulture),
                        formal
                    }, barDeadline.Token, outputBudget, "bar_request", stderrPath, barContext);
                    var completed = false;
                    while (!completed)
                    {
                        var remaining = RemainingDeadline(barDeadlineTimestamp);
                        using var response = await ReadResponseAsync(process, lineReader, log, stderrPath, barDeadline.Token, remaining, barContext, outputBudget, "bar_request");
                        var type = response.RootElement.GetProperty("type").GetString();
                        if (type == "signal")
                        {
                            var eventSignal = ParseSignal(runId, request.SnapshotId, bar, response, signals.Count + 1);
                            if (eventSignal.IsFormal)
                            {
                                signals.Add(eventSignal);
                                _runStore.AppendSignal(runDirectory, eventSignal);
                                record.FormalSignalCount++;
                            }
                            else record.WarmupSignalCount++;
                        }
                        else if (type == "intent")
                        {
                            // trade 策略的交易意图不属于 G2 信号结果；由 TradeReplayRunner 消费。
                        }
                        else if (type == "bar_done") completed = true;
                        else if (type == "error") throw WorkerError(response.RootElement, stderrPath, bar.Timestamp, barEnd);
                        else if (type is "stopped" or "cancelled") throw new ReplayWorkerException("worker_exit", $"Worker 提前结束：{type}");
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && barDeadline.IsCancellationRequested)
                {
                    throw new ReplayWorkerException("strategy_timeout", $"策略整根 deadline 超时；{barContext}; budget={barBudget.TotalSeconds:0.##}s", stderrPath);
                }
                record.ProcessedRows++;
                _runStore.WriteRecord(runDirectory, record);
                progress?.Report(new SignalReplayProgress(runId, record.ProcessedRows, bars.Count, formal ? "正式回放" : "预热"));
            }
            await SendAsync(process, new { command = "finish" }, cancellationToken, outputBudget, "finish", stderrPath);
            await ExpectAsync(process, lineReader, log, stderrPath, "completed", cancellationToken, outputBudget, "finish");
            await process.WaitForExitAsync().WaitAsync(ProtocolTimeout, cancellationToken);
            EnsureOutputBudget(outputBudget, "finish", stderrPath);
            if (process.ExitCode != 0) throw new ReplayWorkerException("worker_exit", $"Worker 退出码：{process.ExitCode}; stderr={ReadStderrTail(stderrPath)}", stderrPath);
            return signals;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
            await SendAsync(process, new { command = "cancel" }, CancellationToken.None, outputBudget, "cancel", stderrPath);
                using var cancelWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await ExpectAsync(process, lineReader, log, stderrPath, "cancelled", cancelWait.Token, outputBudget, "cancel");
            }
            catch { StrategyCatalog.TryKill(process); }
            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                StrategyCatalog.TryKill(process);
                try { process.WaitForExit(500); } catch { }
            }
            try { await stderrTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
            try { process.WaitForExit(0); } catch { }
            process.Refresh();
            var exitText = process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "killed_deadline_pid_checked_externally";
            try { await log.AppendLineAsync($"exit={exitText}; stderr_tail={ReadStderrTail(stderrPath)}", CancellationToken.None); } catch (StrategyOutputLimitException) { }
        }
    }

    private static async Task SendAsync(Process process, object payload, CancellationToken cancellationToken, StrategyProcessOutputBudget outputBudget, string phase, string stderrPath, string? context = null)
    {
        EnsureOutputBudget(outputBudget, phase, stderrPath, context);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload, ProtocolJson).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
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
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, BoundedStrategyLineReader lineReader, SegmentedStrategyLog log, string stderrPath, CancellationToken cancellationToken, TimeSpan? responseTimeout, string? timeoutContext, StrategyProcessOutputBudget outputBudget, string phase)
    {
        EnsureOutputBudget(outputBudget, phase, stderrPath, timeoutContext);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (responseTimeout <= TimeSpan.Zero) throw new ReplayWorkerException("strategy_timeout", $"策略整根 deadline 已耗尽{(timeoutContext is null ? "" : $"；{timeoutContext}")}", stderrPath);
        deadline.CancelAfter(responseTimeout ?? ProtocolTimeout);
        string? line;
        try { line = await lineReader.ReadLineAsync(phase, deadline.Token); }
        catch (StrategyOutputLimitException exception)
        {
            outputBudget.RegisterLimit(exception); StrategyCatalog.TryKill(process); throw CreateOutputLimitException(exception, phase, stderrPath, timeoutContext);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var kind = responseTimeout is null ? "protocol_timeout" : "strategy_timeout";
            var seconds = (responseTimeout ?? ProtocolTimeout).TotalSeconds;
            throw new ReplayWorkerException(kind, $"Worker 响应等待超过 {seconds:0.##}s{(timeoutContext is null ? "" : $"；{timeoutContext}")}; stderr={ReadStderrTail(stderrPath)}", stderrPath);
        }
        EnsureOutputBudget(outputBudget, phase, stderrPath, timeoutContext);
        if (line is null) throw new ReplayWorkerException("worker_exit", $"Worker 无输出，退出码：{(process.HasExited ? process.ExitCode : -1)}; stderr={ReadStderrTail(stderrPath)}", stderrPath);
        try { await log.AppendLineAsync(line, cancellationToken); }
        catch (StrategyOutputLimitException exception)
        {
            outputBudget.RegisterLimit(exception); StrategyCatalog.TryKill(process); throw CreateOutputLimitException(exception, phase, stderrPath, timeoutContext);
        }
        return JsonDocument.Parse(line);
    }

    private static TimeSpan RemainingDeadline(long deadlineTimestamp)
    {
        var seconds = (deadlineTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }

    private static async Task ExpectAsync(Process process, BoundedStrategyLineReader lineReader, SegmentedStrategyLog log, string stderrPath, string expected, CancellationToken cancellationToken, StrategyProcessOutputBudget outputBudget, string phase)
    {
        while (true)
        {
            using var response = await ReadResponseAsync(process, lineReader, log, stderrPath, cancellationToken, null, null, outputBudget, phase);
            var type = response.RootElement.GetProperty("type").GetString();
            if (type == expected) return;
            if (type == "error") throw WorkerError(response.RootElement, stderrPath, null, null);
            if (type is "stopped" or "cancelled") throw new ReplayWorkerException("worker_exit", $"Worker 提前结束：{type}", stderrPath);
        }
    }

    private static SignalEvent ParseSignal(string runId, string snapshotId, Bar bar, JsonDocument response, int sequence)
    {
        var root = response.RootElement;
        var signal = root.GetProperty("signal");
        decimal? reference = signal.TryGetProperty("reference_price", out var price) && price.ValueKind == JsonValueKind.Number ? price.GetDecimal() : null;
        var barEnd = DateTimeOffset.Parse(root.GetProperty("bar_end").GetString()!, CultureInfo.InvariantCulture);
        return new SignalEvent(runId, snapshotId, barEnd, bar.Timestamp, bar.Symbol,
            signal.GetProperty("category").GetString()!, signal.GetProperty("direction").GetString()!,
            signal.GetProperty("description").GetString()!, reference, root.GetProperty("formal").GetBoolean(), sequence, signal.GetRawText());
    }

    private static ReplayWorkerException WorkerError(JsonElement root, string stderrPath, DateTimeOffset? barStart, DateTimeOffset? barEnd)
    {
        var location = barStart is null ? "" : $"; bar_start={barStart:O}; bar_end={barEnd:O}";
        var traceback = root.TryGetProperty("traceback", out var tracebackElement) ? tracebackElement.GetString() : null;
        var strategyPath = root.TryGetProperty("strategy_path", out var pathElement) ? pathElement.GetString() : null;
        var details = string.IsNullOrWhiteSpace(traceback) ? "" : $"; strategy={strategyPath}; traceback={traceback}";
        return new ReplayWorkerException(root.GetProperty("kind").GetString() ?? "worker", $"{root.GetProperty("message").GetString() ?? "Worker 错误"}{location}{details}; stderr={ReadStderrTail(stderrPath)}", stderrPath);
    }

    private static string ReadStderrTail(string path) => StrategyProcessOutput.ReadTail(path);

    private static void EnsureOutputBudget(StrategyProcessOutputBudget outputBudget, string phase, string stderrPath, string? context = null)
    {
        var exception = outputBudget.LimitException;
        if (exception is not null) throw CreateOutputLimitException(exception, phase, stderrPath, context);
    }

    private static ReplayWorkerException CreateOutputLimitException(StrategyOutputLimitException exception, string phase, string stderrPath, string? context = null) =>
        new("output_limit", $"策略输出超限；{(context is null ? $"phase={phase}" : context)}; {exception.Message}; stderr={ReadStderrTail(stderrPath)}", stderrPath);

    private static DateTimeOffset GetBarEnd(Bar bar) => bar.Timestamp + ParsePeriod(bar.Period);

    private static TimeSpan ParsePeriod(string period)
    {
        if (period.Length < 2 || !int.TryParse(period[..^1], out var value) || value <= 0) throw new InvalidDataException($"不支持的周期：{period}");
        return char.ToLowerInvariant(period[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => throw new InvalidDataException($"不支持的周期：{period}")
        };
    }

    private sealed class ReplayWorkerException(string kind, string message, string? stderrPath = null) : Exception(message)
    {
        public string Kind { get; } = kind;
        public string? StderrPath { get; } = stderrPath;
    }
}
