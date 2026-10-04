using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Qh.Core;

public sealed class SignalRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new();
    public string RootDirectory { get; }
    public string RunsDirectory => Path.Combine(RootDirectory, "runs");

    public SignalRunStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RunsDirectory);
    }

    public string CreateRunDirectory(string? preferredId = null)
    {
        var runId = preferredId ?? $"sig-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..31];
        var directory = Path.Combine(RunsDirectory, runId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void WriteRecord(string runDirectory, SignalRunRecord record) =>
        File.WriteAllText(Path.Combine(runDirectory, "run.json"), JsonSerializer.Serialize(record, JsonOptions), new UTF8Encoding(false));

    public SignalRunRecord ReadRecord(string runId)
    {
        var path = Path.Combine(RunsDirectory, runId, "run.json");
        if (!File.Exists(path)) throw new FileNotFoundException("找不到信号运行记录", path);
        return JsonSerializer.Deserialize<SignalRunRecord>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("信号运行记录为空");
    }

    public void AppendSignal(string runDirectory, SignalEvent signal)
    {
        var line = JsonSerializer.Serialize(signal, JsonLineOptions);
        File.AppendAllText(Path.Combine(runDirectory, "signals.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
    }

    public IReadOnlyList<SignalEvent> ReadSignals(string runId)
    {
        var path = Path.Combine(RunsDirectory, runId, "signals.jsonl");
        if (!File.Exists(path)) return [];
        var result = new List<SignalEvent>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            result.Add(JsonSerializer.Deserialize<SignalEvent>(line, JsonOptions)
                ?? throw new InvalidDataException($"信号记录无效：{path}"));
        }
        return result;
    }

    public IReadOnlyList<SignalEvent> Filter(string runId, string? category = null, string? direction = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        ReadSignals(runId).Where(signal =>
            (category is null || string.Equals(signal.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (direction is null || string.Equals(signal.Direction, direction, StringComparison.OrdinalIgnoreCase)) &&
            (from is null || signal.BarEnd >= from) && (to is null || signal.BarEnd < to)).ToArray();

    public void ExportCsv(string runId, string outputPath, string? category = null, string? direction = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        ExportCsv(Filter(runId, category, direction, from, to), outputPath);
    }

    public void ExportCsv(IEnumerable<SignalEvent> signals, string outputPath)
    {
        var builder = new StringBuilder("run_id,snapshot_id,bar_end,bar_start,symbol,category,direction,description,reference_price,is_formal,sequence\n");
        foreach (var signal in signals)
        {
            var values = new[]
            {
                signal.RunId, signal.SnapshotId, signal.BarEnd.ToString("O", CultureInfo.InvariantCulture),
                signal.BarStart.ToString("O", CultureInfo.InvariantCulture), signal.Symbol, signal.Category,
                signal.Direction, signal.Description, signal.ReferencePrice?.ToString(CultureInfo.InvariantCulture) ?? "",
                signal.IsFormal ? "true" : "false", signal.Sequence.ToString(CultureInfo.InvariantCulture)
            };
            builder.AppendLine(string.Join(',', values.Select(Escape)));
        }
        File.WriteAllText(Path.GetFullPath(outputPath), builder.ToString(), new UTF8Encoding(false));
    }

    private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + value.Replace("\"", "\"\"") + '"' : value;
}
