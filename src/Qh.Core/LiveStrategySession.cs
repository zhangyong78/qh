using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Qh.Core;

public sealed record LiveStrategyEvaluationResult(
    IReadOnlyList<LiveSignalCandidate> Candidates,
    LiveSignalSourcePhase SourcePhase,
    bool WasDuplicate,
    bool WasWarmup);

public sealed class LiveStrategyException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

/// <summary>
/// One isolated Python strategy process for one live reminder instance.
/// The process receives only coordinator-accepted completed bars.
/// </summary>
public sealed class LiveStrategySession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ProtocolJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    private readonly LiveNotifyStore _store;
    private readonly string _instanceId;
    private readonly string _workerPath;
    private readonly LiveStrategyConfig _config;
    private readonly int _warmupRequired;
    private readonly Dictionary<string, JsonElement> _parameters;
    private readonly List<LiveKline> _history;
    private readonly Process _process;
    private readonly StreamReader _stdout;
    private readonly StreamWriter _stdin;
    private readonly Task<string> _stderrTask;
    private bool _disposed;

    private LiveStrategySession(LiveNotifyStore store, string instanceId, string workerPath, LiveStrategyConfig config, int warmupRequired, Dictionary<string, JsonElement> parameters, List<LiveKline> history, Process process)
    {
        _store = store;
        _instanceId = instanceId;
        _workerPath = workerPath;
        _config = config;
        _warmupRequired = warmupRequired;
        _parameters = parameters;
        _history = history;
        _process = process;
        _stdout = process.StandardOutput;
        _stdin = process.StandardInput;
        _stderrTask = process.StandardError.ReadToEndAsync();
    }

    public LiveStrategyConfig Config => _store.GetStrategyConfig(_instanceId) ?? _config;

    public static async Task<LiveStrategySession> StartAsync(LiveNotifyStore store, string instanceId, string workerPath, CancellationToken cancellationToken)
    {
        var config = store.GetStrategyConfig(instanceId) ?? throw new LiveStrategyException("strategy_not_configured", "提醒实例没有已加载的策略配置");
        var fullWorker = Path.GetFullPath(workerPath);
        if (!File.Exists(fullWorker)) throw new FileNotFoundException("找不到实时策略 Worker", fullWorker);
        try
        {
            var descriptor = await StrategyCatalog.DescribeAsync(config.StrategyPath, fullWorker, cancellationToken);
            if (!string.Equals(descriptor.Version, config.StrategyVersion, StringComparison.Ordinal) || !string.Equals(descriptor.StrategySha256, config.StrategySha256, StringComparison.OrdinalIgnoreCase))
                throw new LiveStrategyException("strategy_changed", "策略文件版本或哈希已变化，请重新加载策略");
            var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.ParametersJson) ?? new(StringComparer.Ordinal);
            var validated = SignalReplayRunner.ValidateParameters(descriptor, parameters).ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal);
            var warmupRequired = SignalReplayRunner.RequiredWarmupBars(descriptor, validated);
            if (warmupRequired != config.WarmupBarsRequired) throw new LiveStrategyException("strategy_config_changed", "策略预热需求与实例配置不一致，请重新配置");

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
            start.ArgumentList.Add(fullWorker);
            start.ArgumentList.Add("--strategy");
            start.ArgumentList.Add(config.StrategyPath);
            start.ArgumentList.Add("--timeout");
            start.ArgumentList.Add("2");
            var process = new Process { StartInfo = start };
            if (!process.Start()) throw new LiveStrategyException("worker_start", "无法启动实时策略 Worker");
            var session = new LiveStrategySession(store, instanceId, fullWorker, config, warmupRequired, validated, store.GetStrategyHistory(instanceId).ToList(), process);
            store.SetStrategyStatus(instanceId, "warming_up");
            try
            {
                await session.SendAsync(new { command = "start", parameters = validated }, cancellationToken);
                await session.ExpectAsync("started", cancellationToken);
                foreach (var historyBar in session._history.ToArray()) await session.SendBarAsync(historyBar, false, cancellationToken, discardSignals: true);
                store.RecordStrategyProgress(instanceId, session._history.Count >= warmupRequired ? "running" : "warming_up", session._history.Count, session._history, session._history.LastOrDefault()?.BarEnd);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            store.SetStrategyStatus(instanceId, "failed", exception.Message);
            throw;
        }
    }

    public async Task<LiveStrategyEvaluationResult> EvaluateAsync(LiveKline bar, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var existing = _store.GetStrategyEvaluation(_instanceId, bar.BarEnd);
        if (existing is not null) return new(existing.Candidates, existing.SourcePhase, true, existing.SourcePhase == LiveSignalSourcePhase.Warmup);
        var sourcePhase = bar.IsRecovery ? LiveSignalSourcePhase.Recovery : _history.Count < _warmupRequired ? LiveSignalSourcePhase.Warmup : LiveSignalSourcePhase.Live;
        IReadOnlyList<LiveSignalCandidate> candidates;
        try
        {
            candidates = await SendBarAsync(bar, sourcePhase == LiveSignalSourcePhase.Live, cancellationToken, discardSignals: sourcePhase == LiveSignalSourcePhase.Warmup);
        }
        catch (Exception exception) when (exception is LiveStrategyException or TimeoutException or InvalidOperationException)
        {
            _store.SetStrategyStatus(_instanceId, "failed", exception.Message);
            throw;
        }
        _store.RecordStrategyEvaluation(_instanceId, bar, sourcePhase, candidates);
        _history.Add(bar);
        var keep = Math.Max(_warmupRequired + 2, 4);
        if (_history.Count > keep) _history.RemoveRange(0, _history.Count - keep);
        _store.RecordStrategyProgress(_instanceId, sourcePhase == LiveSignalSourcePhase.Live ? "running" : sourcePhase == LiveSignalSourcePhase.Recovery ? "recovering" : "warming_up", Math.Min(_history.Count, _warmupRequired), _history, bar.BarEnd);
        return new(candidates, sourcePhase, false, sourcePhase == LiveSignalSourcePhase.Warmup);
    }

    private async Task<IReadOnlyList<LiveSignalCandidate>> SendBarAsync(LiveKline bar, bool formal, CancellationToken cancellationToken, bool discardSignals)
    {
        var candidates = new List<LiveSignalCandidate>();
        await SendAsync(new
        {
            command = "bar",
            bar = new { timestamp = bar.BarStart.ToString("O", CultureInfo.InvariantCulture), symbol = bar.Symbol, exchange = "", period = PeriodText(bar.DurationSeconds), trading_day = bar.BarStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), open = bar.Open, high = bar.High, low = bar.Low, close = bar.Close, volume = bar.Volume, open_interest = bar.OpenInterest, is_complete = bar.IsComplete },
            bar_end = bar.BarEnd.ToString("O", CultureInfo.InvariantCulture),
            formal
        }, cancellationToken);
        while (true)
        {
            using var response = await ReadAsync(cancellationToken);
            var root = response.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "signal")
            {
                if (!discardSignals)
                {
                    var signal = root.GetProperty("signal");
                    candidates.Add(new LiveSignalCandidate(signal.GetProperty("category").GetString()!, signal.GetProperty("direction").GetString()!, signal.GetProperty("description").GetString()!, signal.TryGetProperty("reference_price", out var reference) && reference.ValueKind == JsonValueKind.Number ? reference.GetDecimal() : null));
                }
            }
            else if (type == "bar_done") return candidates;
            else if (type == "error") throw new LiveStrategyException(root.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "strategy" : "strategy", root.TryGetProperty("message", out var message) ? message.GetString() ?? "策略求值失败" : "策略求值失败");
            else if (type is "stopped" or "cancelled") throw new LiveStrategyException("worker_exit", $"策略 Worker 提前结束：{type}");
        }
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _stdin.WriteLineAsync(JsonSerializer.Serialize(payload, ProtocolJson).AsMemory(), cancellationToken);
        await _stdin.FlushAsync(cancellationToken);
    }

    private async Task ExpectAsync(string expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await ReadAsync(cancellationToken);
            var type = response.RootElement.GetProperty("type").GetString();
            if (type == expected) return;
            if (type == "error") throw new LiveStrategyException("strategy_start", response.RootElement.TryGetProperty("message", out var message) ? message.GetString() ?? "策略启动失败" : "策略启动失败");
        }
    }

    private async Task<JsonDocument> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);
        string? line;
        try { line = await _stdout.ReadLineAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new LiveStrategyException("strategy_timeout", "实时策略求值超过 3 秒响应期限"); }
        if (line is null) throw new LiveStrategyException("worker_exit", $"策略 Worker 已退出：{(_process.HasExited ? _process.ExitCode : -1)}");
        return JsonDocument.Parse(line);
    }

    private static string PeriodText(int seconds) => seconds switch
    {
        60 => "1m", 300 => "5m", 900 => "15m", 1800 => "30m", 3600 => "60m", 86400 => "1d", _ => $"{seconds}s"
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                try { await SendAsync(new { command = "stop" }, CancellationToken.None); } catch { }
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { StrategyCatalog.TryKill(_process); }
            }
        }
        finally
        {
            try { await _stderrTask.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch { }
            _process.Dispose();
        }
    }
}
