using System.IO;
using System.Text.Json;
using System.Windows;
using Polyline = System.Windows.Shapes.Polyline;
using Qh.Core;

namespace QhApp;

public partial class MainWindow
{
    // Runs the same preset application and backtest path used by the two UI buttons.
    // This is an opt-in local self-check; it never starts market/account connections.
    public async Task<bool> RunFinalMainSelfCheckAsync(string outputPath)
    {
        var results = new List<object>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        try
        {
            foreach (var isShort in new[] { false, true })
            {
                var baselineId = FinalMainRunId(isShort);
                var store = new TradeRunStore(_store.RootDirectory);
                var baseline = store.ReadRecord(baselineId);
                if (!await ApplyFinalMainAsync(isShort)) throw new InvalidOperationException("主选配置未成功应用");
                var values = SignalReplayRunner.ValidateParameters(_strategyDescriptor!, BuildParametersOrDefaults());
                var defaults = _strategyDescriptor!.Parameters.ToDictionary(p => p.Key, p => p.Value.Default);
                bool Same(IReadOnlyDictionary<string, JsonElement> a, IReadOnlyDictionary<string, JsonElement> b) =>
                    a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) &&
                        (p.Value.ValueKind == JsonValueKind.Number && v.ValueKind == JsonValueKind.Number
                            ? p.Value.GetDecimal() == v.GetDecimal() : p.Value.ToString() == v.ToString()));
                var fields = _viewingRunId is null && _currentSnapshotId == baseline.SnapshotId && _bars.Count == 3893
                    && _bars.All(b => b.Symbol == "CZCE.SR701" && b.Period == "15m")
                    && Same(values, baseline.Parameters) && Same(defaults, baseline.Parameters)
                    && TradeLotsText.Text == "1" && TradeSlippageText.Text == "1" && TradeOpenFeeText.Text == "2"
                    && TradeInitialEquityText.Text == "2000000"
                    && StrategyPathText.Text.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
                    && TradingRulesContextText.Text.Contains("待回测配置", StringComparison.Ordinal)
                    && BacktestConfigurationStatusText.Text.Contains("尚无本次参数", StringComparison.Ordinal);
                if (!fields) throw new InvalidOperationException("主选字段、策略默认、数据或只读状态不一致");
                await TradeReplayButtonCoreAsync(); // First click must actually create and finish a new run.
                var run = _activeTradeRecord ?? throw new InvalidOperationException("界面未生成交易结果");
                var expectedNet = baseline.NetPnl;
                var expectedDd = baseline.MaxDrawdown;
                var expectedClosed = baseline.ClosedTradeCount;
                var expectedConfig = baseline.Config with { InitialEquity = 2_000_000m };
                var allFields = run.RunId != baselineId && run.Status == "completed" && run.Config == expectedConfig
                    && run.StartInclusive == baseline.StartInclusive && run.EndExclusive == baseline.EndExclusive
                    && run.StrategySha256 == baseline.StrategySha256 && Same(run.Parameters, baseline.Parameters)
                    && run.NetPnl == expectedNet && run.MaxDrawdown == expectedDd && run.ClosedTradeCount == expectedClosed;
                var oldFills = store.ReadMany<FillRecord>(baselineId, "fills.jsonl");
                string FillKey(FillRecord f) => $"{f.Action}|{f.Direction}|{f.Quantity}|{f.BarEnd:O}|{f.Price}|{f.Fee}|{f.TradingDay}";
                var fillsEqual = _tradeFills.Select(FillKey).SequenceEqual(oldFills.Select(FillKey));
                UpdateLayout(); DrawChart(); DrawEquityCurve(); UpdateTradingRules();
                var charts = ChartCanvas.Children.OfType<Polyline>().Any() && EquityCanvas.Children.OfType<Polyline>().Any()
                    && AverageLegendText.Text.Contains("EMA", StringComparison.Ordinal)
                    && TradingRulesContextText.Text.Contains("保存配置一致", StringComparison.Ordinal)
                    && BacktestConfigurationStatusText.Text.Contains("与参数、资金一致", StringComparison.Ordinal)
                    && StrategyCardText.Text.Contains("主选", StringComparison.Ordinal);
                var currentPosition = CurrentPositionSummary(run.RunId);
                var positionCorrect = currentPosition == (isShort ? "short 5 手" : "空仓");
                var parametersJson = StrategyParametersText.Text;
                var modified = run.Parameters.ToDictionary(p => p.Key, p => p.Value);
                modified["risk_budget"] = JsonSerializer.SerializeToElement(900);
                StrategyParametersText.Text = JsonSerializer.Serialize(modified);
                UpdateTradingRules();
                var staleWarning = TradingRulesContextText.Text.Contains("已修改，尚未重跑", StringComparison.Ordinal)
                    && BacktestConfigurationStatusText.Text.Contains("参数或资金已修改", StringComparison.Ordinal);
                StrategyParametersText.Text = parametersJson;
                UpdateTradingRules();
                if (!allFields || !fillsEqual || !charts || !positionCorrect || !staleWarning)
                    throw new InvalidOperationException("结果/逐笔成交/图表/期末持仓/修改提示与冻结基准不一致");
                WorkflowTabs.SelectedIndex = 1; ResultTabs.SelectedIndex = 0;
                var image = CaptureVisual(this, this);
                SaveBitmap(image.Bitmap, Path.Combine(Path.GetDirectoryName(outputPath)!, isShort ? "final-main-short.png" : "final-main-long.png"));
                results.Add(new { direction = isShort ? "short" : "long", run_id = run.RunId, baseline_id = baselineId,
                    source = StrategyPathText.Text, parameters = run.Parameters, config = run.Config, fields, fills_equal = fillsEqual,
                    charts, position_correct = positionCorrect, current_position = currentPosition, stale_warning = staleWarning,
                    net_pnl = run.NetPnl, max_drawdown = run.MaxDrawdown, closed_trades = run.ClosedTradeCount,
                    current_data = CurrentDataOnStrategyText.Text, rules = TradingRulesContextText.Text });

                // Exercise a single-click replay from the read-only historical view with the user's 20,000 CNY budget.
                await OpenTradeRunAsync(baselineId);
                var risk20Parameters = baseline.Parameters.ToDictionary(p => p.Key, p => p.Value);
                risk20Parameters["risk_budget"] = JsonSerializer.SerializeToElement(20_000);
                StrategyParametersText.Text = JsonSerializer.Serialize(risk20Parameters);
                TradeInitialEquityText.Text = "2000000";
                UpdateTradingRules();
                await TradeReplayButtonCoreAsync();
                var risk20Run = _activeTradeRecord ?? throw new InvalidOperationException("2万元风险预算没有生成交易结果");
                var risk20Fills = store.ReadMany<FillRecord>(risk20Run.RunId, "fills.jsonl");
                var risk20Events = store.ReadMany<RiskEvent>(risk20Run.RunId, "risk-events.jsonl");
                var expectedRisk20Net = isShort ? 419_594m : 295_554m;
                var expectedRisk20Dd = isShort ? 160_781m : 78_116m;
                var expectedRisk20Closed = isShort ? 44 : 25;
                var risk20Passed = risk20Run.Status == "completed" && risk20Run.Config.InitialEquity == 2_000_000m
                    && risk20Run.Parameters.TryGetValue("risk_budget", out var savedBudget) && savedBudget.GetDecimal() == 20_000m
                    && risk20Run.NetPnl == expectedRisk20Net && risk20Run.MaxDrawdown == expectedRisk20Dd
                    && risk20Run.ClosedTradeCount == expectedRisk20Closed
                    && !risk20Events.Any(item => item.Kind is "insufficient_funds" or "margin_exceeded");
                if (!risk20Passed) throw new InvalidOperationException("2万元风险预算的 UI 回测与资金配置/历史校验结果不一致");
                results.Add(new { direction = isShort ? "short" : "long", risk_budget = 20_000, initial_equity = risk20Run.Config.InitialEquity,
                    run_id = risk20Run.RunId, status = risk20Run.Status, fills = risk20Fills.Count, closed = risk20Run.ClosedTradeCount,
                    net_pnl = risk20Run.NetPnl, max_drawdown = risk20Run.MaxDrawdown, margin_rejections = risk20Events.Count(item => item.Kind is "insufficient_funds" or "margin_exceeded") });
            }
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new { passed = true, timestamp = DateTimeOffset.UtcNow,
                note = "两个实际主选应用与UI回测路径，首次开始创建新run；未连接交易账户", results }, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception error)
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new { passed = false, error = error.ToString(), results }, new JsonSerializerOptions { WriteIndented = true }));
            return false;
        }
    }
}
