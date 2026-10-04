using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Qh.Core;

public enum MarketErrorKind
{
    None,
    Authentication,
    Network,
    Permission,
    Empty,
    Cancelled,
    Timeout,
    Unsupported,
    Protocol,
    InvalidRequest,
    Unknown
}

public enum MarketSessionState
{
    Offline,
    Connecting,
    Connected,
    Paused,
    Recovering,
    Stopped,
    Failed
}

public sealed record TqSdkCredentials(string Account, string Password);

public sealed record HistoricalSessionRange(string TradingDay, string Start, string End, string? SymbolScope = null);

public sealed record TqSdkRuntimeInfo(string TargetVersion, bool CanDataDownloaderModule, string Evidence);

public sealed record TqSdkCapabilities(
    string TargetVersion,
    bool Connected,
    bool CanGetKlineSerial,
    bool? CanDataDownloader,
    int KlineSerialMaxRows,
    int MaxConcreteSubscriptions,
    int InitialSubscriptionLimit,
    string Evidence);

public sealed record MarketOperationResult<T>(
    bool Success,
    MarketErrorKind ErrorKind,
    string Message,
    T? Value,
    bool IsRetryable = false,
    bool IsTransient = false);

public sealed record HistoricalMarketRequest(
    string Symbol,
    int DurationSeconds,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool PreferProfessionalDownloader = true,
    int RetryCount = 1,
    IReadOnlySet<DateOnly>? ProviderTradingDays = null,
    string? CoverageEvidence = null,
    IReadOnlyList<string>? ProviderSessionWindows = null,
    string? ProviderCalendarSource = null,
    IReadOnlyList<HistoricalSessionRange>? ProviderSessionRanges = null);

public sealed record HistoricalCoverage(
    DateTimeOffset? First,
    DateTimeOffset? Last,
    int Rows,
    bool Complete,
    bool UsedProfessionalDownloader,
    string Note);

public sealed record HistoricalMarketResult(
    IReadOnlyList<Bar> Bars,
    HistoricalCoverage Coverage,
    string ProviderRequestId);

public static class HistoricalCalendarMetadataLoader
{
    public static bool TryLoad(string path, string symbol, DateTimeOffset start, DateTimeOffset end, out IReadOnlySet<DateOnly> tradingDays, out IReadOnlyList<HistoricalSessionRange> sessionRanges, out string source, out string error)
    {
        tradingDays = new HashSet<DateOnly>();
        sessionRanges = [];
        source = "";
        error = "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            source = root.GetProperty("source").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(source)) { error = "本机日历来源为空"; return false; }
            var validFrom = DateTimeOffset.Parse(root.GetProperty("valid_from").GetString()!, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(8));
            var validTo = DateTimeOffset.Parse(root.GetProperty("valid_to").GetString()!, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(8));
            var localStart = start.ToOffset(TimeSpan.FromHours(8));
            var localEnd = end.ToOffset(TimeSpan.FromHours(8));
            if (validTo <= validFrom || localStart < validFrom || localEnd > validTo) { error = "本机日历有效范围不覆盖请求"; return false; }
            var days = root.GetProperty("trading_days").EnumerateArray().Select(item => DateOnly.Parse(item.GetString()!, CultureInfo.InvariantCulture)).ToHashSet();
            var ranges = root.GetProperty("session_ranges").EnumerateArray().Select(item => new HistoricalSessionRange(
                item.GetProperty("trading_day").GetString()!, item.GetProperty("start").GetString()!, item.GetProperty("end").GetString()!,
                item.TryGetProperty("symbol_scope", out var scope) ? scope.GetString() : null)).ToArray();
            if (days.Count == 0 || ranges.Length == 0) { error = "本机日历缺少交易日或适用时段"; return false; }
            if (ranges.Any(range => range.SymbolScope is not null && !string.Equals(range.SymbolScope, symbol, StringComparison.OrdinalIgnoreCase))) { error = "本机日历合约范围不匹配"; return false; }
            var parsedRanges = ranges.Select(range => (range, Start: DateTimeOffset.Parse(range.Start, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(8)), End: DateTimeOffset.Parse(range.End, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(8)))).ToArray();
            if (parsedRanges.Any(item => item.End <= item.Start)) { error = "本机日历时段区间无效"; return false; }
            if (!parsedRanges.Any(item => item.End > localStart && item.Start < localEnd)) { error = "本机日历不覆盖请求范围"; return false; }
            tradingDays = days;
            sessionRanges = ranges;
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or FormatException)
        {
            error = "本机日历配置不可用";
            return false;
        }
    }
}

public sealed record LiveKline(
    string Symbol,
    int DurationSeconds,
    DateTimeOffset BarStart,
    DateTimeOffset BarEnd,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal OpenInterest,
    bool IsComplete,
    long ProviderSequence,
    bool IsRecovery);

public enum LiveSessionEventKind { Connected, Bar, Paused, Recovered, Failed, Stopped }

public sealed record LiveSessionEvent(LiveSessionEventKind Kind, MarketErrorKind ErrorKind, string Message, LiveKline? Bar = null, bool RecoverySilent = false);

public interface ITqSdkGateway
{
    Task<MarketOperationResult<TqSdkCapabilities>> ProbeAsync(TqSdkCredentials? credentials, CancellationToken cancellationToken);
    Task<MarketOperationResult<HistoricalMarketResult>> DownloadHistoryAsync(TqSdkCredentials credentials, HistoricalMarketRequest request, CancellationToken cancellationToken);
}

internal static class TqSdkRuntimePaths
{
    public static string? Resolve(string pythonPath)
    {
        var configured = Environment.GetEnvironmentVariable("QH_TQSDK_PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tqsdk-site"));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tqsdk-site")));
        candidates.Add(Path.Combine(Path.GetDirectoryName(pythonPath) ?? "", "site-packages"));
        return candidates.FirstOrDefault(Directory.Exists);
    }
}

/// <summary>
/// 唯一的 TqSdk 进程接入层。密码只写入 bridge 的标准输入，不进命令行、策略进程或日志。
/// </summary>
public sealed class TqSdkGateway : ITqSdkGateway
{
    private readonly string _pythonPath;
    private readonly string _bridgePath;

    public TqSdkGateway(string? pythonPath = null, string? bridgePath = null)
    {
        _pythonPath = pythonPath ?? PythonRuntime.ResolveExecutable();
        _bridgePath = bridgePath ?? Path.Combine(AppContext.BaseDirectory, "worker", "qh_tqsdk_bridge.py");
    }

    public Task<MarketOperationResult<TqSdkCapabilities>> ProbeAsync(TqSdkCredentials? credentials, CancellationToken cancellationToken) =>
        RunAsync<TqSdkCapabilities>(new { op = "probe", account = credentials?.Account, password = credentials?.Password }, cancellationToken, ParseCapabilities, TimeSpan.FromSeconds(90));

    public Task<MarketOperationResult<TqSdkRuntimeInfo>> RuntimeCheckAsync(CancellationToken cancellationToken) =>
        RunAsync<TqSdkRuntimeInfo>(new { op = "runtime" }, cancellationToken, ParseRuntimeInfo, TimeSpan.FromSeconds(90));

    public Task<MarketOperationResult<HistoricalMarketResult>> DownloadHistoryAsync(TqSdkCredentials credentials, HistoricalMarketRequest request, CancellationToken cancellationToken) =>
        RunAsync<HistoricalMarketResult>(new
        {
            op = "history",
            account = credentials.Account,
            password = credentials.Password,
            symbol = request.Symbol,
            duration_seconds = request.DurationSeconds,
            start = request.Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            end = request.End.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            professional = request.PreferProfessionalDownloader,
            provider_trading_days = request.ProviderTradingDays?.Select(day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            coverage_evidence = request.CoverageEvidence,
            provider_session_windows = request.ProviderSessionWindows,
            provider_calendar_source = request.ProviderCalendarSource,
            provider_session_ranges = request.ProviderSessionRanges?.Select(range => new { trading_day = range.TradingDay, start = range.Start, end = range.End, symbol_scope = range.SymbolScope })
        }, cancellationToken, root => ParseHistory(root, request), TimeSpan.FromMinutes(2));

    private async Task<MarketOperationResult<T>> RunAsync<T>(object request, CancellationToken cancellationToken, Func<JsonElement, T> parse, TimeSpan deadline)
    {
        if (!File.Exists(_bridgePath)) return new(false, MarketErrorKind.Unsupported, "未找到包内 TqSdk bridge；真实连接未验证", default, false, false);
        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.ArgumentList.Add(_bridgePath);
        var projectSdkPath = TqSdkRuntimePaths.Resolve(_pythonPath);
        if (projectSdkPath is not null) startInfo.Environment["QH_TQSDK_PATH"] = projectSdkPath;
        using var process = new Process { StartInfo = startInfo };
        using var deadlineCancellation = new CancellationTokenSource(deadline);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCancellation.Token);
        var token = linkedCancellation.Token;
        Task<string>? stderrTask = null;
        try
        {
            if (!process.Start()) return new(false, MarketErrorKind.Network, "无法启动 TqSdk bridge", default, true, true);
            stderrTask = process.StandardError.ReadToEndAsync();
            var requestJson = JsonSerializer.Serialize(request);
            await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            process.StandardInput.Close();
            var responseLine = await process.StandardOutput.ReadLineAsync(token);
            await process.WaitForExitAsync(token);
            var stderr = stderrTask is null ? "" : await stderrTask;
            if (string.IsNullOrWhiteSpace(responseLine)) return new(false, MarketErrorKind.Protocol, $"TqSdk bridge 没有返回结果（{SanitizeStderr(stderr)}）", default, true, false);
            using var document = JsonDocument.Parse(responseLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var kind = root.TryGetProperty("error_kind", out var errorKind) && Enum.TryParse<MarketErrorKind>(errorKind.GetString(), true, out var parsed) ? parsed : MarketErrorKind.Protocol;
                var message = root.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() ?? "TqSdk bridge 失败" : "TqSdk bridge 失败";
                var retryable = kind is MarketErrorKind.Network or MarketErrorKind.Timeout;
                return new(false, kind, message, default, retryable, retryable);
            }
            return new(true, MarketErrorKind.None, "ok", parse(root), false, false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            var killed = await KillAndConfirmAsync(process);
            var kind = timedOut ? MarketErrorKind.Timeout : MarketErrorKind.Cancelled;
            var message = timedOut ? "TqSdk bridge 超过本次操作时限" : "操作已取消";
            return new(false, kind, killed ? message : message + "；未能在确认窗口内回收 bridge", default, timedOut, timedOut);
        }
        catch (InvalidDataException ex)
        {
            await KillAndConfirmAsync(process);
            return new(false, MarketErrorKind.InvalidRequest, ex.Message, default, false, false);
        }
        catch (Exception ex)
        {
            var killed = await KillAndConfirmAsync(process);
            var suffix = killed ? "" : "；未能在确认窗口内回收 bridge";
            return new(false, MarketErrorKind.Protocol, $"TqSdk bridge 协议失败：{ex.GetType().Name}{suffix}", default, true, false);
        }
    }

    private static async Task<bool> KillAndConfirmAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(wait.Token);
            return process.HasExited;
        }
        catch { return false; }
    }

    private static string SanitizeStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "无 stderr";
        var lower = stderr.ToLowerInvariant();
        if (lower.Contains("permission") || lower.Contains("denied")) return "stderr=permission";
        if (lower.Contains("traceback") || lower.Contains("exception")) return "stderr=python_error";
        if (lower.Contains("not found") || lower.Contains("modulenotfound")) return "stderr=dependency_missing";
        return "stderr=present";
    }

    private static TqSdkCapabilities ParseCapabilities(JsonElement root) => new(
        root.GetProperty("target_version").GetString() ?? "unknown",
        root.GetProperty("connected").GetBoolean(),
        root.GetProperty("can_get_kline_serial").GetBoolean(),
        root.GetProperty("can_data_downloader").ValueKind == JsonValueKind.Null ? null : root.GetProperty("can_data_downloader").GetBoolean(),
        root.GetProperty("kline_serial_max_rows").GetInt32(),
        root.GetProperty("max_concrete_subscriptions").GetInt32(),
        root.GetProperty("initial_subscription_limit").GetInt32(),
        root.GetProperty("evidence").GetString() ?? "");

    private static TqSdkRuntimeInfo ParseRuntimeInfo(JsonElement root) => new(
        root.GetProperty("target_version").GetString() ?? "unknown",
        root.GetProperty("can_data_downloader_module").GetBoolean(),
        root.GetProperty("evidence").GetString() ?? "");

    private static HistoricalMarketResult ParseHistory(JsonElement root, HistoricalMarketRequest request)
    {
        var bars = new List<Bar>();
        foreach (var item in root.GetProperty("bars").EnumerateArray())
        {
            var timestamp = DateTimeOffset.Parse(item.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(8));
            var symbol = item.GetProperty("symbol").GetString() ?? "";
            var period = item.GetProperty("period").GetString() ?? "";
            var expectedSymbol = request.Symbol.StartsWith("fixture://", StringComparison.Ordinal) ? "TEST.FIXTURE" : request.Symbol;
            if (!string.Equals(symbol, expectedSymbol, StringComparison.Ordinal) || !string.Equals(period, HistoricalMarketValidator.PeriodLabel(request.DurationSeconds), StringComparison.Ordinal) || timestamp < request.Start.ToOffset(TimeSpan.FromHours(8)) || timestamp >= request.End.ToOffset(TimeSpan.FromHours(8)))
                throw new InvalidDataException("历史结果未通过合约、周期或请求时间范围核对");
            if (!item.TryGetProperty("is_complete", out var complete) || !complete.GetBoolean())
                throw new InvalidDataException("历史结果包含未确认完成的 K 线");
            bars.Add(new Bar(timestamp, symbol, item.GetProperty("exchange").GetString()!, period, DateOnly.Parse(item.GetProperty("trading_day").GetString()!, CultureInfo.InvariantCulture), item.GetProperty("open").GetDecimal(), item.GetProperty("high").GetDecimal(), item.GetProperty("low").GetDecimal(), item.GetProperty("close").GetDecimal(), item.GetProperty("volume").GetDecimal(), item.GetProperty("open_interest").GetDecimal(), true));
        }
        DateTimeOffset? first = bars.Count == 0 ? null : bars[0].Timestamp;
        DateTimeOffset? last = bars.Count == 0 ? null : bars[^1].Timestamp;
        var coverage = new HistoricalCoverage(first, last, bars.Count, root.GetProperty("complete").GetBoolean(), root.GetProperty("used_professional_downloader").GetBoolean(), root.GetProperty("coverage_note").GetString() ?? "");
        var result = new HistoricalMarketResult(bars, coverage, root.GetProperty("provider_request_id").GetString() ?? "unknown");
        if (!HistoricalMarketValidator.TryValidate(request, result, out var error)) throw new InvalidDataException(error);
        return result;
    }
}

public static class TqSdkConnectionWorkflow
{
    public static async Task<MarketOperationResult<TqSdkRuntimeInfo>> PrepareAsync(TqSdkGateway gateway, CancellationToken cancellationToken, Action<string>? stage = null)
    {
        stage?.Invoke("dependency_prepare");
        return await gateway.RuntimeCheckAsync(cancellationToken);
    }

    public static async Task<MarketOperationResult<TqSdkCapabilities>> ProbeAsync(TqSdkGateway gateway, TqSdkCredentials credentials, CancellationToken cancellationToken, Action<string>? stage = null)
    {
        var runtime = await PrepareAsync(gateway, cancellationToken, stage);
        if (!runtime.Success)
            return new(false, runtime.ErrorKind, $"依赖准备未完成：{runtime.Message}", null, runtime.IsRetryable, runtime.IsTransient);
        stage?.Invoke($"network_handshake:{runtime.Value?.TargetVersion ?? "unknown"}");
        return await gateway.ProbeAsync(credentials, cancellationToken);
    }
}

public sealed class TqSdkLiveSession
{
    private readonly string _pythonPath;
    private readonly string _bridgePath;
    private readonly TqSdkCredentials _credentials;
    private readonly IReadOnlyList<string> _symbols;
    private readonly int _durationSeconds;
    private readonly DateTimeOffset? _lastCompleteBarEnd;
    private readonly IReadOnlySet<DateOnly>? _providerTradingDays;
    private readonly IReadOnlyList<HistoricalSessionRange>? _providerSessionRanges;
    private readonly string? _providerCalendarSource;

    public TqSdkLiveSession(string pythonPath, string bridgePath, TqSdkCredentials credentials, IReadOnlyList<string> symbols, int durationSeconds, DateTimeOffset? lastCompleteBarEnd = null, IReadOnlySet<DateOnly>? providerTradingDays = null, IReadOnlyList<HistoricalSessionRange>? providerSessionRanges = null, string? providerCalendarSource = null)
    {
        _pythonPath = pythonPath;
        _bridgePath = bridgePath;
        _credentials = credentials;
        _symbols = symbols;
        _durationSeconds = durationSeconds;
        _lastCompleteBarEnd = lastCompleteBarEnd;
        _providerTradingDays = providerTradingDays;
        _providerSessionRanges = providerSessionRanges;
        _providerCalendarSource = providerCalendarSource;
    }

    public async IAsyncEnumerable<LiveSessionEvent> RunEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(_bridgePath))
        {
            yield return new(LiveSessionEventKind.Failed, MarketErrorKind.Unsupported, "未找到包内 TqSdk bridge");
            yield break;
        }
        if (!File.Exists(_pythonPath) && !string.Equals(_pythonPath, "python", StringComparison.OrdinalIgnoreCase))
        {
            yield return new(LiveSessionEventKind.Failed, MarketErrorKind.Unsupported, "未找到项目隔离 Python runtime");
            yield break;
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.ArgumentList.Add(_bridgePath);
        var projectSdkPath = TqSdkRuntimePaths.Resolve(_pythonPath);
        if (projectSdkPath is not null) startInfo.Environment["QH_TQSDK_PATH"] = projectSdkPath;
        using var process = new Process { StartInfo = startInfo };
        Task<string>? stderrTask = null;
        if (!TryStart(process))
        {
            yield return new(LiveSessionEventKind.Failed, MarketErrorKind.Network, "无法启动 TqSdk live bridge");
            yield break;
        }
        try
        {
            stderrTask = process.StandardError.ReadToEndAsync();
            var request = JsonSerializer.Serialize(new { op = "live", account = _credentials.Account, password = _credentials.Password, symbols = _symbols, duration_seconds = _durationSeconds, last_complete_bar_end = _lastCompleteBarEnd?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), provider_trading_days = _providerTradingDays?.Select(day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), provider_calendar_source = _providerCalendarSource, provider_session_ranges = _providerSessionRanges?.Select(range => new { trading_day = range.TradingDay, start = range.Start, end = range.End, symbol_scope = range.SymbolScope }) });
            if (!await SendRequestAsync(process, request, cancellationToken))
            {
                yield return new(LiveSessionEventKind.Failed, MarketErrorKind.Protocol, "无法写入 TqSdk live 请求");
                yield break;
            }
            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var handshakeToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handshakeTimeout.Token);
            var firstLine = await ReadLineOrNullAsync(process.StandardOutput, handshakeToken.Token);
            if (firstLine is null)
            {
                if (!cancellationToken.IsCancellationRequested)
                    yield return new(LiveSessionEventKind.Failed, handshakeTimeout.IsCancellationRequested ? MarketErrorKind.Timeout : MarketErrorKind.Protocol, "TqSdk live bridge 未完成握手");
                yield break;
            }
            if (!TryParseEvent(firstLine, _durationSeconds, out var firstEvent))
            {
                yield return new(LiveSessionEventKind.Failed, MarketErrorKind.Protocol, "TqSdk live bridge 握手格式无效");
                yield break;
            }
            if (firstEvent.Kind == LiveSessionEventKind.Failed)
            {
                yield return firstEvent;
                yield break;
            }
            yield return firstEvent;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineOrNullAsync(process.StandardOutput, cancellationToken);
                if (line is null)
                {
                    if (!cancellationToken.IsCancellationRequested) yield return new(LiveSessionEventKind.Failed, MarketErrorKind.Protocol, "TqSdk live bridge 意外结束");
                    yield break;
                }
                if (TryParseEvent(line, _durationSeconds, out var sessionEvent)) yield return sessionEvent;
                else yield return new(LiveSessionEventKind.Failed, MarketErrorKind.Protocol, "TqSdk live bridge 事件格式无效");
            }
        }
        finally
        {
            await KillAndConfirmAsync(process);
            if (stderrTask is not null) { try { await stderrTask; } catch { } }
        }
    }

    public async IAsyncEnumerable<LiveKline> RunAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in RunEventsAsync(cancellationToken))
        {
            if (item.Kind == LiveSessionEventKind.Failed) throw new InvalidOperationException($"{item.ErrorKind}: {item.Message}");
            if (item.Bar is not null) yield return item.Bar;
        }
    }

    private static bool TryStart(Process process)
    {
        try { return process.Start(); } catch { return false; }
    }

    private static async Task<bool> SendRequestAsync(Process process, string request, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            return true;
        }
        catch { return false; }
    }

    private static async Task<string?> ReadLineOrNullAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try { return await reader.ReadLineAsync(cancellationToken); } catch (OperationCanceledException) { return null; } catch { return null; }
    }

    private static bool TryParseEvent(string line, int durationSeconds, out LiveSessionEvent sessionEvent)
    {
        sessionEvent = new(LiveSessionEventKind.Failed, MarketErrorKind.Protocol, "invalid");
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
            {
                var kind = root.TryGetProperty("error_kind", out var errorKind) && Enum.TryParse<MarketErrorKind>(errorKind.GetString(), true, out var parsed) ? parsed : MarketErrorKind.Protocol;
                var message = root.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() ?? "TqSdk live bridge 失败" : "TqSdk live bridge 失败";
                sessionEvent = new(LiveSessionEventKind.Failed, kind, message);
                return true;
            }
            var name = root.GetProperty("event").GetString();
            if (name == "connected") { sessionEvent = new(LiveSessionEventKind.Connected, MarketErrorKind.None, "实时会话已连接"); return true; }
            if (name == "paused") { sessionEvent = new(LiveSessionEventKind.Paused, MarketErrorKind.Network, root.GetProperty("message").GetString() ?? "实时会话暂停"); return true; }
            if (name == "recovery_ready") { sessionEvent = new(LiveSessionEventKind.Recovered, MarketErrorKind.None, "实时序列已就绪，正在静默补缺", RecoverySilent: true); return true; }
            if (name == "recovered") { sessionEvent = new(LiveSessionEventKind.Recovered, MarketErrorKind.None, "实时会话已恢复，继续消费新 bar", RecoverySilent: root.TryGetProperty("recovery_silent", out var silent) && silent.GetBoolean()); return true; }
            if (name != "bar") return false;
            var item = root.GetProperty("bar");
            var barStart = DateTimeOffset.Parse(item.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(8));
            var bar = new LiveKline(item.GetProperty("symbol").GetString()!, durationSeconds, barStart, barStart.AddSeconds(durationSeconds), item.GetProperty("open").GetDecimal(), item.GetProperty("high").GetDecimal(), item.GetProperty("low").GetDecimal(), item.GetProperty("close").GetDecimal(), item.GetProperty("volume").GetDecimal(), item.GetProperty("open_interest").GetDecimal(), item.GetProperty("is_complete").GetBoolean(), item.GetProperty("sequence").GetInt64(), item.TryGetProperty("is_recovery", out var recovery) && recovery.GetBoolean());
            sessionEvent = new(bar.IsRecovery ? LiveSessionEventKind.Recovered : LiveSessionEventKind.Bar, MarketErrorKind.None, bar.IsRecovery ? "恢复期补缺 bar" : "完成 bar", bar, bar.IsRecovery);
            return true;
        }
        catch { return false; }
    }

    private static async Task KillAndConfirmAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(wait.Token);
        }
        catch { }
    }
}

public sealed class FixtureTqSdkGateway : ITqSdkGateway
{
    private int _transientAttempts;

    public Task<MarketOperationResult<TqSdkCapabilities>> ProbeAsync(TqSdkCredentials? credentials, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.Account)) return Task.FromResult(new MarketOperationResult<TqSdkCapabilities>(false, MarketErrorKind.Authentication, "fixture_auth_required", null));
        if (credentials.Account == "fixture-auth") return Task.FromResult(new MarketOperationResult<TqSdkCapabilities>(false, MarketErrorKind.Authentication, "fixture_auth_failed", null));
        if (credentials.Account == "fixture-permission") return Task.FromResult(new MarketOperationResult<TqSdkCapabilities>(false, MarketErrorKind.Permission, "fixture_permission_denied", null));
        return Task.FromResult(new MarketOperationResult<TqSdkCapabilities>(true, MarketErrorKind.None, "fixture_connected", new TqSdkCapabilities("3.8.7-fixture", true, true, credentials.Account != "fixture-standard", 8000, 20, 5, "explicit fixture; not real account")));
    }

    public async Task<MarketOperationResult<HistoricalMarketResult>> DownloadHistoryAsync(TqSdkCredentials credentials, HistoricalMarketRequest request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (credentials.Account == "fixture-auth") return new(false, MarketErrorKind.Authentication, "fixture_auth_failed", null);
        if (credentials.Account == "fixture-permission") return new(false, MarketErrorKind.Permission, "fixture_history_permission_denied", null);
        if (credentials.Account == "fixture-transient" && Interlocked.Increment(ref _transientAttempts) == 1) return new(false, MarketErrorKind.Network, "fixture_transient_network", null, true, true);
        if (request.Symbol == "fixture://empty") return new(false, MarketErrorKind.Empty, "fixture_empty_result", null);
        if (request.Symbol == "fixture://cancel") { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        var start = request.Start.ToOffset(TimeSpan.FromHours(8));
        var bars = Enumerable.Range(0, 4).Select(index =>
        {
            var timestamp = start.AddMinutes(index);
            var close = 100m + index;
            return new Bar(timestamp, "TEST.FIXTURE", "FIXTURE", "1m", DateOnly.FromDateTime(timestamp.DateTime), close - 0.5m, close + 1m, close - 1m, close, 10, 5, true);
        }).ToArray();
        var professional = credentials.Account != "fixture-standard";
        var result = new HistoricalMarketResult(bars, new HistoricalCoverage(bars[0].Timestamp, bars[^1].Timestamp, bars.Length, true, professional, professional ? "fixture DataDownloader path" : "fixture standard last-8000 path"), "fixture-history-001");
        return new(true, MarketErrorKind.None, "fixture_history_completed", result);
    }
}

public sealed record HistoricalPublicationResult(string? SnapshotId, string? SnapshotDirectory, int Attempts, HistoricalCoverage? Coverage, string Message);

public static class HistoricalMarketValidator
{
    public static string PeriodLabel(int durationSeconds)
    {
        if (durationSeconds % 86400 == 0) return $"{durationSeconds / 86400}d";
        if (durationSeconds % 3600 == 0) return $"{durationSeconds / 3600}h";
        if (durationSeconds % 60 == 0) return $"{durationSeconds / 60}m";
        return $"{durationSeconds}s";
    }

    public static bool TryValidate(HistoricalMarketRequest request, HistoricalMarketResult result, out string error)
    {
        error = "";
        if (result.Bars.Count == 0) { error = "历史结果为空"; return false; }
        var expectedSymbol = request.Symbol.StartsWith("fixture://", StringComparison.Ordinal) ? "TEST.FIXTURE" : request.Symbol;
        var expectedPeriod = PeriodLabel(request.DurationSeconds);
        var start = request.Start.ToOffset(TimeSpan.FromHours(8));
        var end = request.End.ToOffset(TimeSpan.FromHours(8));
        if (result.Coverage.Rows != result.Bars.Count || result.Coverage.First != result.Bars[0].Timestamp || result.Coverage.Last != result.Bars[^1].Timestamp)
        {
            error = "历史结果 Coverage 字段与 bars 不一致";
            return false;
        }
        if (result.Bars.Zip(result.Bars.Skip(1)).Any(pair => pair.First.Timestamp >= pair.Second.Timestamp))
        {
            error = "历史结果 bars 未按时间升序排列";
            return false;
        }
        if (result.Bars.Any(bar => !string.Equals(bar.Symbol, expectedSymbol, StringComparison.Ordinal) || !string.Equals(bar.Period, expectedPeriod, StringComparison.Ordinal) || bar.Timestamp < start || bar.Timestamp >= end || !bar.IsComplete))
        {
            error = "历史结果未通过合约、周期、请求时间范围或完成状态核对";
            return false;
        }
        if (request.DurationSeconds < 86400 && request.ProviderSessionRanges is not null)
        {
            foreach (var bar in result.Bars)
            {
                var matchingRanges = request.ProviderSessionRanges.Where(range =>
                {
                    if (range.SymbolScope is not null && !string.Equals(range.SymbolScope, request.Symbol, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!DateOnly.TryParse(range.TradingDay, CultureInfo.InvariantCulture, out _)) return false;
                    if (!DateTimeOffset.TryParse(range.Start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var rangeStart) || !DateTimeOffset.TryParse(range.End, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var rangeEnd)) return false;
                    return rangeEnd > rangeStart && bar.Timestamp >= rangeStart.ToOffset(TimeSpan.FromHours(8)) && bar.Timestamp < rangeEnd.ToOffset(TimeSpan.FromHours(8));
                }).ToArray();
                if (matchingRanges.Length == 0 || matchingRanges.Any(range => !DateOnly.TryParse(range.TradingDay, CultureInfo.InvariantCulture, out var tradingDay) || bar.TradingDay != tradingDay))
                {
                    error = "历史结果供应商交易日与适用日历时段不一致";
                    return false;
                }
            }
        }
        var actual = result.Bars.Select(bar => bar.Timestamp).ToHashSet();
        var expected = new HashSet<DateTimeOffset>();
        var computedComplete = false;
        if (request.DurationSeconds >= 86400)
        {
            if (request.ProviderTradingDays is null) { error = "日线交易日历元数据缺失，不能宣称完整覆盖"; return false; }
            if (result.Bars.Any(bar => !request.ProviderTradingDays.Contains(bar.TradingDay)))
            {
                error = "日线结果包含不在本机交易日历集合中的 TradingDay";
                return false;
            }
            var expectedDays = request.ProviderTradingDays.Where(day => day >= DateOnly.FromDateTime(start.DateTime) && day < DateOnly.FromDateTime(end.DateTime)).ToHashSet();
            var actualDays = result.Bars.Select(bar => bar.TradingDay).ToHashSet();
            computedComplete = expectedDays.Count > 0 && expectedDays.IsSubsetOf(actualDays);
        }
        else
        {
            if (request.ProviderTradingDays is null || string.IsNullOrWhiteSpace(request.ProviderCalendarSource) || (request.ProviderSessionRanges is null && (request.ProviderSessionWindows is null || request.ProviderSessionWindows.Count == 0)))
            {
                error = "分钟线交易日/时段元数据缺失，不能宣称完整覆盖";
                return false;
            }
            if (!TryBuildExpectedMinuteSet(start, end, request.DurationSeconds, request.Symbol, request.ProviderTradingDays, request.ProviderSessionWindows, request.ProviderSessionRanges, out expected))
            {
                error = "分钟线交易时段元数据无效，不能宣称完整覆盖";
                return false;
            }
            computedComplete = expected.Count > 0 && result.Bars[0].Timestamp >= expected.Min() && expected.IsSubsetOf(actual);
        }
        if (request.DurationSeconds < 86400)
            computedComplete = computedComplete && result.Bars[^1].Timestamp + TimeSpan.FromSeconds(request.DurationSeconds) >= expected.Max() + TimeSpan.FromSeconds(request.DurationSeconds);
        if (result.Coverage.Complete != computedComplete)
        {
            error = computedComplete ? "历史结果 Coverage.Complete 未反映完整覆盖" : "历史结果实际缺少请求区间内有效时段，不能宣称完整覆盖";
            return false;
        }
        if (!computedComplete) { error = "历史结果未完整覆盖请求区间"; return false; }
        return true;
    }

    private static bool TryBuildExpectedMinuteSet(DateTimeOffset start, DateTimeOffset end, int durationSeconds, string symbol, IReadOnlySet<DateOnly> tradingDays, IReadOnlyList<string>? windows, IReadOnlyList<HistoricalSessionRange>? ranges, out HashSet<DateTimeOffset> expected)
    {
        expected = new HashSet<DateTimeOffset>();
        if (ranges is not null)
        {
            foreach (var range in ranges)
            {
                if (!DateOnly.TryParse(range.TradingDay, CultureInfo.InvariantCulture, out var tradingDay) || !tradingDays.Contains(tradingDay) || range.SymbolScope is not null && !string.Equals(range.SymbolScope, symbol, StringComparison.OrdinalIgnoreCase)) return false;
                if (!DateTimeOffset.TryParse(range.Start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var rangeStart) || !DateTimeOffset.TryParse(range.End, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var rangeEnd) || rangeEnd <= rangeStart) return false;
                rangeStart = rangeStart.ToOffset(TimeSpan.FromHours(8));
                rangeEnd = rangeEnd.ToOffset(TimeSpan.FromHours(8));
                for (var candidate = rangeStart; candidate < rangeEnd; candidate += TimeSpan.FromSeconds(durationSeconds))
                    if (candidate >= start && candidate < end) expected.Add(candidate);
            }
            return expected.Count > 0;
        }
        if (windows is null) return false;
        var parsed = new List<(TimeOnly Start, TimeOnly End)>();
        foreach (var window in windows)
        {
            var parts = window.Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TimeOnly.TryParse(parts[0], CultureInfo.InvariantCulture, out var sessionStart) || !TimeOnly.TryParse(parts[1], CultureInfo.InvariantCulture, out var sessionEnd))
                return false;
            parsed.Add((sessionStart, sessionEnd));
        }
        var firstDay = DateOnly.FromDateTime(start.DateTime).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(end.DateTime).AddDays(1);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (!tradingDays.Contains(day)) continue;
            foreach (var (sessionStart, sessionEnd) in parsed)
            {
                var sessionBegin = new DateTimeOffset(day.ToDateTime(sessionStart, DateTimeKind.Unspecified), TimeSpan.FromHours(8));
                var sessionFinishDay = sessionEnd <= sessionStart ? day.AddDays(1) : day;
                var sessionFinish = new DateTimeOffset(sessionFinishDay.ToDateTime(sessionEnd, DateTimeKind.Unspecified), TimeSpan.FromHours(8));
                var candidate = sessionBegin;
                while (candidate < sessionFinish)
                {
                    if (candidate >= start && candidate < end) expected.Add(candidate);
                    candidate += TimeSpan.FromSeconds(durationSeconds);
                }
            }
        }
        return expected.Count > 0;
    }
}

public sealed class MarketHistoryPublisher
{
    public async Task<MarketOperationResult<HistoricalPublicationResult>> DownloadAndPublishAsync(ITqSdkGateway gateway, TqSdkCredentials credentials, HistoricalMarketRequest request, DataStore store, CancellationToken cancellationToken)
    {
        var attempts = 0;
        while (true)
        {
            attempts++;
            MarketOperationResult<HistoricalMarketResult> download;
            try { download = await gateway.DownloadHistoryAsync(credentials, request, cancellationToken); }
            catch (OperationCanceledException) { return new(false, MarketErrorKind.Cancelled, "历史任务已取消", null); }
            if (!download.Success)
            {
                if (download.IsRetryable && attempts <= request.RetryCount)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempts), cancellationToken);
                    continue;
                }
                return new(false, download.ErrorKind, download.Message, new HistoricalPublicationResult(null, null, attempts, null, download.Message), download.IsRetryable, download.IsTransient);
            }
            if (download.Value is null || download.Value.Bars.Count == 0) return new(false, MarketErrorKind.Empty, "历史结果为空，未发布", new HistoricalPublicationResult(null, null, attempts, download.Value?.Coverage, "empty"));
            if (!HistoricalMarketValidator.TryValidate(request, download.Value, out var validationError))
                return new(false, MarketErrorKind.InvalidRequest, $"历史结果核对失败：{validationError}", new HistoricalPublicationResult(null, null, attempts, download.Value.Coverage, validationError));

            var staging = Path.Combine(store.RootDirectory, "market", "downloads", Guid.NewGuid().ToString("N"));
            var source = Path.Combine(staging, "download.csv");
            try
            {
                Directory.CreateDirectory(staging);
                await File.WriteAllTextAsync(source, ToCsv(download.Value.Bars), new UTF8Encoding(false), cancellationToken);
                var import = await new CsvImporter().ImportAsync(source, store, cancellationToken: cancellationToken);
                if (!import.Published) return new(false, MarketErrorKind.InvalidRequest, $"历史质量校验未通过：{import.Message}", new HistoricalPublicationResult(null, null, attempts, download.Value.Coverage, import.Message));
                return new(true, MarketErrorKind.None, "历史校验并发布成功", new HistoricalPublicationResult(import.SnapshotId, import.SnapshotDirectory, attempts, download.Value.Coverage, import.Message));
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                var downloadsRoot = Path.GetDirectoryName(staging);
                if (downloadsRoot is not null && Directory.Exists(downloadsRoot) && !Directory.EnumerateFileSystemEntries(downloadsRoot).Any()) Directory.Delete(downloadsRoot);
            }
        }
    }

    private static string ToCsv(IReadOnlyList<Bar> bars)
    {
        var builder = new StringBuilder("timestamp,symbol,exchange,period,trading_day,open,high,low,close,volume,open_interest,is_complete\n");
        foreach (var bar in bars)
            builder.AppendJoin(',', bar.Timestamp.ToString("O", CultureInfo.InvariantCulture), bar.Symbol, bar.Exchange, bar.Period, bar.TradingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), bar.Open.ToString(CultureInfo.InvariantCulture), bar.High.ToString(CultureInfo.InvariantCulture), bar.Low.ToString(CultureInfo.InvariantCulture), bar.Close.ToString(CultureInfo.InvariantCulture), bar.Volume.ToString(CultureInfo.InvariantCulture), bar.OpenInterest.ToString(CultureInfo.InvariantCulture), bar.IsComplete ? "true" : "false").Append('\n');
        return builder.ToString();
    }
}

public sealed class LiveBarNormalizer
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IReadOnlyList<LiveKline> Accept(IEnumerable<LiveKline> input)
    {
        var accepted = new List<LiveKline>();
        foreach (var item in input)
        {
            if (!item.IsComplete) continue;
            var key = $"{item.Symbol}|{item.DurationSeconds}|{item.BarEnd:O}";
            if (_seen.Add(key)) accepted.Add(item);
        }
        return accepted;
    }
}

public sealed class PersistentLiveBarNormalizer
{
    private readonly string _path;
    private readonly HashSet<string> _seen;
    private readonly string? _loadError;

    public PersistentLiveBarNormalizer(string rootDirectory)
    {
        _path = Path.Combine(Path.GetFullPath(rootDirectory), "market", "live-bars.json");
        _loadError = null;
        try
        {
            _seen = LoadSeen(_path);
        }
        catch (Exception ex)
        {
            _seen = new(StringComparer.Ordinal);
            _loadError = $"实时去重账本损坏：{ex.GetType().Name}；已暂停以避免重复历史 bar";
        }
    }

    public IReadOnlyList<LiveKline> Accept(IEnumerable<LiveKline> input)
    {
        if (_loadError is not null) throw new InvalidDataException(_loadError);
        var accepted = new List<LiveKline>();
        foreach (var item in input)
        {
            if (!item.IsComplete) continue;
            if (_seen.Add(Key(item))) accepted.Add(item);
        }
        if (accepted.Count > 0) SaveSeen();
        return accepted;
    }

    private static string Key(LiveKline item) => $"{item.Symbol}|{item.DurationSeconds}|{item.BarEnd:O}";

    private static HashSet<string> LoadSeen(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path, Encoding.UTF8));
        if (values is null) throw new InvalidDataException("实时去重账本不是有效数组");
        return new(values, StringComparer.Ordinal);
    }

    private void SaveSeen()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(_seen.Order(StringComparer.Ordinal).ToArray()), Encoding.UTF8);
        File.Move(temp, _path, overwrite: true);
    }
}

public sealed class LiveSessionCoordinator
{
    private readonly PersistentLiveBarNormalizer _normalizer;
    private readonly LiveSessionLedger _ledger;

    public LiveSessionCoordinator(string rootDirectory)
    {
        _normalizer = new PersistentLiveBarNormalizer(rootDirectory);
        _ledger = new LiveSessionLedger(rootDirectory);
    }

    public async Task<IReadOnlyList<LiveKline>> AcceptAsync(string sessionId, IEnumerable<LiveKline> bars, MarketSessionState state, CancellationToken cancellationToken)
    {
        var accepted = _normalizer.Accept(bars);
        var last = accepted.LastOrDefault();
        var previous = _ledger.Load();
        var lastEnd = last?.BarEnd ?? previous?.LastCompleteBarEnd;
        var sequence = last?.ProviderSequence ?? previous?.LastProviderSequence ?? 0;
        var actualState = accepted.Any(item => item.IsRecovery) ? MarketSessionState.Recovering : state;
        var recoverySilent = actualState == MarketSessionState.Recovering && accepted.Any(item => item.IsRecovery);
        await _ledger.SaveAsync(new LiveSessionLedgerState(sessionId, actualState, lastEnd, sequence, recoverySilent, DateTimeOffset.UtcNow), cancellationToken);
        return accepted;
    }

    public async Task SaveStateAsync(string sessionId, MarketSessionState state, bool recoverySilent, CancellationToken cancellationToken)
    {
        var previous = _ledger.Load();
        await _ledger.SaveAsync(new LiveSessionLedgerState(sessionId, state, previous?.LastCompleteBarEnd, previous?.LastProviderSequence ?? 0, recoverySilent, DateTimeOffset.UtcNow), cancellationToken);
    }

    public LiveSessionLedgerState? LoadState() => _ledger.Load();

    public async Task<IReadOnlyList<LiveKline>> AcceptEventAsync(string sessionId, LiveSessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        if (sessionEvent.Kind == LiveSessionEventKind.Connected)
        {
            await SaveStateAsync(sessionId, MarketSessionState.Connected, false, cancellationToken);
            return [];
        }
        if (sessionEvent.Kind == LiveSessionEventKind.Paused)
        {
            await SaveStateAsync(sessionId, MarketSessionState.Paused, true, cancellationToken);
            return [];
        }
        if (sessionEvent.Kind == LiveSessionEventKind.Failed)
        {
            await SaveStateAsync(sessionId, MarketSessionState.Failed, true, cancellationToken);
            return [];
        }
        if (sessionEvent.Bar is not null)
            return await AcceptAsync(sessionId, [sessionEvent.Bar], sessionEvent.Kind == LiveSessionEventKind.Recovered ? MarketSessionState.Recovering : MarketSessionState.Connected, cancellationToken);
        await SaveStateAsync(sessionId, sessionEvent.RecoverySilent ? MarketSessionState.Recovering : MarketSessionState.Connected, sessionEvent.RecoverySilent, cancellationToken);
        return [];
    }
}

internal sealed class WrongContractFixtureGateway : ITqSdkGateway
{
    public Task<MarketOperationResult<TqSdkCapabilities>> ProbeAsync(TqSdkCredentials? credentials, CancellationToken cancellationToken) =>
        Task.FromResult(new MarketOperationResult<TqSdkCapabilities>(false, MarketErrorKind.Unsupported, "wrong_contract_fixture", null));

    public Task<MarketOperationResult<HistoricalMarketResult>> DownloadHistoryAsync(TqSdkCredentials credentials, HistoricalMarketRequest request, CancellationToken cancellationToken)
    {
        var start = request.Start.ToOffset(TimeSpan.FromHours(8));
        var bars = Enumerable.Range(0, 4).Select(index => new Bar(start.AddMinutes(index), "TEST.WRONG", "TEST", "1m", DateOnly.FromDateTime(start.DateTime), 100, 101, 99, 100, 1, 1, true)).ToArray();
        var value = new HistoricalMarketResult(bars, new HistoricalCoverage(bars[0].Timestamp, bars[^1].Timestamp, bars.Length, true, false, "wrong contract fixture"), "wrong-contract-fixture");
        return Task.FromResult(new MarketOperationResult<HistoricalMarketResult>(true, MarketErrorKind.None, "wrong_contract_fixture", value));
    }
}

public sealed record LiveSessionLedgerState(string SessionId, MarketSessionState State, DateTimeOffset? LastCompleteBarEnd, long LastProviderSequence, bool RecoverySilent, DateTimeOffset UpdatedAt);

public sealed class LiveSessionLedger
{
    private readonly string _path;
    public LiveSessionLedger(string rootDirectory) => _path = Path.Combine(Path.GetFullPath(rootDirectory), "market", "live-session.json");

    public LiveSessionLedgerState? Load()
    {
        if (!File.Exists(_path)) return null;
        return JsonSerializer.Deserialize<LiveSessionLedgerState>(File.ReadAllText(_path, Encoding.UTF8));
    }

    public async Task SaveAsync(LiveSessionLedgerState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temp, _path, overwrite: true);
    }
}

public static class G4FixtureVerifier
{
    private static readonly string[] FixtureSessionWindows = ["09:00-10:15", "10:30-11:30", "13:30-15:00", "21:00-23:00", "23:00-02:30"];

    private static HistoricalMarketRequest FixtureMinuteRequest(string symbol, DateTimeOffset start, DateTimeOffset end, int retryCount = 1) =>
        new(symbol, 60, start, end, RetryCount: retryCount, ProviderTradingDays: new HashSet<DateOnly> { DateOnly.FromDateTime(start.DateTime) }, ProviderSessionWindows: FixtureSessionWindows, ProviderCalendarSource: "fixture-structured-session-calendar");

    public static async Task<JsonDocument> RunAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var gateway = new FixtureTqSdkGateway();
        var checks = new Dictionary<string, object?>();
        var auth = await gateway.ProbeAsync(new TqSdkCredentials("fixture-auth", "not-written"), cancellationToken);
        checks["authentication_failure"] = auth.ErrorKind == MarketErrorKind.Authentication;
        var permission = await gateway.ProbeAsync(new TqSdkCredentials("fixture-permission", "not-written"), cancellationToken);
        checks["permission_failure"] = permission.ErrorKind == MarketErrorKind.Permission;
        var standard = await gateway.DownloadHistoryAsync(new TqSdkCredentials("fixture-standard", "not-written"), FixtureMinuteRequest("fixture://standard", DateTimeOffset.Parse("2026-10-01T09:00:00+08:00"), DateTimeOffset.Parse("2026-10-01T09:04:00+08:00")), cancellationToken);
        checks["standard_history"] = standard.Success && standard.Value?.Coverage.UsedProfessionalDownloader == false && standard.Value.Bars.Count == 4;
        var professional = await gateway.DownloadHistoryAsync(new TqSdkCredentials("fixture-professional", "not-written"), FixtureMinuteRequest("fixture://professional", DateTimeOffset.Parse("2026-10-01T09:00:00+08:00"), DateTimeOffset.Parse("2026-10-01T09:04:00+08:00")), cancellationToken);
        checks["professional_history"] = professional.Success && professional.Value?.Coverage.UsedProfessionalDownloader == true;
        var empty = await gateway.DownloadHistoryAsync(new TqSdkCredentials("fixture-professional", "not-written"), new HistoricalMarketRequest("fixture://empty", 60, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow), cancellationToken);
        checks["empty_result"] = empty.ErrorKind == MarketErrorKind.Empty;
        var publisher = new MarketHistoryPublisher();
        var store = new DataStore(rootDirectory);
        var publishRequest = FixtureMinuteRequest("fixture://publish", DateTimeOffset.Parse("2026-10-01T09:00:00+08:00"), DateTimeOffset.Parse("2026-10-01T09:04:00+08:00"));
        var firstPublish = await publisher.DownloadAndPublishAsync(gateway, new TqSdkCredentials("fixture-professional", "not-written"), publishRequest, store, cancellationToken);
        var secondPublish = await publisher.DownloadAndPublishAsync(gateway, new TqSdkCredentials("fixture-professional", "not-written"), publishRequest, store, cancellationToken);
        checks["validated_publish_and_idempotent"] = firstPublish.Success && secondPublish.Success && firstPublish.Value?.SnapshotId == secondPublish.Value?.SnapshotId && store.GetSnapshots().Count == 1 && !Directory.Exists(Path.Combine(rootDirectory, "market", "downloads"));
        var transient = await publisher.DownloadAndPublishAsync(gateway, new TqSdkCredentials("fixture-transient", "not-written"), FixtureMinuteRequest("fixture://retry", DateTimeOffset.Parse("2026-10-01T09:00:00+08:00"), DateTimeOffset.Parse("2026-10-01T09:04:00+08:00")), store, cancellationToken);
        checks["retry_once"] = transient.Success && transient.Value?.Attempts == 2;
        var wrongContract = await publisher.DownloadAndPublishAsync(new WrongContractFixtureGateway(), new TqSdkCredentials("fixture", "not-written"), FixtureMinuteRequest("fixture://wrong", DateTimeOffset.Parse("2026-10-01T09:00:00+08:00"), DateTimeOffset.Parse("2026-10-01T09:04:00+08:00")), store, cancellationToken);
        checks["reject_wrong_contract"] = wrongContract.ErrorKind == MarketErrorKind.InvalidRequest && wrongContract.Value?.SnapshotId is null;
        var lunchStart = DateTimeOffset.Parse("2026-09-29T10:15:00+08:00");
        var lunchEnd = DateTimeOffset.Parse("2026-09-29T10:32:00+08:00");
        var lunchBars = new[]
        {
            new Bar(DateTimeOffset.Parse("2026-09-29T10:30:00+08:00"), "TEST.FIXTURE", "FIXTURE", "1m", DateOnly.Parse("2026-09-29"), 100, 102, 99, 101, 10, 6, true),
            new Bar(DateTimeOffset.Parse("2026-09-29T10:31:00+08:00"), "TEST.FIXTURE", "FIXTURE", "1m", DateOnly.Parse("2026-09-29"), 100, 102, 99, 101, 10, 6, true)
        };
        var lunchResult = new HistoricalMarketResult(lunchBars, new HistoricalCoverage(lunchBars[0].Timestamp, lunchBars[^1].Timestamp, lunchBars.Length, true, false, "fixture provider session calendar"), "fixture-lunch-calendar");
        var lunchAccepted = HistoricalMarketValidator.TryValidate(FixtureMinuteRequest("fixture://lunch", lunchStart, lunchEnd), lunchResult, out _);
        var sparseBars = new[]
        {
            lunchBars[0] with { Timestamp = DateTimeOffset.Parse("2026-09-29T09:00:00+08:00") },
            lunchBars[1] with { Timestamp = DateTimeOffset.Parse("2026-09-29T09:03:00+08:00") }
        };
        var sparseResult = new HistoricalMarketResult(sparseBars, new HistoricalCoverage(sparseBars[0].Timestamp, sparseBars[^1].Timestamp, sparseBars.Length, true, false, "fixture provider session calendar"), "fixture-real-gap");
        var sparseAccepted = HistoricalMarketValidator.TryValidate(FixtureMinuteRequest("fixture://gap", DateTimeOffset.Parse("2026-09-29T09:00:00+08:00"), DateTimeOffset.Parse("2026-09-29T09:04:00+08:00")), sparseResult, out _);
        var dailyBars = new[]
        {
            new Bar(DateTimeOffset.Parse("2026-09-25T00:00:00+08:00"), "TEST.FIXTURE", "FIXTURE", "1d", DateOnly.Parse("2026-09-25"), 100, 102, 99, 101, 10, 6, true),
            new Bar(DateTimeOffset.Parse("2026-09-28T00:00:00+08:00"), "TEST.FIXTURE", "FIXTURE", "1d", DateOnly.Parse("2026-09-28"), 100, 102, 99, 101, 10, 6, true)
        };
        var dailyResult = new HistoricalMarketResult(dailyBars, new HistoricalCoverage(dailyBars[0].Timestamp, dailyBars[^1].Timestamp, dailyBars.Length, true, false, "fixture provider weekday calendar"), "fixture-weekday-calendar");
        var dailyTradingDays = new HashSet<DateOnly> { DateOnly.Parse("2026-09-25"), DateOnly.Parse("2026-09-28") };
        var dailySessionRanges = new[] { new HistoricalSessionRange("2026-09-25", "2026-09-25T09:00:00+08:00", "2026-09-25T15:00:00+08:00", "fixture://daily") };
        var dailyRequest = new HistoricalMarketRequest("fixture://daily", 86400, DateTimeOffset.Parse("2026-09-25T00:00:00+08:00"), DateTimeOffset.Parse("2026-09-29T00:00:00+08:00"), ProviderTradingDays: dailyTradingDays, ProviderCalendarSource: "fixture-structured-trading-day-calendar", ProviderSessionRanges: dailySessionRanges);
        var dailyAccepted = HistoricalMarketValidator.TryValidate(dailyRequest, dailyResult, out _);
        var invalidDailyBars = new[] { dailyBars[0], dailyBars[1] with { TradingDay = DateOnly.Parse("2026-09-27") } };
        var invalidDailyResult = dailyResult with { Bars = invalidDailyBars, Coverage = dailyResult.Coverage with { First = invalidDailyBars[0].Timestamp, Last = invalidDailyBars[^1].Timestamp } };
        var dailyTradingDayRejected = !HistoricalMarketValidator.TryValidate(dailyRequest, invalidDailyResult, out _);
        var dailyKeywordOnlyAccepted = HistoricalMarketValidator.TryValidate(new HistoricalMarketRequest("fixture://daily", 86400, DateTimeOffset.Parse("2026-09-25T09:00:00+08:00"), DateTimeOffset.Parse("2026-09-29T09:00:00+08:00")), dailyResult, out _);
        checks["daily_midnight_timestamp_calendar"] = dailyAccepted;
        checks["daily_unknown_trading_day_rejected"] = dailyTradingDayRejected;
        checks["calendar_boundaries"] = lunchAccepted && !sparseAccepted && dailyAccepted && dailyTradingDayRejected && !dailyKeywordOnlyAccepted;
        using var cancelled = new CancellationTokenSource(100);
        var cancelledResult = await publisher.DownloadAndPublishAsync(gateway, new TqSdkCredentials("fixture-professional", "not-written"), FixtureMinuteRequest("fixture://cancel", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow), store, cancelled.Token);
        checks["cancelled"] = cancelledResult.ErrorKind == MarketErrorKind.Cancelled;
        var bar = new LiveKline("TEST.FIXTURE", 60, DateTimeOffset.Parse("2026-10-01T09:00:00+08:00"), DateTimeOffset.Parse("2026-10-01T09:01:00+08:00"), 100, 101, 99, 100.5m, 1, 1, true, 1, false);
        var recoveryBar = bar with { BarStart = bar.BarEnd, BarEnd = bar.BarEnd.AddMinutes(1), ProviderSequence = 2, IsRecovery = true };
        var session = new LiveSessionCoordinator(rootDirectory);
        var accepted = await session.AcceptAsync("fixture-session", [bar, bar, bar with { IsComplete = false }, recoveryBar], MarketSessionState.Recovering, cancellationToken);
        checks["complete_bar_dedup_and_recovery_silent"] = accepted.Count == 2 && accepted[1].IsRecovery && accepted.All(item => item.IsComplete);
        var eventSession = new LiveSessionCoordinator(Path.Combine(rootDirectory, "event-consumer"));
        var eventAccepted = await eventSession.AcceptEventAsync("fixture-event-session", new LiveSessionEvent(LiveSessionEventKind.Recovered, MarketErrorKind.None, "fixture recovery bar", recoveryBar, true), cancellationToken);
        var eventState = eventSession.LoadState();
        checks["event_consumer_recovery_bar"] = eventAccepted.Count == 1 && eventState?.State == MarketSessionState.Recovering && eventState.RecoverySilent;
        var recoveryState = new LiveSessionLedger(rootDirectory).Load();
        var restarted = new LiveSessionCoordinator(rootDirectory);
        var newBar = recoveryBar with { BarStart = recoveryBar.BarEnd, BarEnd = recoveryBar.BarEnd.AddMinutes(1), ProviderSequence = 3, IsRecovery = false };
        var afterRestart = await restarted.AcceptAsync("fixture-session", [recoveryBar, newBar], MarketSessionState.Connected, cancellationToken);
        var ledger = new LiveSessionLedger(rootDirectory);
        var restored = ledger.Load();
        checks["restart_ledger"] = recoveryState?.State == MarketSessionState.Recovering && recoveryState.RecoverySilent && afterRestart.Count == 1 && afterRestart[0].BarEnd == newBar.BarEnd && restored?.SessionId == "fixture-session" && restored.State == MarketSessionState.Connected && !restored.RecoverySilent;
        checks["all_passed"] = checks.Values.All(value => value is true);
        return JsonDocument.Parse(JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
