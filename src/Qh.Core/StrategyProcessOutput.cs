using System.Diagnostics;
using System.Text;

namespace Qh.Core;

internal static class StrategyProcessOutputLimits
{
    public const int MaxProtocolLineChars = 1 * 1024 * 1024;
    public const long MaxWorkerLogBytes = 4L * 1024 * 1024;
    public const long MaxStderrBytes = 256L * 1024;
    public const long MaxDescribeBytes = 256L * 1024;
    public const int SegmentBytes = 64 * 1024;
    public const int TailBytes = 16 * 1024;
    public const int TailCharacters = 4000;
}

internal sealed class StrategyOutputLimitException(string stream, string phase, long limit, long observed, string unit) : Exception(
    $"stream={stream}; phase={phase}; limit={limit}{unit}; observed={observed}{unit}")
{
    public string Stream { get; } = stream;
    public string Phase { get; } = phase;
    public long Limit { get; } = limit;
    public long Observed { get; } = observed;
    public string Unit { get; } = unit;
}

internal sealed class StrategyProcessOutputBudget
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _used = new(StringComparer.Ordinal);
    private StrategyOutputLimitException? _limitException;

    public StrategyOutputLimitException? LimitException
    {
        get { lock (_gate) return _limitException; }
    }

    public void Reserve(string stream, long bytes, long limit, string phase)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            if (_limitException is not null) throw _limitException;
            var used = _used.GetValueOrDefault(stream);
            if (used > limit - bytes)
            {
                var exception = new StrategyOutputLimitException(stream, phase, limit, used + bytes, " bytes");
                _limitException = exception;
                throw exception;
            }
            _used[stream] = used + bytes;
        }
    }

    public bool TryReserve(string stream, long bytes, long limit)
    {
        if (bytes <= 0) return true;
        lock (_gate)
        {
            if (_limitException is not null) return false;
            var used = _used.GetValueOrDefault(stream);
            if (used > limit - bytes) return false;
            _used[stream] = used + bytes;
            return true;
        }
    }

    public void RegisterLimit(StrategyOutputLimitException exception)
    {
        lock (_gate) _limitException ??= exception;
    }

    public void ThrowIfLimited()
    {
        var exception = LimitException;
        if (exception is not null) throw exception;
    }
}

internal sealed class SegmentedStrategyLog : IAsyncDisposable
{
    private readonly string _basePath;
    private readonly string _streamName;
    private readonly string _phase;
    private readonly long _maxBytes;
    private readonly StrategyProcessOutputBudget _budget;
    private readonly bool _diagnosticOnly;
    private FileStream? _stream;
    private long _segmentBytes;
    private int _segmentIndex;

    public SegmentedStrategyLog(string basePath, string streamName, string phase, long maxBytes, StrategyProcessOutputBudget budget, bool diagnosticOnly = false)
    {
        _basePath = basePath;
        _streamName = streamName;
        _phase = phase;
        _maxBytes = maxBytes;
        _budget = budget;
        _diagnosticOnly = diagnosticOnly;
    }

    public Task AppendLineAsync(string text, CancellationToken cancellationToken) => AppendTextAsync(text + Environment.NewLine, cancellationToken);

    public async Task AppendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (_diagnosticOnly)
        {
            if (!_budget.TryReserve(_streamName, bytes.Length, _maxBytes)) return;
        }
        else
        {
            _budget.Reserve(_streamName, bytes.Length, _maxBytes, _phase);
        }
        var offset = 0;
        while (offset < bytes.Length)
        {
            await EnsureSegmentAsync(cancellationToken);
            var available = StrategyProcessOutputLimits.SegmentBytes - _segmentBytes;
            var count = (int)Math.Min(available, (long)(bytes.Length - offset));
            await _stream!.WriteAsync(new ReadOnlyMemory<byte>(bytes, offset, count), cancellationToken);
            _segmentBytes += count;
            offset += count;
        }
    }

    private async Task EnsureSegmentAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null && _segmentBytes < StrategyProcessOutputLimits.SegmentBytes) return;
        if (_stream is not null) await _stream.DisposeAsync();
        var path = _segmentIndex == 0 ? _basePath : $"{_basePath}.part-{_segmentIndex:0000}";
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        _segmentBytes = 0;
        _segmentIndex++;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.FlushAsync();
            await _stream.DisposeAsync();
        }
        _stream = null;
    }
}

internal sealed class BoundedStrategyLineReader(StreamReader reader)
{
    private readonly StreamReader _reader = reader;
    private readonly char[] _buffer = new char[4096];
    private int _offset;
    private int _count;

    public async Task<string?> ReadLineAsync(string phase, CancellationToken cancellationToken)
    {
        var chars = new StringBuilder();
        while (true)
        {
            var current = await ReadCharAsync(cancellationToken);
            if (current < 0) return chars.Length == 0 ? null : chars.ToString();
            if (current == '\n') return chars.ToString().TrimEnd('\r');
            if (chars.Length >= StrategyProcessOutputLimits.MaxProtocolLineChars)
                throw new StrategyOutputLimitException("stdout", phase, StrategyProcessOutputLimits.MaxProtocolLineChars, chars.Length + 1, " chars");
            chars.Append((char)current);
        }
    }

    private async ValueTask<int> ReadCharAsync(CancellationToken cancellationToken)
    {
        if (_offset >= _count)
        {
            _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
            _offset = 0;
            if (_count == 0) return -1;
        }
        return _buffer[_offset++];
    }
}

internal static class StrategyProcessOutput
{

    public static async Task<string> ReadAllAsyncBounded(StreamReader reader, StrategyProcessOutputBudget budget, string phase, Process process, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) return text.ToString();
                var chunk = new string(buffer, 0, count);
                budget.Reserve("describe", Encoding.UTF8.GetByteCount(chunk), StrategyProcessOutputLimits.MaxDescribeBytes, phase);
                text.Append(chunk);
            }
        }
        catch (StrategyOutputLimitException exception)
        {
            budget.RegisterLimit(exception);
            StrategyCatalog.TryKill(process);
            throw;
        }
    }

    public static async Task DrainStandardErrorAsync(StreamReader reader, string path, StrategyProcessOutputBudget budget, Process process, string phase)
    {
        await using var log = new SegmentedStrategyLog(path, "stderr", phase, StrategyProcessOutputLimits.MaxStderrBytes, budget);
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory());
                if (count == 0) return;
                await log.AppendTextAsync(new string(buffer, 0, count), CancellationToken.None);
            }
        }
        catch (StrategyOutputLimitException exception)
        {
            budget.RegisterLimit(exception);
            StrategyCatalog.TryKill(process);
        }
    }

    public static string ReadTail(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? ".";
            var name = Path.GetFileName(path);
            var files = Directory.GetFiles(directory, name + ".part-*")
                .OrderBy(item => item, StringComparer.Ordinal)
                .Prepend(path)
                .Where(File.Exists)
                .ToArray();
            var chunks = new List<string>();
            var remaining = StrategyProcessOutputLimits.TailBytes;
            for (var index = files.Length - 1; index >= 0 && remaining > 0; index--)
            {
                using var stream = new FileStream(files[index], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var count = (int)Math.Min(remaining, stream.Length);
                stream.Seek(-count, SeekOrigin.End);
                var bytes = new byte[count];
                var read = stream.Read(bytes, 0, count);
                chunks.Insert(0, Encoding.UTF8.GetString(bytes, 0, read));
                remaining -= read;
            }
            var text = string.Concat(chunks);
            return text.Length <= StrategyProcessOutputLimits.TailCharacters ? text.Trim() : text[^StrategyProcessOutputLimits.TailCharacters..].Trim();
        }
        catch { return "<stderr unavailable>"; }
    }
}
