using Qh.Core;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QhApp;

public partial class MainWindow
{
    private IReadOnlyList<Bar>? _averageBars;
    private string? _averageKey;
    private decimal?[] _fastAverage = [];
    private decimal?[] _slowAverage = [];
    private string _averageLabel = "";
    private bool _chartFocused;
    private GridLength _savedResultsHeight;
    private GridLength _savedSidebarWidth;

    // The worker seeds EMA with the first N closes, then applies 2/(N+1).
    // Indicators.Calculate uses the same formula and all pre-start warmup bars.
    private void EnsureChartAverages()
    {
        var key = _activeTradeRecord is { } run
            ? JsonSerializer.Serialize(run.Parameters)
            : StrategyParametersText?.Text ?? "{}";
        if (ReferenceEquals(_averageBars, _bars) && key == _averageKey) return;
        _averageBars = _bars;
        _averageKey = key;
        _fastAverage = new decimal?[_bars.Count];
        _slowAverage = new decimal?[_bars.Count];
        _averageLabel = "未配置双均线";
        try
        {
            using var document = JsonDocument.Parse(key);
            var parameters = document.RootElement;
            if (!parameters.TryGetProperty("fast_period", out var fastValue) ||
                !parameters.TryGetProperty("slow_period", out var slowValue)) return;
            var fast = fastValue.GetInt32();
            var slow = slowValue.GetInt32();
            var average = parameters.TryGetProperty("average", out var type) ? type.GetString() : "sma";
            if (fast < 1 || slow <= fast || slow > 10000 || average is not ("sma" or "ema")) return;
            _fastAverage = Indicators.Calculate(_bars, fast).Select(p => average == "ema" ? p.Ema : p.Sma).ToArray();
            _slowAverage = Indicators.Calculate(_bars, slow).Select(p => average == "ema" ? p.Ema : p.Sma).ToArray();
            _averageLabel = $"{average.ToUpperInvariant()} 快{fast}（橙） / 慢{slow}（蓝）";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            _averageLabel = "均线参数无效";
        }
        finally
        {
            if (AverageLegendText is not null) AverageLegendText.Text = _averageLabel;
        }
    }

    private void RefreshTradeReport(IReadOnlyList<ClosedTradeRecord> closed)
    {
        var fills = _tradeFills.ToDictionary(f => f.FillSequence);
        ClosedTradeGrid.ItemsSource = closed.Select(t => new ClosedTradeView(
            t.Direction == "long" ? "多" : "空", t.Quantity,
            fills.GetValueOrDefault(t.EntryFillSequence)?.BarEnd,
            fills.GetValueOrDefault(t.ExitFillSequence)?.BarEnd,
            t.EntryPrice, t.ExitPrice, t.GrossPnl, t.Fees, t.NetPnl, t.ExitFillSequence)).ToArray();
        if (_activeTradeRecord is not { } r)
        {
            ReportDetailsText.Text = "导入行情并完成交易回放，或输入已有 run ID 打开结果。";
            return;
        }
        var c = r.Config;
        var win = r.WinRate is { } rate ? $"{rate:0.00}%" : "不适用（无已平仓交易）";
        var fees = c.FeeMode == TradingFeeMode.Fixed
            ? $"开仓 {c.OpenFee:0.##} / 平今 {c.TodayCloseFee:0.##} / 平昨 {c.OrdinaryCloseFee:0.##} 元/手"
            : $"按成交额 {c.FeeRate:P4}";
        ReportDetailsText.Text = $"运行：{r.RunId}\n状态：{r.Status}  |  {(r.Explore ? "探索模式" : "严格模式")}\n" +
            $"合约：{_bars.FirstOrDefault()?.Symbol}  |  周期：{_bars.FirstOrDefault()?.Period}\n" +
            $"策略：{r.StrategyName}\n参数：{JsonSerializer.Serialize(r.Parameters)}\n" +
            $"区间（左闭右开）：\n{r.StartInclusive:yyyy-MM-dd HH:mm zzz} → {r.EndExclusive:yyyy-MM-dd HH:mm zzz}\n" +
            $"正式样本：{r.FormalRows:N0} 根  |  已处理：{r.ProcessedRows:N0} 根\n预热：{r.WarmupBarsAvailable} / 需要 {r.WarmupBarsRequired} 根\n\n" +
            $"初始资金：{c.InitialEquity:N2}\n期末权益：{r.FinalEquity:N2}\n净利：{r.NetPnl:N2}  |  收益率：{r.ReturnRate:0.00}%\n" +
            $"已实现毛利：{r.TotalGrossPnl:N2}  |  手续费：{r.TotalFees:N2}\n最大回撤（金额）：{r.MaxDrawdown:N2}\n" +
            $"成交：{r.FillCount} 笔  |  已平仓：{r.ClosedTradeCount} 笔  |  胜率：{win}\n\n" +
            $"成本假设（可调，非供应商确认费率）：\n{fees}\n滑点：{c.SlippageTicks} 跳  |  最小变动：{c.MinTick}\n" +
            $"每手乘数：{c.Multiplier}  |  默认手数：{c.Lots}  |  保证金率：{c.MarginRate:P2}\n" +
            "完成K线产生信号，下一有效K线开盘近似撮合。期末未平仓按收盘价计入权益；已平仓表不含未平仓。\n\n" +
            $"数据完整性：{(r.DataIncomplete ? "存在缺口/不完整，请查看数据质量" : "无已标记缺口")}\n覆盖：{r.CoverageMessage}\n" +
            (r.ErrorMessage is null ? "" : $"运行说明：{r.ErrorMessage}\n") + $"结果目录：{_tradeRunDirectory}";
    }

    private sealed record ClosedTradeView(string Direction, int Quantity, DateTimeOffset? EntryTime,
        DateTimeOffset? ExitTime, decimal EntryPrice, decimal ExitPrice, decimal GrossPnl, decimal Fees,
        decimal NetPnl, int ExitFillSequence);

    private void ClosedTradeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClosedTradeGrid.SelectedItem is ClosedTradeView trade &&
            _tradeFills.FirstOrDefault(f => f.FillSequence == trade.ExitFillSequence) is { } fill) LocateFill(fill);
    }

    private void ChartZoomIn_Click(object sender, RoutedEventArgs e) => ZoomChart(120, new Point(ChartCanvas.ActualWidth / 2, 40));
    private void ChartZoomOut_Click(object sender, RoutedEventArgs e) => ZoomChart(-120, new Point(ChartCanvas.ActualWidth / 2, 40));
    private void ChartReset_Click(object sender, RoutedEventArgs e)
    {
        _viewportCount = Math.Min(240, _bars.Count);
        _viewportStart = Math.Max(0, _bars.Count - _viewportCount);
        DrawChart();
    }
    private void ChartAll_Click(object sender, RoutedEventArgs e)
    {
        _viewportStart = 0;
        _viewportCount = _bars.Count;
        DrawChart();
    }
    private void ChartFocus_Click(object sender, RoutedEventArgs e)
    {
        _chartFocused = !_chartFocused;
        if (_chartFocused)
        {
            _savedResultsHeight = ResultsRow.Height;
            _savedSidebarWidth = SidebarColumn.Width;
        }
        SidebarPanel.Visibility = ResultsPanel.Visibility = ResultsSplitter.Visibility = _chartFocused ? Visibility.Collapsed : Visibility.Visible;
        SidebarSplitter.Visibility = _chartFocused ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.MinWidth = _chartFocused ? 0 : 300;
        SidebarColumn.Width = _chartFocused ? new GridLength(0) : _savedSidebarWidth;
        SidebarGapColumn.Width = new GridLength(_chartFocused ? 0 : 12);
        ResultsRow.MinHeight = _chartFocused ? 0 : 160;
        ResultsRow.Height = _chartFocused ? new GridLength(0) : _savedResultsHeight;
        ResultsSplitterRow.Height = new GridLength(_chartFocused ? 0 : 6);
        ChartFocusButton.Content = _chartFocused ? "返回报告" : "展开图表";
    }
    private void ChartCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isPanning) return;
        if (_crosshairVertical is not null) _crosshairVertical.Visibility = Visibility.Hidden;
        if (_crosshairHorizontal is not null) _crosshairHorizontal.Visibility = Visibility.Hidden;
        if (_crosshairLabel is not null) _crosshairLabel.Visibility = Visibility.Hidden;
    }

    public async Task<bool> OpenTradeRunAsync(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId != System.IO.Path.GetFileName(runId) || runId is "." or "..")
        {
            SetStatus("无效的交易 run ID");
            return false;
        }
        RunIdText.Text = runId;
        await OpenRunButtonCoreAsync();
        return _activeTradeRecord?.RunId == runId;
    }
}
