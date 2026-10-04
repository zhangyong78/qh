using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Qh.Core;

public sealed class DataStore
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    public string RootDirectory { get; }
    public string SnapshotsDirectory => Path.Combine(RootDirectory, "snapshots");
    private string IndexPath => Path.Combine(RootDirectory, "index.sqlite");

    public DataStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(SnapshotsDirectory);
        InitializeIndex();
    }

    public async Task<string> PublishAsync(string snapshotId, string sourcePath, string canonical, string hash, IReadOnlyList<Bar> bars, QualityReport qualityReport, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(SnapshotsDirectory, snapshotId);
        var canonicalFileText = "timestamp,symbol,exchange,period,trading_day,open,high,low,close,volume,open_interest,is_complete\n" + canonical + "\n";
        var expectedCsvHash = ComputeHash(Encoding.UTF8.GetBytes(canonicalFileText));
        var qualityJson = SerializeQualityReport(qualityReport);
        if (!HasCompleteSnapshot(directory, hash, bars.Count, expectedCsvHash))
        {
            var tempDirectory = directory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var tempCanonical = Path.Combine(tempDirectory, "bars.csv");
                var tempParquet = Path.Combine(tempDirectory, "bars.parquet");
                var tempReport = Path.Combine(tempDirectory, "source.txt");
                var tempMeta = Path.Combine(tempDirectory, "snapshot.meta");
                await File.WriteAllTextAsync(tempCanonical, canonicalFileText, Utf8NoBom, cancellationToken);
                progress?.Report("写入 Parquet 快照");
                await WriteParquetAsync(tempCanonical, tempParquet, cancellationToken);
                await File.WriteAllTextAsync(tempReport, sourcePath + Environment.NewLine, Utf8NoBom, cancellationToken);
                ValidateParquetFile(tempParquet, bars.Count);
                var tempQuality = Path.Combine(tempDirectory, "quality.json");
                await File.WriteAllTextAsync(tempQuality, qualityJson, Utf8NoBom, cancellationToken);
                var parquetHash = ComputeFileHash(tempParquet);
                var qualityHash = ComputeFileHash(tempQuality);
                await File.WriteAllTextAsync(tempMeta, $"content_hash={hash}{Environment.NewLine}csv_sha256={expectedCsvHash}{Environment.NewLine}parquet_sha256={parquetHash}{Environment.NewLine}quality_sha256={qualityHash}{Environment.NewLine}rows={bars.Count}{Environment.NewLine}session_table={CsvImporter.SessionTableVersion}{Environment.NewLine}", Utf8NoBom, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (Directory.Exists(directory))
                {
                    var quarantine = directory + ".incomplete-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                    Directory.Move(directory, quarantine);
                }
                Directory.Move(tempDirectory, directory);
            }
            catch
            {
                if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
                throw;
            }
        }

        using var connection = new SqliteConnection($"Data Source={IndexPath}");
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO snapshots(snapshot_id, directory, symbol, exchange, period, first_timestamp, last_timestamp, row_count, source_path, content_hash, created_at)
            VALUES ($id,$directory,$symbol,$exchange,$period,$first,$last,$rows,$source,$hash,$created);
            """;
        command.Parameters.AddWithValue("$id", snapshotId);
        command.Parameters.AddWithValue("$directory", directory);
        command.Parameters.AddWithValue("$symbol", bars[0].Symbol);
        command.Parameters.AddWithValue("$exchange", bars[0].Exchange);
        command.Parameters.AddWithValue("$period", bars[0].Period);
        command.Parameters.AddWithValue("$first", bars[0].Timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$last", bars[^1].Timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$rows", bars.Count);
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return directory;
    }

    public QualityReport ReadQualityReport(string snapshotId)
    {
        var directory = Path.Combine(SnapshotsDirectory, snapshotId);
        ValidateSnapshotDirectory(directory);
        var qualityPath = Path.Combine(directory, "quality.json");
        var report = JsonSerializer.Deserialize<QualityReport>(File.ReadAllText(qualityPath, Encoding.UTF8));
        return report ?? throw new InvalidDataException($"质量报告为空：{qualityPath}");
    }

    private static bool HasCompleteSnapshot(string directory, string hash, int expectedRows, string expectedCsvHash)
    {
        try
        {
            ValidateSnapshotDirectory(directory);
            var meta = ReadMetadata(Path.Combine(directory, "snapshot.meta"));
            return meta.GetValueOrDefault("content_hash") == hash &&
                   meta.GetValueOrDefault("csv_sha256") == expectedCsvHash &&
                   int.TryParse(meta.GetValueOrDefault("rows"), out var rows) && rows == expectedRows;
        }
        catch { return false; }
    }

    private static void ValidateSnapshotDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new InvalidDataException($"快照目录不存在：{directory}");
        var canonical = Path.Combine(directory, "bars.csv");
        var parquet = Path.Combine(directory, "bars.parquet");
        var source = Path.Combine(directory, "source.txt");
        var quality = Path.Combine(directory, "quality.json");
        var metaPath = Path.Combine(directory, "snapshot.meta");
        if (!File.Exists(canonical) || !File.Exists(parquet) || !File.Exists(source) || !File.Exists(quality) || !File.Exists(metaPath))
            throw new InvalidDataException($"快照文件不完整：{directory}");
        var meta = ReadMetadata(metaPath);
        if (!meta.TryGetValue("csv_sha256", out var expectedCsv) || ComputeFileHash(canonical) != expectedCsv)
            throw new InvalidDataException($"快照 CSV 摘要不匹配：{canonical}");
        if (!meta.TryGetValue("parquet_sha256", out var expectedParquet) || ComputeFileHash(parquet) != expectedParquet)
            throw new InvalidDataException($"快照 Parquet 摘要不匹配：{parquet}");
        if (!meta.TryGetValue("quality_sha256", out var expectedQuality) || ComputeFileHash(quality) != expectedQuality)
            throw new InvalidDataException($"快照质量报告摘要不匹配：{quality}");
        if (JsonSerializer.Deserialize<QualityReport>(File.ReadAllText(quality, Encoding.UTF8)) is null)
            throw new InvalidDataException($"快照质量报告结构无效：{quality}");
        if (!int.TryParse(meta.GetValueOrDefault("rows"), out var rows))
            throw new InvalidDataException($"快照行数元数据无效：{metaPath}");
        ValidateParquetFile(parquet, rows);
    }

    private static Dictionary<string, string> ReadMetadata(string path) => File.ReadAllLines(path, Encoding.UTF8)
        .Select(line => line.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    private static string SerializeQualityReport(QualityReport report) => JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

    private static string ComputeFileHash(string path) => ComputeHash(File.ReadAllBytes(path));

    private static string ComputeHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateParquetFile(string parquetPath, int expectedRows)
    {
        if (!File.Exists(parquetPath) || new FileInfo(parquetPath).Length == 0) throw new InvalidDataException($"Parquet 文件为空：{parquetPath}");
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        var escaped = parquetPath.Replace("'", "''");
        command.CommandText = $"SELECT COUNT(*) FROM read_parquet('{escaped}')";
        var actualRows = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (actualRows != expectedRows) throw new InvalidDataException($"Parquet 行数不匹配：{parquetPath}，实际 {actualRows}，预期 {expectedRows}");
        command.CommandText = $"SELECT timestamp, symbol, exchange, period, trading_day, open, high, low, close, volume, open_interest, is_complete FROM read_parquet('{escaped}') LIMIT 0";
        using var reader = command.ExecuteReader();
    }

    public IReadOnlyList<Bar> QuerySnapshot(string snapshotId, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(SnapshotsDirectory, snapshotId);
        ValidateSnapshotDirectory(directory);
        var path = Path.Combine(directory, "bars.parquet");
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT CAST(timestamp AS VARCHAR) AS timestamp_text, symbol, exchange, period, trading_day, open, high, low, close, volume, open_interest, is_complete FROM read_parquet('{path.Replace("'", "''")}') ORDER BY timestamp";
        using var reader = command.ExecuteReader();
        var rows = new List<Bar>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offsetTimestamp = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(8));
            rows.Add(new Bar(offsetTimestamp, reader.GetString(1), reader.GetString(2), reader.GetString(3), DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10), reader.GetBoolean(11)));
        }
        return rows;
    }

    public SnapshotInfo? GetLatestSnapshot()
    {
        using var connection = new SqliteConnection($"Data Source={IndexPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_id,directory,symbol,exchange,period,first_timestamp,last_timestamp,row_count,source_path,content_hash,created_at FROM snapshots ORDER BY created_at DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : null;
    }

    public IReadOnlyList<SnapshotInfo> GetSnapshots()
    {
        using var connection = new SqliteConnection($"Data Source={IndexPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_id,directory,symbol,exchange,period,first_timestamp,last_timestamp,row_count,source_path,content_hash,created_at FROM snapshots ORDER BY created_at DESC";
        using var reader = command.ExecuteReader();
        var result = new List<SnapshotInfo>();
        while (reader.Read()) result.Add(ReadSnapshot(reader));
        return result;
    }

    private static SnapshotInfo ReadSnapshot(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), reader.GetInt32(7), reader.GetString(8), reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture));

    private void InitializeIndex()
    {
        using var connection = new SqliteConnection($"Data Source={IndexPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshots(
              snapshot_id TEXT PRIMARY KEY,
              directory TEXT NOT NULL,
              symbol TEXT NOT NULL,
              exchange TEXT NOT NULL,
              period TEXT NOT NULL,
              first_timestamp TEXT NOT NULL,
              last_timestamp TEXT NOT NULL,
              row_count INTEGER NOT NULL,
              source_path TEXT NOT NULL,
              content_hash TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static async Task WriteParquetAsync(string csvPath, string parquetPath, CancellationToken cancellationToken)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        var escaped = csvPath.Replace("'", "''");
        var escapedOutput = parquetPath.Replace("'", "''");
        command.CommandText = $@"
            COPY (
              SELECT * FROM read_csv('{escaped}', header=true, columns={{
                'timestamp':'TIMESTAMPTZ','symbol':'VARCHAR','exchange':'VARCHAR','period':'VARCHAR','trading_day':'VARCHAR',
                'open':'DECIMAL(28,8)','high':'DECIMAL(28,8)','low':'DECIMAL(28,8)','close':'DECIMAL(28,8)',
                'volume':'DECIMAL(28,8)','open_interest':'DECIMAL(28,8)','is_complete':'BOOLEAN'
              }})
            ) TO '{escapedOutput}' (FORMAT PARQUET);
            ";
        command.ExecuteNonQuery();
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
    }
}
