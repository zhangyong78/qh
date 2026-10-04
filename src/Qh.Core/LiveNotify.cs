using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace Qh.Core;

public enum LiveReminderState
{
    Stopped,
    Starting,
    WarmingUp,
    Running,
    Paused,
    Recovering,
    Failed
}

public enum LiveSignalSourcePhase
{
    Live,
    Warmup,
    Recovery
}

public enum LiveNotificationChannel
{
    Desktop,
    Sound
}

public sealed record LiveReminderInstance(
    string InstanceId,
    string Symbol,
    int DurationSeconds,
    string StrategyName,
    string StrategyVersion,
    string StrategySha256,
    LiveReminderState State,
    bool DesktopNotificationsEnabled,
    bool SoundEnabled,
    int CooldownSeconds,
    DateTimeOffset? NotificationsPausedUntil,
    DateTimeOffset? LastCompleteBarEnd,
    DateTimeOffset? LastCalculationAt,
    DateTimeOffset? LastSignalAt,
    string? LastSignalSummary,
    DateTimeOffset UpdatedAt);

public sealed record LiveSignalCandidate(
    string Category,
    string Direction,
    string Description,
    decimal? ReferencePrice = null);

public sealed record LiveSignalLedgerRecord(
    string SignalId,
    string InstanceId,
    string StrategyVersion,
    string Symbol,
    int DurationSeconds,
    DateTimeOffset BarEnd,
    string Category,
    string Direction,
    string Description,
    decimal? ReferencePrice,
    LiveSignalSourcePhase SourcePhase,
    bool NotificationAllowed,
    DateTimeOffset CreatedAt);

public sealed record LiveNotificationAttempt(
    string AttemptId,
    string SignalId,
    LiveNotificationChannel Channel,
    string Status,
    string? Reason,
    DateTimeOffset? AttemptedAt,
    DateTimeOffset UpdatedAt);

public sealed record LiveNotificationDispatch(
    LiveSignalLedgerRecord Signal,
    IReadOnlyList<LiveNotificationAttempt> Attempts);

public sealed record LiveStrategyConfig(
    string InstanceId,
    string StrategyPath,
    string StrategyName,
    string StrategyVersion,
    string StrategySha256,
    string ParametersJson,
    string ParametersSummary,
    int WarmupBarsRequired,
    int WarmupBarsProcessed,
    string Status,
    string StrategyHistoryJson,
    DateTimeOffset? LastBarEnd,
    string? LastError,
    DateTimeOffset UpdatedAt);

public sealed record LiveStrategyEvaluationLedger(
    string InstanceId,
    DateTimeOffset BarEnd,
    LiveSignalSourcePhase SourcePhase,
    IReadOnlyList<LiveSignalCandidate> Candidates,
    DateTimeOffset EvaluatedAt);

public interface ILiveNotificationSink
{
    void ShowDesktop(LiveReminderInstance instance, LiveSignalLedgerRecord signal);
    void PlaySound(LiveReminderInstance instance, LiveSignalLedgerRecord signal);
}

public sealed class LiveNotifyStore
{
    private readonly string _databasePath;

    public LiveNotifyStore(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        _databasePath = Path.Combine(root, "live-notify.sqlite");
        Initialize();
    }

    public IReadOnlyList<LiveReminderInstance> GetInstances()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT instance_id,symbol,duration_seconds,strategy_name,strategy_version,strategy_sha256,state,desktop_enabled,sound_enabled,cooldown_seconds,paused_until,last_bar_end,last_calculation,last_signal_at,last_signal_summary,updated_at FROM live_instances ORDER BY updated_at DESC";
        using var reader = command.ExecuteReader();
        var result = new List<LiveReminderInstance>();
        while (reader.Read()) result.Add(ReadInstance(reader));
        return result;
    }

    public LiveReminderInstance? GetInstance(string instanceId) => GetInstances().FirstOrDefault(item => item.InstanceId == instanceId);

    public bool TryCreateOrUpdateInstance(LiveReminderInstance instance, out string error)
    {
        error = "";
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM live_instances WHERE state IN ('Starting','WarmingUp','Running','Paused','Recovering') AND instance_id <> $id";
            count.Parameters.AddWithValue("$id", instance.InstanceId);
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= 5)
            {
                error = "最多同时保留 5 个活跃提醒实例";
                return false;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO live_instances(instance_id,symbol,duration_seconds,strategy_name,strategy_version,strategy_sha256,state,desktop_enabled,sound_enabled,cooldown_seconds,paused_until,last_bar_end,last_calculation,last_signal_at,last_signal_summary,created_at,updated_at)
                VALUES($id,$symbol,$duration,$name,$version,$sha,$state,$desktop,$sound,$cooldown,$paused,$bar,$calc,$signal,$summary,$now,$now)
                ON CONFLICT(instance_id) DO UPDATE SET symbol=$symbol,duration_seconds=$duration,strategy_name=$name,strategy_version=$version,strategy_sha256=$sha,state=$state,desktop_enabled=$desktop,sound_enabled=$sound,cooldown_seconds=$cooldown,paused_until=$paused,updated_at=$now;
                """;
            AddInstanceParameters(command, instance, DateTimeOffset.UtcNow);
            command.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
        catch (Exception ex)
        {
            try { transaction.Rollback(); } catch { }
            error = ex.Message;
            return false;
        }
    }

    public void UpdateInstanceState(string instanceId, LiveReminderState state, DateTimeOffset? lastBarEnd = null, DateTimeOffset? lastCalculation = null, DateTimeOffset? lastSignalAt = null, string? lastSignalSummary = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE live_instances SET state=$state,last_bar_end=COALESCE($bar,last_bar_end),last_calculation=COALESCE($calc,last_calculation),last_signal_at=COALESCE($signal,last_signal_at),last_signal_summary=COALESCE($summary,last_signal_summary),updated_at=$now WHERE instance_id=$id";
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$bar", lastBarEnd?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$calc", lastCalculation?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$signal", lastSignalAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$summary", lastSignalSummary ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", instanceId);
        command.ExecuteNonQuery();
    }

    public void SetNotificationOptions(string instanceId, bool desktopEnabled, bool soundEnabled, DateTimeOffset? pausedUntil)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE live_instances SET desktop_enabled=$desktop,sound_enabled=$sound,paused_until=$paused,updated_at=$now WHERE instance_id=$id";
        command.Parameters.AddWithValue("$desktop", desktopEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$sound", soundEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$paused", pausedUntil?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", instanceId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LiveNotificationDispatch> RecordSignals(string instanceId, LiveKline bar, IEnumerable<LiveSignalCandidate> candidates, LiveSignalSourcePhase sourcePhase)
    {
        var instance = GetInstance(instanceId) ?? throw new InvalidOperationException($"提醒实例不存在：{instanceId}");
        if (instance.State is LiveReminderState.Stopped or LiveReminderState.Failed)
        {
            throw new InvalidOperationException($"提醒实例已停止或失败，不能继续记录信号：{instanceId}");
        }
        var now = DateTimeOffset.UtcNow;
        var dispatches = new List<LiveNotificationDispatch>();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var candidate in candidates)
        {
            var signalId = Guid.NewGuid().ToString("N");
            using var signal = connection.CreateCommand();
            signal.Transaction = transaction;
            signal.CommandText = """
                INSERT OR IGNORE INTO live_signals(signal_id,instance_id,strategy_version,symbol,duration_seconds,bar_end,category,direction,description,reference_price,source_phase,notification_allowed,created_at)
                VALUES($signal_id,$instance_id,$version,$symbol,$duration,$bar_end,$category,$direction,$description,$reference,$source,$allowed,$created)
                """;
            signal.Parameters.AddWithValue("$signal_id", signalId);
            signal.Parameters.AddWithValue("$instance_id", instance.InstanceId);
            signal.Parameters.AddWithValue("$version", instance.StrategyVersion);
            signal.Parameters.AddWithValue("$symbol", bar.Symbol);
            signal.Parameters.AddWithValue("$duration", bar.DurationSeconds);
            signal.Parameters.AddWithValue("$bar_end", bar.BarEnd.ToString("O", CultureInfo.InvariantCulture));
            signal.Parameters.AddWithValue("$category", candidate.Category);
            signal.Parameters.AddWithValue("$direction", candidate.Direction);
            signal.Parameters.AddWithValue("$description", candidate.Description);
            signal.Parameters.AddWithValue("$reference", candidate.ReferencePrice ?? (object)DBNull.Value);
            signal.Parameters.AddWithValue("$source", sourcePhase.ToString());
            var paused = instance.NotificationsPausedUntil is { } until && until > now;
            var coolingDown = IsCoolingDown(connection, transaction, instance, bar, candidate);
            var allowed = sourcePhase == LiveSignalSourcePhase.Live && !paused && !coolingDown;
            signal.Parameters.AddWithValue("$allowed", allowed ? 1 : 0);
            signal.Parameters.AddWithValue("$created", now.ToString("O", CultureInfo.InvariantCulture));
            if (signal.ExecuteNonQuery() != 1) continue;

            var stored = new LiveSignalLedgerRecord(signalId, instance.InstanceId, instance.StrategyVersion, bar.Symbol, bar.DurationSeconds, bar.BarEnd, candidate.Category, candidate.Direction, candidate.Description, candidate.ReferencePrice, sourcePhase, allowed, now);
            var attempts = new List<LiveNotificationAttempt>
            {
                InsertAttempt(connection, transaction, stored, LiveNotificationChannel.Desktop, allowed && instance.DesktopNotificationsEnabled ? "pending" : SuppressedReason(sourcePhase, paused, coolingDown, instance.DesktopNotificationsEnabled), now),
                InsertAttempt(connection, transaction, stored, LiveNotificationChannel.Sound, allowed && instance.SoundEnabled ? "pending" : SuppressedReason(sourcePhase, paused, coolingDown, instance.SoundEnabled), now)
            };
            dispatches.Add(new(stored, attempts));
        }
        transaction.Commit();
        var latest = dispatches.LastOrDefault();
        UpdateInstanceState(instanceId, instance.State, bar.BarEnd, now, latest?.Signal.BarEnd, latest is null ? null : $"{latest.Signal.Direction} · {latest.Signal.Description}");
        return dispatches;
    }

    public bool TryClaimAttempt(string attemptId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE live_notification_attempts SET status='in_flight',updated_at=$now WHERE attempt_id=$id AND status='pending'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", attemptId);
        return command.ExecuteNonQuery() == 1;
    }

    public void CompleteAttempt(string attemptId, bool success, string? reason = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE live_notification_attempts SET status=$status,reason=$reason,attempted_at=$attempted,updated_at=$now WHERE attempt_id=$id AND status='in_flight'";
        command.Parameters.AddWithValue("$status", success ? "attempted" : "failed");
        command.Parameters.AddWithValue("$reason", reason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$attempted", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", attemptId);
        command.ExecuteNonQuery();
    }

    public int RecoverInFlightAttempts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE live_notification_attempts SET status='failed',reason='进程重启后未重试，桌面提醒可能漏发',updated_at=$now WHERE status='in_flight'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<LiveNotificationAttempt> GetAttempts(string? instanceId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.attempt_id,a.signal_id,a.channel,a.status,a.reason,a.attempted_at,a.updated_at FROM live_notification_attempts a LEFT JOIN live_signals s ON s.signal_id=a.signal_id WHERE ($instance IS NULL OR s.instance_id=$instance) ORDER BY a.updated_at";
        command.Parameters.AddWithValue("$instance", instanceId ?? (object)DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<LiveNotificationAttempt>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), Enum.Parse<LiveNotificationChannel>(reader.GetString(2)), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        return result;
    }

    public int GetSignalCount(string instanceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM live_signals WHERE instance_id=$id";
        command.Parameters.AddWithValue("$id", instanceId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public LiveStrategyConfig? GetStrategyConfig(string instanceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT instance_id,strategy_path,strategy_name,strategy_version,strategy_sha256,parameters_json,parameters_summary,warmup_bars_required,warmup_bars_processed,status,history_json,last_bar_end,last_error,updated_at FROM live_strategy_configs WHERE instance_id=$id";
        command.Parameters.AddWithValue("$id", instanceId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStrategyConfig(reader) : null;
    }

    public void ConfigureStrategy(string instanceId, string strategyPath, string strategyName, string strategyVersion, string strategySha256, string parametersJson, string parametersSummary, int warmupBarsRequired)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var existing = GetStrategyConfig(instanceId);
        var same = existing is not null && string.Equals(existing.StrategyPath, strategyPath, StringComparison.OrdinalIgnoreCase) && existing.StrategyVersion == strategyVersion && existing.StrategySha256 == strategySha256 && existing.ParametersJson == parametersJson && existing.WarmupBarsRequired == warmupBarsRequired;
        if (!same)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM live_strategy_evaluations WHERE instance_id=$id";
            delete.Parameters.AddWithValue("$id", instanceId);
            delete.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO live_strategy_configs(instance_id,strategy_path,strategy_name,strategy_version,strategy_sha256,parameters_json,parameters_summary,warmup_bars_required,warmup_bars_processed,status,history_json,last_bar_end,last_error,updated_at)
            VALUES($id,$path,$name,$version,$sha,$parameters,$summary,$warmup,$processed,$status,$history,$bar,$error,$now)
            ON CONFLICT(instance_id) DO UPDATE SET strategy_path=$path,strategy_name=$name,strategy_version=$version,strategy_sha256=$sha,parameters_json=$parameters,parameters_summary=$summary,warmup_bars_required=$warmup,warmup_bars_processed=CASE WHEN $same THEN warmup_bars_processed ELSE 0 END,status='loaded',history_json=CASE WHEN $same THEN history_json ELSE '[]' END,last_bar_end=CASE WHEN $same THEN last_bar_end ELSE NULL END,last_error=NULL,updated_at=$now;
            """;
        command.Parameters.AddWithValue("$id", instanceId);
        command.Parameters.AddWithValue("$path", strategyPath);
        command.Parameters.AddWithValue("$name", strategyName);
        command.Parameters.AddWithValue("$version", strategyVersion);
        command.Parameters.AddWithValue("$sha", strategySha256);
        command.Parameters.AddWithValue("$parameters", parametersJson);
        command.Parameters.AddWithValue("$summary", parametersSummary);
        command.Parameters.AddWithValue("$warmup", warmupBarsRequired);
        command.Parameters.AddWithValue("$processed", same && existing is not null ? existing.WarmupBarsProcessed : 0);
        command.Parameters.AddWithValue("$status", "loaded");
        command.Parameters.AddWithValue("$history", same && existing is not null ? existing.StrategyHistoryJson : "[]");
        command.Parameters.AddWithValue("$bar", same && existing?.LastBarEnd is { } bar ? bar.ToString("O", CultureInfo.InvariantCulture) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$same", same ? 1 : 0);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void SetStrategyStatus(string instanceId, string status, string? error = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE live_strategy_configs SET status=$status,last_error=$error,updated_at=$now WHERE instance_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", instanceId);
        command.ExecuteNonQuery();
    }

    public void RecordStrategyProgress(string instanceId, string status, int processedBars, IReadOnlyList<LiveKline> history, DateTimeOffset? lastBarEnd, string? error = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE live_strategy_configs SET status=$status,warmup_bars_processed=$processed,history_json=$history,last_bar_end=$bar,last_error=$error,updated_at=$now WHERE instance_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$processed", processedBars);
        command.Parameters.AddWithValue("$history", JsonSerializer.Serialize(history));
        command.Parameters.AddWithValue("$bar", lastBarEnd?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", error ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", instanceId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LiveKline> GetStrategyHistory(string instanceId)
    {
        var config = GetStrategyConfig(instanceId);
        if (config is null || string.IsNullOrWhiteSpace(config.StrategyHistoryJson)) return [];
        return JsonSerializer.Deserialize<List<LiveKline>>(config.StrategyHistoryJson) ?? [];
    }

    public LiveStrategyEvaluationLedger? GetStrategyEvaluation(string instanceId, DateTimeOffset barEnd)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT instance_id,bar_end,source_phase,candidates_json,evaluated_at FROM live_strategy_evaluations WHERE instance_id=$id AND bar_end=$bar";
        command.Parameters.AddWithValue("$id", instanceId);
        command.Parameters.AddWithValue("$bar", barEnd.ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), Enum.Parse<LiveSignalSourcePhase>(reader.GetString(2)), JsonSerializer.Deserialize<List<LiveSignalCandidate>>(reader.GetString(3)) ?? [], DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
    }

    public void RecordStrategyEvaluation(string instanceId, LiveKline bar, LiveSignalSourcePhase sourcePhase, IReadOnlyList<LiveSignalCandidate> candidates)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO live_strategy_evaluations(instance_id,bar_end,source_phase,candidates_json,evaluated_at) VALUES($id,$bar,$source,$candidates,$now)";
        command.Parameters.AddWithValue("$id", instanceId);
        command.Parameters.AddWithValue("$bar", bar.BarEnd.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", sourcePhase.ToString());
        command.Parameters.AddWithValue("$candidates", JsonSerializer.Serialize(candidates));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LiveSignalLedgerRecord> GetSignals(string? instanceId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT signal_id,instance_id,strategy_version,symbol,duration_seconds,bar_end,category,direction,description,reference_price,source_phase,notification_allowed,created_at FROM live_signals WHERE ($instance IS NULL OR instance_id=$instance) ORDER BY bar_end DESC,created_at DESC";
        command.Parameters.AddWithValue("$instance", instanceId ?? (object)DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<LiveSignalLedgerRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture), Enum.Parse<LiveSignalSourcePhase>(reader.GetString(10)), reader.GetInt64(11) != 0, DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture)));
        return result;
    }

    private static string SuppressedReason(LiveSignalSourcePhase source, bool paused, bool coolingDown, bool enabled) => source != LiveSignalSourcePhase.Live ? "suppressed_recovery_or_warmup" : paused ? "suppressed_paused" : coolingDown ? "suppressed_cooldown" : enabled ? "suppressed_disabled" : "suppressed_muted";

    private static bool IsCoolingDown(SqliteConnection connection, SqliteTransaction transaction, LiveReminderInstance instance, LiveKline bar, LiveSignalCandidate candidate)
    {
        if (instance.CooldownSeconds <= 0) return false;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM live_signals WHERE instance_id=$instance AND category=$category AND direction=$direction AND bar_end > $since AND bar_end <= $bar LIMIT 1";
        command.Parameters.AddWithValue("$instance", instance.InstanceId);
        command.Parameters.AddWithValue("$category", candidate.Category);
        command.Parameters.AddWithValue("$direction", candidate.Direction);
        command.Parameters.AddWithValue("$since", bar.BarEnd.AddSeconds(-instance.CooldownSeconds).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$bar", bar.BarEnd.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteScalar() is not null;
    }

    private static LiveNotificationAttempt InsertAttempt(SqliteConnection connection, SqliteTransaction transaction, LiveSignalLedgerRecord signal, LiveNotificationChannel channel, string status, DateTimeOffset now)
    {
        var attemptId = Guid.NewGuid().ToString("N");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO live_notification_attempts(attempt_id,signal_id,channel,status,reason,attempted_at,updated_at) VALUES($id,$signal,$channel,$status,$reason,NULL,$now)";
        command.Parameters.AddWithValue("$id", attemptId);
        command.Parameters.AddWithValue("$signal", signal.SignalId);
        command.Parameters.AddWithValue("$channel", channel.ToString());
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$reason", status.StartsWith("suppressed_", StringComparison.Ordinal) ? status : (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return new(attemptId, signal.SignalId, channel, status, status.StartsWith("suppressed_", StringComparison.Ordinal) ? status : null, null, now);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    private static void AddInstanceParameters(SqliteCommand command, LiveReminderInstance instance, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", instance.InstanceId);
        command.Parameters.AddWithValue("$symbol", instance.Symbol);
        command.Parameters.AddWithValue("$duration", instance.DurationSeconds);
        command.Parameters.AddWithValue("$name", instance.StrategyName);
        command.Parameters.AddWithValue("$version", instance.StrategyVersion);
        command.Parameters.AddWithValue("$sha", instance.StrategySha256);
        command.Parameters.AddWithValue("$state", instance.State.ToString());
        command.Parameters.AddWithValue("$desktop", instance.DesktopNotificationsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$sound", instance.SoundEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$cooldown", instance.CooldownSeconds);
        command.Parameters.AddWithValue("$paused", instance.NotificationsPausedUntil?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$bar", instance.LastCompleteBarEnd?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$calc", instance.LastCalculationAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$signal", instance.LastSignalAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$summary", instance.LastSignalSummary ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
    }

    private static LiveReminderInstance ReadInstance(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), Enum.Parse<LiveReminderState>(reader.GetString(6)), reader.GetInt64(7) != 0, reader.GetInt64(8) != 0, reader.GetInt32(9),
        reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture), reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture), reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture), reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture), reader.IsDBNull(14) ? null : reader.GetString(14), DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture));

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS live_instances(
              instance_id TEXT PRIMARY KEY, symbol TEXT NOT NULL, duration_seconds INTEGER NOT NULL,
              strategy_name TEXT NOT NULL, strategy_version TEXT NOT NULL, strategy_sha256 TEXT NOT NULL,
              state TEXT NOT NULL, desktop_enabled INTEGER NOT NULL, sound_enabled INTEGER NOT NULL, cooldown_seconds INTEGER NOT NULL DEFAULT 0,
              paused_until TEXT NULL, last_bar_end TEXT NULL, last_calculation TEXT NULL,
              last_signal_at TEXT NULL, last_signal_summary TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS live_signals(
              signal_id TEXT PRIMARY KEY, instance_id TEXT NOT NULL, strategy_version TEXT NOT NULL,
              symbol TEXT NOT NULL, duration_seconds INTEGER NOT NULL, bar_end TEXT NOT NULL,
              category TEXT NOT NULL, direction TEXT NOT NULL, description TEXT NOT NULL,
              reference_price NUMERIC NULL, source_phase TEXT NOT NULL, notification_allowed INTEGER NOT NULL, created_at TEXT NOT NULL,
              UNIQUE(instance_id,strategy_version,symbol,duration_seconds,bar_end,category,direction)
            );
            CREATE TABLE IF NOT EXISTS live_notification_attempts(
              attempt_id TEXT PRIMARY KEY, signal_id TEXT NOT NULL, channel TEXT NOT NULL,
              status TEXT NOT NULL, reason TEXT NULL, attempted_at TEXT NULL, updated_at TEXT NOT NULL,
              UNIQUE(signal_id,channel)
            );
            CREATE INDEX IF NOT EXISTS idx_live_signals_instance_bar ON live_signals(instance_id,bar_end);
            CREATE TABLE IF NOT EXISTS live_strategy_configs(
              instance_id TEXT PRIMARY KEY, strategy_path TEXT NOT NULL, strategy_name TEXT NOT NULL,
              strategy_version TEXT NOT NULL, strategy_sha256 TEXT NOT NULL, parameters_json TEXT NOT NULL,
              parameters_summary TEXT NOT NULL, warmup_bars_required INTEGER NOT NULL, warmup_bars_processed INTEGER NOT NULL,
              status TEXT NOT NULL, history_json TEXT NOT NULL, last_bar_end TEXT NULL, last_error TEXT NULL, updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS live_strategy_evaluations(
              instance_id TEXT NOT NULL, bar_end TEXT NOT NULL, source_phase TEXT NOT NULL,
              candidates_json TEXT NOT NULL, evaluated_at TEXT NOT NULL,
              PRIMARY KEY(instance_id,bar_end)
            );
            """;
        command.ExecuteNonQuery();
        try
        {
            using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE live_instances ADD COLUMN cooldown_seconds INTEGER NOT NULL DEFAULT 0";
            migration.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    private static LiveStrategyConfig ReadStrategyConfig(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture), reader.IsDBNull(12) ? null : reader.GetString(12), DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture));
}

public sealed class LiveReminderEngine
{
    private readonly LiveNotifyStore _store;
    private readonly ILiveNotificationSink _sink;

    public LiveReminderEngine(LiveNotifyStore store, ILiveNotificationSink sink)
    {
        _store = store;
        _sink = sink;
        _store.RecoverInFlightAttempts();
    }

    public async Task<IReadOnlyList<LiveNotificationDispatch>> AcceptCompletedBarAsync(string instanceId, LiveKline bar, IReadOnlyList<LiveSignalCandidate> candidates, CancellationToken cancellationToken, LiveSignalSourcePhase? phaseOverride = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var instance = _store.GetInstance(instanceId) ?? throw new InvalidOperationException($"提醒实例不存在：{instanceId}");
        if (instance.State is LiveReminderState.Stopped or LiveReminderState.Failed)
        {
            throw new InvalidOperationException($"提醒实例已停止或失败，不能继续消费完成K线：{instanceId}");
        }
        var phase = phaseOverride ?? (bar.IsRecovery ? LiveSignalSourcePhase.Recovery : LiveSignalSourcePhase.Live);
        var dispatches = _store.RecordSignals(instanceId, bar, candidates, phase);
        foreach (var dispatch in dispatches)
        {
            foreach (var attempt in dispatch.Attempts.Where(item => item.Status == "pending"))
            {
                if (!_store.TryClaimAttempt(attempt.AttemptId)) continue;
                try
                {
                    if (attempt.Channel == LiveNotificationChannel.Desktop) _sink.ShowDesktop(instance, dispatch.Signal);
                    else _sink.PlaySound(instance, dispatch.Signal);
                    _store.CompleteAttempt(attempt.AttemptId, true);
                }
                catch (Exception ex)
                {
                    _store.CompleteAttempt(attempt.AttemptId, false, ex.GetType().Name);
                }
            }
        }
        await Task.CompletedTask;
        return dispatches;
    }
}

public static class G5ANotifyFixtureVerifier
{
    public static JsonDocument Run(string rootDirectory)
    {
        if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, true);
        Directory.CreateDirectory(rootDirectory);
        var now = DateTimeOffset.Parse("2026-10-01T09:00:00+08:00", CultureInfo.InvariantCulture);
        var instance = new LiveReminderInstance("fixture-instance", "TEST.FIXTURE", 60, "Fixture SMA", "1.0", "fixture-sha", LiveReminderState.Running, true, true, 0, null, null, null, null, null, now);
        var store = new LiveNotifyStore(rootDirectory);
        var created = store.TryCreateOrUpdateInstance(instance, out _);
        var bar = new LiveKline("TEST.FIXTURE", 60, now, now.AddMinutes(1), 100, 101, 99, 100, 1, 1, true, 1, false);
        var candidate = new LiveSignalCandidate("cross_up", "long", "fixture up", 100);
        var first = store.RecordSignals(instance.InstanceId, bar, [candidate], LiveSignalSourcePhase.Live);
        var duplicate = store.RecordSignals(instance.InstanceId, bar, [candidate], LiveSignalSourcePhase.Live);
        var restarted = new LiveNotifyStore(rootDirectory);
        var afterRestart = restarted.RecordSignals(instance.InstanceId, bar, [candidate], LiveSignalSourcePhase.Live);
        var recovery = restarted.RecordSignals(instance.InstanceId, bar with { BarEnd = bar.BarEnd.AddMinutes(1), ProviderSequence = 2, IsRecovery = true }, [candidate], LiveSignalSourcePhase.Recovery);
        restarted.SetNotificationOptions(instance.InstanceId, true, false, null);
        var muted = restarted.RecordSignals(instance.InstanceId, bar with { BarEnd = bar.BarEnd.AddMinutes(2), ProviderSequence = 3 }, [candidate], LiveSignalSourcePhase.Live);
        restarted.SetNotificationOptions(instance.InstanceId, true, false, DateTimeOffset.UtcNow.AddHours(1));
        var paused = restarted.RecordSignals(instance.InstanceId, bar with { BarEnd = bar.BarEnd.AddMinutes(3), ProviderSequence = 4 }, [candidate], LiveSignalSourcePhase.Live);
        var crashStore = new LiveNotifyStore(Path.Combine(rootDirectory, "crash-window"));
        var crashInstance = instance with { InstanceId = "crash-instance" };
        crashStore.TryCreateOrUpdateInstance(crashInstance, out _);
        var crashDispatch = crashStore.RecordSignals(crashInstance.InstanceId, bar, [candidate], LiveSignalSourcePhase.Live).Single();
        var claimed = crashStore.TryClaimAttempt(crashDispatch.Attempts.First(item => item.Channel == LiveNotificationChannel.Desktop).AttemptId);
        var afterCrash = new LiveNotifyStore(Path.Combine(rootDirectory, "crash-window"));
        var recoveredInFlight = afterCrash.RecoverInFlightAttempts();
        var engine = new LiveReminderEngine(afterCrash, new FixtureNotificationSink());
        var engineDispatch = engine.AcceptCompletedBarAsync(crashInstance.InstanceId, bar with { BarEnd = bar.BarEnd.AddMinutes(5), ProviderSequence = 5 }, [candidate], CancellationToken.None).GetAwaiter().GetResult();
        var stoppedStore = new LiveNotifyStore(Path.Combine(rootDirectory, "stopped-state"));
        var stoppedCreated = stoppedStore.TryCreateOrUpdateInstance(instance with { InstanceId = "stopped", State = LiveReminderState.Stopped }, out _);
        var failedCreated = stoppedStore.TryCreateOrUpdateInstance(instance with { InstanceId = "failed", State = LiveReminderState.Failed }, out _);
        var stoppedRejected = false;
        var failedRejected = false;
        var stoppedEngine = new LiveReminderEngine(stoppedStore, new FixtureNotificationSink());
        try { stoppedEngine.AcceptCompletedBarAsync("stopped", bar, [candidate], CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { stoppedRejected = true; }
        try { stoppedEngine.AcceptCompletedBarAsync("failed", bar, [candidate], CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { failedRejected = true; }
        var cooldownStore = new LiveNotifyStore(Path.Combine(rootDirectory, "cooldown"));
        var cooldownInstance = instance with { InstanceId = "cooldown", CooldownSeconds = 60 };
        var cooldownCreated = cooldownStore.TryCreateOrUpdateInstance(cooldownInstance, out _);
        var cooldownFirst = cooldownStore.RecordSignals("cooldown", bar, [candidate], LiveSignalSourcePhase.Live);
        var cooldownSameDirection = cooldownStore.RecordSignals("cooldown", bar with { BarEnd = bar.BarEnd.AddSeconds(30), ProviderSequence = 6 }, [candidate], LiveSignalSourcePhase.Live);
        var reverse = new LiveSignalCandidate("cross_up", "short", "fixture reverse", 100);
        var cooldownReverse = cooldownStore.RecordSignals("cooldown", bar with { BarEnd = bar.BarEnd.AddSeconds(45), ProviderSequence = 7 }, [reverse], LiveSignalSourcePhase.Live);
        var failedCountCreated = restarted.TryCreateOrUpdateInstance(instance with { InstanceId = "failed-count", State = LiveReminderState.Failed }, out _);
        var stoppedCountCreated = restarted.TryCreateOrUpdateInstance(instance with { InstanceId = "stopped-count", State = LiveReminderState.Stopped }, out _);
        var five = Enumerable.Range(1, 4).Select(index => restarted.TryCreateOrUpdateInstance(instance with { InstanceId = "fixture-" + index, State = LiveReminderState.Running }, out _)).All(item => item);
        var sixth = restarted.TryCreateOrUpdateInstance(instance with { InstanceId = "fixture-sixth", State = LiveReminderState.Running }, out _);
        var attempts = restarted.GetAttempts();
        var checks = new Dictionary<string, object?>
        {
            ["created"] = created,
            ["duplicate_signal_dedup"] = first.Count == 1 && duplicate.Count == 0 && restarted.GetSignalCount(instance.InstanceId) == 4,
            ["restart_signal_dedup"] = afterRestart.Count == 0,
            ["recovery_suppressed"] = recovery.Count == 1 && recovery[0].Attempts.All(item => item.Status == "suppressed_recovery_or_warmup"),
            ["paused_and_muted_suppressed"] = muted.Count == 1 && muted[0].Attempts.Any(item => item.Channel == LiveNotificationChannel.Desktop && item.Status == "pending") && muted[0].Attempts.Any(item => item.Channel == LiveNotificationChannel.Sound && item.Status == "suppressed_muted") && paused.Count == 1 && paused[0].Attempts.All(item => item.Status == "suppressed_paused"),
            ["crash_window_recovered"] = claimed && recoveredInFlight == 1 && afterCrash.GetAttempts(crashInstance.InstanceId).Any(item => item.Status == "failed" && item.Reason is not null && item.Reason.Contains("进程重启", StringComparison.Ordinal)) && engineDispatch.Count == 1 && afterCrash.GetAttempts(crashInstance.InstanceId).Count(item => item.Status == "attempted") == 2,
            ["stopped_failed_rejected"] = stoppedCreated && failedCreated && stoppedRejected && failedRejected && stoppedStore.GetSignalCount("stopped") == 0 && stoppedStore.GetSignalCount("failed") == 0,
            ["failed_stopped_do_not_consume_limit"] = failedCountCreated && stoppedCountCreated && five && !sixth,
            ["cooldown_same_direction_only"] = cooldownCreated && cooldownFirst.Count == 1 && cooldownSameDirection.Count == 1 && cooldownSameDirection[0].Attempts.All(item => item.Status == "suppressed_cooldown") && cooldownReverse.Count == 1 && cooldownReverse[0].Attempts.All(item => item.Status == "pending"),
            ["signal_query_stable"] = restarted.GetSignals(instance.InstanceId).Count == 4 && restarted.GetSignals(instance.InstanceId).SequenceEqual(restarted.GetSignals(instance.InstanceId).OrderByDescending(item => item.BarEnd).ThenByDescending(item => item.CreatedAt)),
            ["attempt_statuses_persisted"] = attempts.Any(item => item.Status == "pending" || item.Status.StartsWith("suppressed_", StringComparison.Ordinal)),
        };
        checks["all_passed"] = checks.Values.All(value => value is true);
        return JsonDocument.Parse(JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FixtureNotificationSink : ILiveNotificationSink
    {
        public void ShowDesktop(LiveReminderInstance instance, LiveSignalLedgerRecord signal) { }
        public void PlaySound(LiveReminderInstance instance, LiveSignalLedgerRecord signal) { }
    }
}
