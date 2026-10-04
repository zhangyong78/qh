using System.Text;
using System.Text.Json;

namespace Qh.Core;

public sealed class TradeRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int JournalBufferSize = 64 * 1024;
    public string RootDirectory { get; }
    public string RunsDirectory => Path.Combine(RootDirectory, "trade-runs");
    private readonly Dictionary<string, (StreamWriter Writer, int Lines)> _journalWriters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _journalLock = new();

    public TradeRunStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RunsDirectory);
    }

    public string CreateRunDirectory(string? preferredId = null)
    {
        var runId = preferredId ?? $"trd-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..31];
        var directory = Path.Combine(RunsDirectory, runId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void WriteRecord(string directory, TradeRunRecord record) => Write(directory, "run.json", record, JsonOptions);
    public TradeRunRecord ReadRecord(string runId) => Read<TradeRunRecord>(runId, "run.json");

    public void Append<T>(string directory, string fileName, T item)
    {
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        var line = JsonSerializer.Serialize(item);
        lock (_journalLock)
        {
            if (!_journalWriters.TryGetValue(path, out var journal))
            {
                journal = (new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, JournalBufferSize), new UTF8Encoding(false), JournalBufferSize), 0);
            }
            journal.Writer.WriteLine(line);
            journal.Lines++;
            if (journal.Lines % 4096 == 0) journal.Writer.Flush();
            _journalWriters[path] = journal;
        }
    }

    public void CloseRun(string directory)
    {
        var prefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        lock (_journalLock)
        {
            foreach (var path in _journalWriters.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var journal = _journalWriters[path]; journal.Writer.Flush(); journal.Writer.Dispose(); _journalWriters.Remove(path);
            }
        }
    }

    public IReadOnlyList<T> ReadMany<T>(string runId, string fileName)
    {
        var path = Path.Combine(RunsDirectory, runId, fileName);
        if (!File.Exists(path)) return [];
        return File.ReadLines(path, Encoding.UTF8).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<T>(line) ?? throw new InvalidDataException($"记录无效：{path}"))
            .ToArray();
    }

    public IReadOnlyList<EquityPoint> ReadEquitySample(string runId, int maxPoints = 1200)
    {
        var path = Path.Combine(RunsDirectory, runId, "equity.jsonl");
        if (!File.Exists(path) || maxPoints < 1) return [];
        var total = File.ReadLines(path, Encoding.UTF8).LongCount(line => !string.IsNullOrWhiteSpace(line));
        if (total == 0) return [];
        var step = Math.Max(1L, (long)Math.Ceiling(total / (double)maxPoints));
        return File.ReadLines(path, Encoding.UTF8).Where(line => !string.IsNullOrWhiteSpace(line)).Select((line, index) => (line, index))
            .Where(item => item.index % step == 0 || item.index == total - 1)
            .Select(item => JsonSerializer.Deserialize<EquityPoint>(item.line) ?? throw new InvalidDataException($"权益记录无效：{path}"))
            .ToArray();
    }

    public void ExportFillsCsv(string runId, string outputPath)
    {
        var fills = ReadMany<FillRecord>(runId, "fills.jsonl");
        var builder = new StringBuilder("run_id,intent_sequence,fill_sequence,action,direction,quantity,bar_end,trading_day,price,notional,fee,fee_type,slippage,position_effect\n");
        foreach (var fill in fills)
        {
            builder.AppendLine(string.Join(',', new[] { fill.RunId, fill.IntentSequence.ToString(), fill.FillSequence.ToString(), fill.Action, fill.Direction,
                fill.Quantity.ToString(), fill.BarEnd.ToString("O"), fill.TradingDay.ToString("O"), fill.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fill.Notional.ToString(System.Globalization.CultureInfo.InvariantCulture), fill.Fee.ToString(System.Globalization.CultureInfo.InvariantCulture), fill.FeeType,
                fill.Slippage.ToString(System.Globalization.CultureInfo.InvariantCulture), fill.PositionEffect }.Select(Escape)));
        }
        File.WriteAllText(Path.GetFullPath(outputPath), builder.ToString(), new UTF8Encoding(false));
    }

    public void ExportEquityCsv(string runId, string outputPath)
    {
        var builder = new StringBuilder("run_id,bar_end,equity,realized_gross_pnl,fees,floating_pnl,drawdown\n");
        foreach (var point in ReadMany<EquityPoint>(runId, "equity.jsonl"))
            builder.AppendLine(string.Join(',', new[] { point.RunId, point.BarEnd.ToString("O"), point.Equity.ToString(System.Globalization.CultureInfo.InvariantCulture), point.RealizedGrossPnl.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Fees.ToString(System.Globalization.CultureInfo.InvariantCulture), point.FloatingPnl.ToString(System.Globalization.CultureInfo.InvariantCulture), point.Drawdown.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Select(Escape)));
        File.WriteAllText(Path.GetFullPath(outputPath), builder.ToString(), new UTF8Encoding(false));
    }

    public void ExportSummaryJson(string runId, string outputPath) => File.Copy(Path.Combine(RunsDirectory, runId, "run.json"), Path.GetFullPath(outputPath), overwrite: true);

    private T Read<T>(string runId, string fileName)
    {
        var path = Path.Combine(RunsDirectory, runId, fileName);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到交易运行记录", path);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("交易运行记录为空");
    }

    private static void Write<T>(string directory, string fileName, T item, JsonSerializerOptions options) =>
        File.WriteAllText(Path.Combine(directory, fileName), JsonSerializer.Serialize(item, options), new UTF8Encoding(false));

    private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + value.Replace("\"", "\"\"") + '"' : value;
}
