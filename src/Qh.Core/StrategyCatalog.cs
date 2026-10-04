using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qh.Core;

public static class StrategyCatalog
{
    public static async Task<StrategyDescriptor> DescribeAsync(string strategyPath, string workerPath, CancellationToken cancellationToken = default)
    {
        var fullStrategy = ValidateStrategyPath(strategyPath);
        var fullWorker = Path.GetFullPath(workerPath);
        if (!File.Exists(fullWorker)) throw new FileNotFoundException("找不到策略 Worker", fullWorker);
        var result = await RunPythonAsync(fullWorker, ["--describe", fullStrategy], TimeSpan.FromMilliseconds(3500), cancellationToken);
        if (result.TimedOut) throw new TimeoutException("策略描述超时（可能卡在模块加载）");
        if (result.OutputLimited) throw new InvalidDataException($"output_limit; phase=describe; strategy={fullStrategy}; {result.OutputLimitMessage}");
        if (result.ExitCode != 0) throw new InvalidDataException(FormatPythonError(result));
        using var document = JsonDocument.Parse(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("type").GetString(), "descriptor", StringComparison.Ordinal))
            throw new InvalidDataException("策略描述响应类型错误");
        var metadata = root.GetProperty("metadata");
        return ParseDescriptor(fullStrategy, metadata);
    }

    internal static string ValidateStrategyPath(string strategyPath)
    {
        var full = Path.GetFullPath(strategyPath);
        if (!File.Exists(full)) throw new FileNotFoundException("找不到策略文件", full);
        if (!string.Equals(Path.GetExtension(full), ".py", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("策略文件必须是 .py");
        return full;
    }

    internal static StrategyDescriptor ParseDescriptor(string strategyPath, JsonElement metadata)
    {
        var parameters = new Dictionary<string, StrategyParameter>(StringComparer.Ordinal);
        foreach (var property in metadata.GetProperty("parameters").EnumerateObject())
        {
            var spec = property.Value;
            var choices = spec.TryGetProperty("choices", out var choiceElement)
                ? choiceElement.EnumerateArray().Select(x => x.Clone()).ToArray()
                : null;
            parameters[property.Name] = new StrategyParameter(
                spec.GetProperty("type").GetString() ?? "",
                spec.GetProperty("default").Clone(),
                spec.TryGetProperty("min", out var min) ? min.Clone() : null,
                spec.TryGetProperty("max", out var max) ? max.Clone() : null,
                choices);
        }
        var metadataJson = metadata.GetRawText();
        var warmupParameterNames = metadata.TryGetProperty("warmup_bars_parameters", out var warmupParameters)
            ? warmupParameters.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
            : [];
        return new StrategyDescriptor(
            metadata.GetProperty("name").GetString() ?? "",
            metadata.GetProperty("version").GetString() ?? "",
            metadata.GetProperty("kind").GetString() ?? "",
            metadata.GetProperty("warmup_bars").GetInt32(),
            warmupParameterNames,
            parameters,
            strategyPath,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(strategyPath))).ToLowerInvariant(),
            metadataJson);
    }

    internal static async Task<ProcessResult> RunPythonAsync(string workerPath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
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
        start.ArgumentList.Add(workerPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Python Worker");
        var outputBudget = new StrategyProcessOutputBudget();
        var stdout = StrategyProcessOutput.ReadAllAsyncBounded(process.StandardOutput, outputBudget, "describe_stdout", process, cancellationToken);
        var stderr = StrategyProcessOutput.ReadAllAsyncBounded(process.StandardError, outputBudget, "describe_stderr", process, cancellationToken);
        var cancelled = false;
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            TryKill(process);
            await WaitForExitWithDeadlineAsync(process, TimeSpan.FromMilliseconds(300));
            try { await stdout; } catch { }
            try { await stderr; } catch { }
            throw;
        }
        catch (TimeoutException)
        {
            timedOut = true;
            TryKill(process);
            await WaitForExitWithDeadlineAsync(process, TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            if (!process.HasExited && (cancelled || timedOut)) TryKill(process);
        }
        string stdoutText = "", stderrText = "";
        StrategyOutputLimitException? outputLimit = outputBudget.LimitException;
        try { stdoutText = await stdout; } catch (StrategyOutputLimitException exception) { outputLimit ??= exception; }
        try { stderrText = await stderr; } catch (StrategyOutputLimitException exception) { outputLimit ??= exception; }
        return new ProcessResult(process.HasExited ? process.ExitCode : -1, stdoutText, stderrText, timedOut, outputLimit is not null, outputLimit?.Message);
    }

    private static async Task WaitForExitWithDeadlineAsync(Process process, TimeSpan deadline)
    {
        if (process.HasExited) return;
        try { await process.WaitForExitAsync().WaitAsync(deadline); }
        catch
        {
            TryKill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(300)); } catch { }
        }
    }

    internal static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    internal static string FormatPythonError(ProcessResult result) =>
        $"stdout={result.Stdout.Trim()} stderr={result.Stderr.Trim()} exit={result.ExitCode}";

    internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool OutputLimited, string? OutputLimitMessage);
}
