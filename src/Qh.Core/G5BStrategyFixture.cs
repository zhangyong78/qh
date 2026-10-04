using System.Globalization;
using System.Text.Json;

namespace Qh.Core;

public static class G5BStrategyFixtureVerifier
{
    public static async Task<JsonDocument> RunAsync(string rootDirectory, string strategyPath, string workerPath, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(rootDirectory)) Directory.Delete(rootDirectory, true);
        Directory.CreateDirectory(rootDirectory);
        var store = new LiveNotifyStore(rootDirectory);
        var sink = new FixtureSink();
        var engine = new LiveReminderEngine(store, sink);
        var coordinator = new LiveSessionCoordinator(rootDirectory);
        var descriptor = await StrategyCatalog.DescribeAsync(strategyPath, workerPath, cancellationToken);
        var parameters = SignalReplayRunner.ValidateParameters(descriptor, new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        var parameterJson = JsonSerializer.Serialize(parameters);
        var parameterSummary = string.Join("，", parameters.Select(item => $"{item.Key}={item.Value}"));
        var warmup = SignalReplayRunner.RequiredWarmupBars(descriptor, parameters);
        var now = DateTimeOffset.Parse("2026-10-01T09:00:00+08:00", CultureInfo.InvariantCulture);
        var instance = new LiveReminderInstance("g5b-main", "TEST.G5B", 60, descriptor.Name, descriptor.Version, descriptor.StrategySha256, LiveReminderState.Running, true, true, 0, null, null, null, null, null, DateTimeOffset.UtcNow);
        var created = store.TryCreateOrUpdateInstance(instance, out _);
        store.ConfigureStrategy(instance.InstanceId, descriptor.StrategyPath, descriptor.Name, descriptor.Version, descriptor.StrategySha256, parameterJson, parameterSummary, warmup);
        var session = await LiveStrategySession.StartAsync(store, instance.InstanceId, workerPath, cancellationToken);
        var bars = new[] { 3m, 2m, 1m, 2m, 3m, 0.5m }.Select((close, index) => new LiveKline("TEST.G5B", 60, now.AddMinutes(index), now.AddMinutes(index + 1), close, close + 0.2m, close - 0.2m, close, 1, 1, true, index + 1, false)).ToArray();
        var warmupResults = new List<LiveStrategyEvaluationResult>();
        for (var index = 0; index < 5; index++)
        {
            var accepted = await coordinator.AcceptAsync("g5b-session", [bars[index]], MarketSessionState.Connected, cancellationToken);
            foreach (var bar in accepted)
            {
                var evaluation = await session.EvaluateAsync(bar, cancellationToken);
                warmupResults.Add(evaluation);
                if (evaluation.Candidates.Count > 0) await engine.AcceptCompletedBarAsync(instance.InstanceId, bar, evaluation.Candidates, cancellationToken, evaluation.SourcePhase);
            }
        }
        var formalSignalCount = store.GetSignalCount(instance.InstanceId);
        var formalAttempts = store.GetAttempts(instance.InstanceId).Count(item => item.Status == "attempted");
        var duplicateEvaluation = await session.EvaluateAsync(bars[4], cancellationToken);
        if (duplicateEvaluation.Candidates.Count > 0) await engine.AcceptCompletedBarAsync(instance.InstanceId, bars[4], duplicateEvaluation.Candidates, cancellationToken, duplicateEvaluation.SourcePhase);
        var afterDuplicate = store.GetSignalCount(instance.InstanceId);
        await session.DisposeAsync();
        var restarted = await LiveStrategySession.StartAsync(store, instance.InstanceId, workerPath, cancellationToken);
        var restartEvaluation = await restarted.EvaluateAsync(bars[4], cancellationToken);
        if (restartEvaluation.Candidates.Count > 0) await engine.AcceptCompletedBarAsync(instance.InstanceId, bars[4], restartEvaluation.Candidates, cancellationToken, restartEvaluation.SourcePhase);
        var afterRestart = store.GetSignalCount(instance.InstanceId);
        var recoveryAccepted = await coordinator.AcceptAsync("g5b-session", [bars[5] with { IsRecovery = true }], MarketSessionState.Recovering, cancellationToken);
        var recoveryEvaluation = recoveryAccepted.Count == 1 ? await restarted.EvaluateAsync(recoveryAccepted[0], cancellationToken) : new LiveStrategyEvaluationResult([], LiveSignalSourcePhase.Recovery, false, false);
        if (recoveryEvaluation.Candidates.Count > 0) await engine.AcceptCompletedBarAsync(instance.InstanceId, recoveryAccepted[0], recoveryEvaluation.Candidates, cancellationToken, recoveryEvaluation.SourcePhase);
        var recoveryAttempts = store.GetAttempts(instance.InstanceId).Where(item => item.Reason == "suppressed_recovery_or_warmup").ToArray();
        await restarted.DisposeAsync();

        var errorDescriptor = await StrategyCatalog.DescribeAsync(Path.Combine(Path.GetDirectoryName(strategyPath)!, "runtime_error.py"), workerPath, cancellationToken);
        var errorInstance = instance with { InstanceId = "g5b-error", Symbol = "TEST.ERROR", StrategyName = errorDescriptor.Name, StrategyVersion = errorDescriptor.Version, StrategySha256 = errorDescriptor.StrategySha256 };
        var errorCreated = store.TryCreateOrUpdateInstance(errorInstance, out _);
        store.ConfigureStrategy(errorInstance.InstanceId, errorDescriptor.StrategyPath, errorDescriptor.Name, errorDescriptor.Version, errorDescriptor.StrategySha256, "{}", "无参数", 0);
        var errorSession = await LiveStrategySession.StartAsync(store, errorInstance.InstanceId, workerPath, cancellationToken);
        var errorFailed = false;
        try { await errorSession.EvaluateAsync(bars[0] with { Symbol = "TEST.ERROR" }, cancellationToken); }
        catch (LiveStrategyException) { errorFailed = true; }
        await errorSession.DisposeAsync();
        var errorConfig = store.GetStrategyConfig(errorInstance.InstanceId);
        var mainConfig = store.GetStrategyConfig(instance.InstanceId);
        var checks = new Dictionary<string, object?>
        {
            ["created_and_configured"] = created && mainConfig is not null && mainConfig.StrategyVersion == descriptor.Version && mainConfig.StrategySha256 == descriptor.StrategySha256 && mainConfig.WarmupBarsRequired == warmup,
            ["preheat_silent"] = warmupResults.Count >= warmup && warmupResults.Take(warmup).All(item => item.WasWarmup && item.Candidates.Count == 0) && store.GetSignalCount(instance.InstanceId) >= 1,
            ["formal_candidate_to_ledger"] = formalSignalCount == 1 && formalAttempts == 2 && sink.DesktopCount == 1 && sink.SoundCount == 1,
            ["duplicate_and_restart_dedup"] = duplicateEvaluation.WasDuplicate && restartEvaluation.WasDuplicate && afterDuplicate == formalSignalCount && afterRestart == formalSignalCount,
            ["recovery_ledger_only"] = recoveryEvaluation.SourcePhase == LiveSignalSourcePhase.Recovery && recoveryEvaluation.Candidates.Count == 1 && store.GetSignalCount(instance.InstanceId) == 2 && recoveryAttempts.Length == 2,
            ["strategy_error_isolated"] = errorCreated && errorFailed && errorConfig?.Status == "failed" && (mainConfig?.Status is "running" or "recovering")
        };
        checks["all_passed"] = checks.Values.All(value => value is true);
        return JsonDocument.Parse(JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FixtureSink : ILiveNotificationSink
    {
        public int DesktopCount { get; private set; }
        public int SoundCount { get; private set; }
        public void ShowDesktop(LiveReminderInstance instance, LiveSignalLedgerRecord signal) => DesktopCount++;
        public void PlaySound(LiveReminderInstance instance, LiveSignalLedgerRecord signal) => SoundCount++;
    }
}
