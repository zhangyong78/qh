using Microsoft.Win32;
using Qh.Core;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace QhApp;

public partial class MainWindow : Window
{
    private sealed record ContractOption(string Symbol, string DisplayName, bool IsConcrete);

    private readonly DataStore _store;
    private readonly CsvImporter _importer = new();
    private CancellationTokenSource? _taskCancellation;
    private Process? _worker;
    private IReadOnlyList<Bar> _bars = [];
    private IReadOnlyList<IndicatorPoint> _indicators = [];
    private Task<ImportResult>? _importOperation;
    private CancellationTokenSource? _queryCancellation;
    private Task? _queryOperation;
    private Task<SignalReplayResult>? _replayOperation;
    private Task<TradeReplayResult>? _tradeReplayOperation;
    private readonly SemaphoreSlim _contextGate = new(1, 1);
    private readonly TqCredentialVault _tqCredentialVault = new();
    private CancellationTokenSource? _tqProbeCancellation;
    private Task<MarketOperationResult<TqSdkCapabilities>>? _tqProbeOperation;
    private CancellationTokenSource? _tqHistoryCancellation;
    private Task? _tqHistoryOperation;
    private CancellationTokenSource? _tqLiveCancellation;
    private Task? _tqLiveOperation;
    private LiveNotifyStore? _liveNotifyStore;
    private LiveReminderEngine? _liveReminderEngine;
    private LiveNotificationSink? _liveNotificationSink;
    private LiveStrategySession? _liveStrategySession;
    private string? _liveInstanceId;
    private CancellationTokenSource? _replayCancellation;
    private CancellationTokenSource? _tradeReplayCancellation;
    private SignalReplayResult? _lastSignalResult;
    private string? _currentSnapshotId;
    private StrategyDescriptor? _strategyDescriptor;
    private readonly Dictionary<string, TextBox> _strategyParameterTextBoxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComboBox> _strategyParameterComboBoxes = new(StringComparer.Ordinal);
    private bool _syncingStrategyParameterForm;
    private IReadOnlyList<SignalEvent> _allSignals = [];
    private IReadOnlyList<SignalEvent> _displayedSignals = [];
    private IReadOnlyList<SignalEvent> _tradeSignals = [];
    private IReadOnlyList<FillRecord> _tradeFills = [];
    private IReadOnlyList<EquityPoint> _tradeEquity = [];
    private string? _tradeRunDirectory;
    private string? _activeTradeRunId;
    private TradeRunRecord? _activeTradeRecord;
    private CancellationTokenSource? _strategyLoadCancellation;
    private Task<StrategyDescriptor>? _strategyLoadOperation;
    private SnapshotInfo? _currentSnapshotInfo;
    private bool _syncingPeriodSwitch;
    private bool _syncingTqHistorySymbolOptions;
    private bool _tqHistoryManualMode;
    private bool _clearingTqHistorySymbolText;
    private bool _syncingTqLiveSymbolOptions;
    private bool _tqLiveManualMode;
    private bool _clearingTqLiveSymbolText;
    private string? _viewingRunId;
    private bool _allowClose;
    private bool _closingInProgress;
    private bool _restoringRunView;
    private int _viewportStart;
    private int _viewportCount;
    private bool _isPanning;
    private Point _panOrigin;
    private int _panStart;
    private Line? _crosshairVertical;
    private Line? _crosshairHorizontal;
    private TextBlock? _crosshairLabel;
    private double _chartMin;
    private double _chartMax;
    private double _chartHeight;
    private double _chartWidth;
    private CancellationTokenSource? _workerProtocolCancellation;
    private bool _workerReady;
    private bool _workerCancelPending;
    private bool _workerStopAck;

    public MainWindow(string? dataRoot = null, bool autoLoadLatest = true)
    {
        InitializeComponent();
        TradeConfigText.Text = "{\"initialEquity\":10000,\"lots\":1,\"multiplier\":10,\"minTick\":1,\"marginRate\":0.1,\"feeMode\":\"Fixed\",\"openFee\":2,\"todayCloseFee\":3,\"ordinaryCloseFee\":1,\"feeRate\":0,\"slippageTicks\":0}";
        _store = new DataStore(dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QhResearch", "data"));
        _liveNotifyStore = new LiveNotifyStore(_store.RootDirectory);
        _liveNotificationSink = new LiveNotificationSink(this);
        _liveReminderEngine = new LiveReminderEngine(_liveNotifyStore, _liveNotificationSink);
        UpdateCurrentDataSelection();
        RefreshTqHistorySymbolOptions();
        RefreshTqLiveSymbolOptions();
        UpdateTqHistoryCalendarReadiness();
        RefreshLiveReminderList();
        var bundledStrategy = Path.Combine(AppContext.BaseDirectory, "strategies", "sma_ema_cross.py");
        if (File.Exists(bundledStrategy)) StrategyPathText.Text = bundledStrategy;
        UpdateStrategyCardForPreset();
        UpdateLiveStrategySelectionSummary();
        if (_tqCredentialVault.Exists)
        {
            try
            {
                var saved = _tqCredentialVault.Load();
                if (saved is not null) TqAccountText.Text = saved.Account;
                TqSdkStatusText.Text = "已存在当前 Windows 用户保护凭据；默认仍离线";
            }
            catch { TqSdkStatusText.Text = "本机凭据无法解密，请重新输入；默认仍离线"; }
        }
        if (autoLoadLatest) Loaded += async (_, _) => await LoadLatestSnapshotAsync();
        Closing += MainWindow_Closing;
    }

    private void TqSaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TqRememberCheck.IsChecked != true) { TqSdkStatusText.Text = "未勾选保存；凭据只可用于本次连接测试，不写入磁盘"; return; }
            _tqCredentialVault.Save(TqAccountText.Text, TqPasswordBox.Password);
            TqPasswordBox.Clear();
            TqSdkStatusText.Text = "凭据已保存到当前 Windows 用户保护；未连接、未进入策略进程";
        }
        catch { TqSdkStatusText.Text = "凭据保存失败；未显示详细异常，请检查当前 Windows 用户保护状态"; }
    }

    private async void TqTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tqProbeOperation is { IsCompleted: false }) return;
        var account = TqAccountText.Text.Trim();
        var password = TqPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(account)) { TqSdkStatusText.Text = "请输入账号；未启动连接"; return; }
        if (string.IsNullOrEmpty(password) && _tqCredentialVault.Exists)
        {
            try
            {
                var saved = _tqCredentialVault.Load();
                if (saved is not null && string.Equals(saved.Account.Trim(), account, StringComparison.OrdinalIgnoreCase)) password = saved.Password;
                else if (saved is not null) { TqSdkStatusText.Text = "当前账号与已保存凭据不一致，请重新输入密码"; return; }
            }
            catch { TqSdkStatusText.Text = "本机凭据无法解密，请重新输入密码"; return; }
        }
        if (string.IsNullOrEmpty(password)) { TqSdkStatusText.Text = "请输入密码；未启动连接"; return; }
        _tqProbeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _tqProbeCancellation = cancellation;
        TqTestButton.IsEnabled = false;
        TqCancelButton.IsEnabled = true;
        Task<MarketOperationResult<TqSdkCapabilities>>? operation = null;
        try
        {
            var gateway = new TqSdkGateway();
            operation = ProbeTqWithPreparationAsync(gateway, new TqSdkCredentials(account, password), cancellation.Token);
            _tqProbeOperation = operation;
            var result = await operation;
            if (result.Success && result.Value is { } capabilities)
                TqSdkStatusText.Text = $"连接成功：TqSdk {capabilities.TargetVersion}；普通 K 线末端 {capabilities.KlineSerialMaxRows} 根；DataDownloader={(capabilities.CanDataDownloader is true ? "已验证" : "未验证") }。未启动策略。";
            else
                TqSdkStatusText.Text = FormatTqProbeFailure(result.ErrorKind);
        }
        catch (OperationCanceledException)
        {
            TqSdkStatusText.Text = "连接测试已取消";
        }
        catch { TqSdkStatusText.Text = "未验证：未知错误；连接测试未完成。请确认包内依赖后重试；未显示详细异常。"; }
        finally
        {
            if (ReferenceEquals(_tqProbeOperation, operation)) _tqProbeOperation = null;
            _tqProbeCancellation?.Dispose();
            _tqProbeCancellation = null;
            TqTestButton.IsEnabled = true;
            TqCancelButton.IsEnabled = false;
        }
    }

    private async Task<MarketOperationResult<TqSdkCapabilities>> ProbeTqWithPreparationAsync(TqSdkGateway gateway, TqSdkCredentials credentials, CancellationToken cancellationToken)
    {
        return await TqSdkConnectionWorkflow.ProbeAsync(gateway, credentials, cancellationToken, stage =>
        {
            TqSdkStatusText.Text = stage.StartsWith("network_handshake", StringComparison.Ordinal)
                ? $"阶段 2/2：依赖已就绪（{stage[(stage.IndexOf(':') + 1)..]}），正在进行连接握手……"
                : "阶段 1/2：准备项目隔离 TqSdk 依赖……";
        });
    }

    private static string FormatTqProbeFailure(MarketErrorKind errorKind) => errorKind switch
    {
        MarketErrorKind.Authentication => "未验证：认证失败；请检查账号和密码后重试。密码不会写入日志或命令行。",
        MarketErrorKind.Network => "未验证：网络/DNS/连接失败；请检查网络、代理或防火墙后重试。",
        MarketErrorKind.Timeout => "未验证：网络连接超时；请检查网络、代理或防火墙后重试。",
        MarketErrorKind.Permission => "未验证：权限不足；普通连接或专业 DataDownloader 权限未获供应商确认，请联系供应商核对权限。",
        MarketErrorKind.Unsupported => "未验证：SDK 缺失或初始化失败；请确认从完整包目录启动，并保留包内依赖目录。",
        MarketErrorKind.Protocol => "未验证：SDK bridge 协议失败；请重新解压完整包后重试，未显示详细异常。",
        MarketErrorKind.Cancelled => "连接测试已取消",
        _ => "未验证：未知错误；请确认网络与包内依赖后重试，未显示详细异常。"
    };

    private void TqCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tqProbeCancellation is { IsCancellationRequested: false })
        {
            TqSdkStatusText.Text = "正在取消连接测试……";
            _tqProbeCancellation.Cancel();
        }
    }

    private bool TryGetTqCredentials(out TqSdkCredentials credentials, out string error)
    {
        credentials = default!;
        error = "";
        var account = TqAccountText.Text.Trim();
        var password = TqPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(account)) { error = "请输入账号"; return false; }
        if (string.IsNullOrEmpty(password) && _tqCredentialVault.Exists)
        {
            try
            {
                var saved = _tqCredentialVault.Load();
                if (saved is not null && string.Equals(saved.Account.Trim(), account, StringComparison.OrdinalIgnoreCase)) password = saved.Password;
                else if (saved is not null) { error = "当前账号与已保存凭据不一致，请重新输入密码"; return false; }
            }
            catch { error = "本机凭据无法解密，请重新输入密码"; return false; }
        }
        if (string.IsNullOrEmpty(password)) { error = "请输入密码"; return false; }
        credentials = new TqSdkCredentials(account, password);
        return true;
    }

    private bool TryBuildTqHistoryRequest(out TqSdkCredentials credentials, out HistoricalMarketRequest request, out string error)
    {
        request = default!;
        if (!TryGetTqCredentials(out credentials, out error)) return false;
        var selectedOption = TqHistorySymbolCombo.SelectedItem as ContractOption;
        var selectedSymbol = selectedOption?.IsConcrete == true ? selectedOption.Symbol : null;
        var symbol = HistoryFormSelection.ResolveContract(selectedSymbol, TqHistorySymbolText.Text, _tqHistoryManualMode);
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Contains('@') || symbol.Contains("888", StringComparison.OrdinalIgnoreCase) || symbol.Contains("999", StringComparison.OrdinalIgnoreCase)) { error = "请输入具体合约，不能使用主连/指数/888/999"; return false; }
        if (TqHistoryDurationCombo.SelectedItem is not ComboBoxItem durationItem || !int.TryParse(durationItem.Tag?.ToString() ?? durationItem.Content?.ToString(), out var duration) || duration <= 0) { error = "历史周期无效"; return false; }
        if (!HistoryFormSelection.TryBuildDateRange(TqHistoryStartDatePicker.SelectedDate, TqHistoryEndDatePicker.SelectedDate, TqHistoryStartText.Text, TqHistoryEndText.Text, out var start, out var end, out error)) return false;
        var professional = TqHistoryModeCombo.SelectedItem is ComboBoxItem modeItem && string.Equals(modeItem.Tag?.ToString(), "professional", StringComparison.OrdinalIgnoreCase);
        if (!HistoricalCalendarResolver.TryLoad(_store.RootDirectory, symbol, start, end, out var tradingDays, out var sessionRanges, out var calendarSource, out _, out var calendarError)) { error = calendarError; return false; }
        request = new HistoricalMarketRequest(symbol, duration, start, end, PreferProfessionalDownloader: professional, RetryCount: 1, ProviderTradingDays: tradingDays, CoverageEvidence: calendarSource, ProviderCalendarSource: calendarSource, ProviderSessionRanges: sessionRanges);
        return true;
    }

    private string GetTqHistorySymbol()
    {
        var selected = TqHistorySymbolCombo.SelectedItem as ContractOption;
        return HistoryFormSelection.ResolveContract(selected?.IsConcrete == true ? selected.Symbol : null, TqHistorySymbolText.Text, _tqHistoryManualMode);
    }

    private void RefreshTqHistorySymbolOptions()
    {
        try
        {
            var localOptions = GetLocalConcreteContractOptions();
            var options = localOptions.Concat(CommonContractDirectory()).ToArray();
            _syncingTqHistorySymbolOptions = true;
            TqHistorySymbolCombo.ItemsSource = options;
            var currentSymbol = _currentSnapshotInfo?.Symbol;
            TqHistorySymbolCombo.SelectedItem = options.FirstOrDefault(item => string.Equals(item.Symbol, currentSymbol, StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault();
            _syncingTqHistorySymbolOptions = false;
            TqHistorySymbolHintText.Text = localOptions.Count == 0
                ? "当前数据根没有已发布具体合约；下方常用入口仅供填写前缀，仍需展开手动输入完整月份。"
                : $"本地已验证具体合约 {localOptions.Count} 个，优先显示；下方常用入口不是行情覆盖承诺，仍需具体合约、连接和日历门禁。";
            UpdateTqHistoryCalendarReadiness();
        }
        catch (Exception ex)
        {
            _syncingTqHistorySymbolOptions = false;
            TqHistorySymbolHintText.Text = "读取已发布合约失败；请展开手动输入。";
            AppendLog("读取历史合约选择列表失败：" + ex.Message);
        }
    }

    private IReadOnlyList<ContractOption> GetLocalConcreteContractOptions() =>
        _store.GetSnapshots()
            .Where(item => !string.IsNullOrWhiteSpace(item.Symbol) && !item.Symbol.Contains('@') && !item.Symbol.Contains("888", StringComparison.OrdinalIgnoreCase) && !item.Symbol.Contains("999", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Symbol)
            .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .Select(symbol => new ContractOption(symbol, ContractDisplayName(symbol), true))
            .ToArray();

    private void RefreshTqLiveSymbolOptions()
    {
        try
        {
            var options = GetLocalConcreteContractOptions();
            _syncingTqLiveSymbolOptions = true;
            TqLiveSymbolCombo.ItemsSource = options;
            var currentSymbol = _currentSnapshotInfo?.Symbol;
            TqLiveSymbolCombo.SelectedItem = options.FirstOrDefault(item => string.Equals(item.Symbol, currentSymbol, StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault();
            _syncingTqLiveSymbolOptions = false;
            TqLiveSymbolHintText.Text = options.Count == 0
                ? "当前数据根没有已发布具体合约；请展开高级手动输入。主流前缀不会作为可启动选项。"
                : $"可选 {options.Count} 个本地已验证具体合约；显示含交易所、代码和可用状态。";
        }
        catch (Exception ex)
        {
            _syncingTqLiveSymbolOptions = false;
            TqLiveSymbolHintText.Text = "读取本地具体合约失败；请展开高级手动输入。";
            AppendLog("读取实时合约选择列表失败：" + ex.Message);
        }
    }

    private static IReadOnlyList<ContractOption> CommonContractDirectory() =>
    [
        new("", "常用入口 · 郑商所 · 白糖 SR · CZCE.SR<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 郑商所 · 棉花 CF · CZCE.CF<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 郑商所 · PTA TA · CZCE.TA<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 郑商所 · 甲醇 MA · CZCE.MA<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 上期所 · 沪铜 CU · SHFE.CU<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 上期所 · 螺纹钢 RB · SHFE.RB<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 上期所 · 热卷 HC · SHFE.HC<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 上期所 · 沪铝 AL · SHFE.AL<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 大商所 · 铁矿石 I · DCE.I<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 大商所 · 豆粕 M · DCE.M<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 大商所 · 棕榈油 P · DCE.P<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 能源中心 · 原油 SC · INE.SC<月份> · 需完整月份；需连接+日历；本地无数据", false),
        new("", "常用入口 · 中金所 · 沪深300 IF · CFFEX.IF<月份> · 需完整月份；需连接+日历；本地无数据", false)
    ];

    private static string ContractDisplayName(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        var name = normalized.StartsWith("CZCE.SR", StringComparison.Ordinal) ? "白糖"
            : normalized.StartsWith("SHFE.CU", StringComparison.Ordinal) ? "沪铜"
            : normalized.StartsWith("SHFE.RB", StringComparison.Ordinal) ? "螺纹钢"
            : "期货合约";
        var exchange = normalized.Split('.', 2)[0] switch
        {
            "CZCE" => "郑商所",
            "SHFE" => "上期所",
            "DCE" => "大商所",
            "INE" => "能源中心",
            "CFFEX" => "中金所",
            _ => normalized.Split('.', 2)[0]
        };
        return $"{name} · {exchange} · {symbol} · 本地已验证，可直接使用";
    }

    private void TqHistorySymbolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingTqHistorySymbolOptions) UseSelectedTqHistorySymbol();
    }

    private void TqHistorySymbolCombo_DropDownClosed(object sender, EventArgs e)
    {
        if (!_syncingTqHistorySymbolOptions && TqHistorySymbolCombo.SelectedItem is ContractOption) UseSelectedTqHistorySymbol();
    }

    private void UseSelectedTqHistorySymbol()
    {
        if (TqHistorySymbolCombo.SelectedItem is not ContractOption selected) return;
        _tqHistoryManualMode = false;
        _clearingTqHistorySymbolText = true;
        try { TqHistorySymbolText.Clear(); }
        finally { _clearingTqHistorySymbolText = false; }
        TqHistorySymbolHintText.Text = selected.IsConcrete
            ? $"已选择：{selected.DisplayName}；请求将使用该具体合约。"
            : $"{selected.DisplayName}；这是品种前缀入口，不是具体合约，不会直接发起请求。请展开高级输入补完整月份，例如 CZCE.SR701。";
        UpdateWorkflowHint();
        UpdateTqHistoryCalendarReadiness();
    }

    private void TqHistorySymbolText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_clearingTqHistorySymbolText && !_syncingTqHistorySymbolOptions)
        {
            _tqHistoryManualMode = !string.IsNullOrWhiteSpace(TqHistorySymbolText.Text);
            if (_tqHistoryManualMode) TqHistorySymbolHintText.Text = $"手动模式：将使用 {TqHistorySymbolText.Text.Trim()}；选择下拉合约会清除手动值。";
        }
        UpdateWorkflowHint();
        UpdateTqHistoryCalendarReadiness();
    }

    private void TqLiveSymbolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingTqLiveSymbolOptions) UseSelectedTqLiveSymbol();
    }

    private void TqLiveSymbolCombo_DropDownClosed(object sender, EventArgs e)
    {
        if (!_syncingTqLiveSymbolOptions && TqLiveSymbolCombo.SelectedItem is ContractOption) UseSelectedTqLiveSymbol();
    }

    private void UseSelectedTqLiveSymbol()
    {
        if (TqLiveSymbolCombo.SelectedItem is not ContractOption selected) return;
        _tqLiveManualMode = false;
        _clearingTqLiveSymbolText = true;
        try { TqLiveSymbolText.Clear(); }
        finally { _clearingTqLiveSymbolText = false; }
        TqLiveSymbolHintText.Text = $"已选择：{selected.DisplayName}；实时启动将使用该具体合约。";
    }

    private void TqLiveSymbolText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_clearingTqLiveSymbolText && !_syncingTqLiveSymbolOptions)
        {
            _tqLiveManualMode = !string.IsNullOrWhiteSpace(TqLiveSymbolText.Text);
            if (_tqLiveManualMode) TqLiveSymbolHintText.Text = $"手动模式：将使用 {TqLiveSymbolText.Text.Trim()}；仍需通过具体合约和本机日历门禁。";
        }
    }

    private string GetTqLiveSymbol()
    {
        var selected = TqLiveSymbolCombo.SelectedItem as ContractOption;
        return HistoryFormSelection.ResolveContract(selected?.IsConcrete == true ? selected.Symbol : null, TqLiveSymbolText.Text, _tqLiveManualMode);
    }

    private void TqHistoryDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateWorkflowHint(); UpdateTqHistoryCalendarReadiness(); }

    private void TqHistoryDatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e) { UpdateWorkflowHint(); UpdateTqHistoryCalendarReadiness(); }

    private void UpdateTqHistoryCalendarReadiness()
    {
        if (!IsInitialized || TqHistoryCalendarStatusText is null) return;
        var symbol = GetTqHistorySymbol();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            TqHistoryCalendarStatusText.Text = "请选择具体合约；未选择前不会发起请求。";
            return;
        }
        if (!HistoryFormSelection.TryBuildDateRange(TqHistoryStartDatePicker.SelectedDate, TqHistoryEndDatePicker.SelectedDate, TqHistoryStartText.Text, TqHistoryEndText.Text, out var start, out var end, out var dateError))
        {
            TqHistoryCalendarStatusText.Text = $"日期范围未就绪：{dateError}。未发起请求。";
            return;
        }
        if (HistoricalCalendarResolver.TryLoad(_store.RootDirectory, symbol, start, end, out _, out _, out var source, out var path, out var error))
        {
            TqHistoryCalendarStatusText.Text = $"本机日历已就绪：{Path.GetFileName(path)}；来源={source}。现在仍需明确点击“检查覆盖”或“下载并发布”。";
        }
        else TqHistoryCalendarStatusText.Text = error;
    }

    private void SetTqHistoryStatus(string message)
    {
        TqHistoryStatusText.Text = message;
        TqHistoryCalendarStatusText.Text = message;
    }

    private void GenerateLocalCalendarButton_Click(object sender, RoutedEventArgs e)
    {
        var symbol = GetTqHistorySymbol();
        if (string.IsNullOrWhiteSpace(symbol)) { SetTqHistoryStatus("请先选择具体合约；未生成日历"); return; }
        if (!HistoryFormSelection.TryBuildDateRange(TqHistoryStartDatePicker.SelectedDate, TqHistoryEndDatePicker.SelectedDate, TqHistoryStartText.Text, TqHistoryEndText.Text, out var start, out var end, out var dateError))
        {
            SetTqHistoryStatus("不能生成日历：" + dateError);
            return;
        }
        try
        {
            GenerateLocalCalendarButton.IsEnabled = false;
            if (HistoricalLocalCalendarGenerator.TryGenerate(_store, symbol, start, end, out var generated, out var error) && generated is not null)
            {
                SetTqHistoryStatus($"已生成本机研究日历：{generated.Symbol}；{generated.TradingDayCount} 个交易日、{generated.SessionRangeCount} 个实际时段；来源快照={generated.SnapshotId}。不是供应商官方日历；请继续点击“检查覆盖”或“下载并发布”。");
                UpdateTqHistoryCalendarReadiness();
            }
            else SetTqHistoryStatus("未生成日历：" + error);
        }
        catch (Exception exception) { SetTqHistoryStatus("生成日历失败：" + exception.Message); }
        finally { GenerateLocalCalendarButton.IsEnabled = true; }
    }

    private async void TqHistoryCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tqHistoryOperation is { IsCompleted: false }) return;
        if (!TryBuildTqHistoryRequest(out var credentials, out var request, out var error)) { SetTqHistoryStatus(error); return; }
        _tqHistoryCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _tqHistoryCancellation = cancellation;
        TqHistoryCheckButton.IsEnabled = false; TqHistoryDownloadButton.IsEnabled = false; TqHistoryCancelButton.IsEnabled = true;
        try
        {
            SetTqHistoryStatus("正在检查实际历史覆盖……");
            _tqHistoryOperation = new TqSdkGateway().DownloadHistoryAsync(credentials, request, cancellation.Token);
            var result = await (Task<MarketOperationResult<HistoricalMarketResult>>)_tqHistoryOperation;
            SetTqHistoryStatus(result.Success && result.Value is { } value
                ? $"覆盖检查：路径={(value.Coverage.UsedProfessionalDownloader ? "专业" : "普通")}；{value.Coverage.Rows} 行；{value.Coverage.First:O} → {value.Coverage.Last:O}；完整={value.Coverage.Complete}；{value.Coverage.Note}"
                : $"历史未验证：{result.ErrorKind}；{result.Message}");
        }
        catch (OperationCanceledException) { SetTqHistoryStatus("历史检查已取消"); }
        catch (Exception ex) { SetTqHistoryStatus("历史检查失败：" + ex.GetType().Name); }
        finally { _tqHistoryOperation = null; _tqHistoryCancellation?.Dispose(); _tqHistoryCancellation = null; TqHistoryCheckButton.IsEnabled = true; TqHistoryDownloadButton.IsEnabled = true; TqHistoryCancelButton.IsEnabled = false; UpdateTqHistoryCalendarReadiness(); }
    }

    private async void TqHistoryDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tqHistoryOperation is { IsCompleted: false }) return;
        if (!TryBuildTqHistoryRequest(out var credentials, out var request, out var error)) { SetTqHistoryStatus(error); return; }
        if (!await _contextGate.WaitAsync(0)) { SetTqHistoryStatus("已有研究上下文任务运行中，历史发布未启动"); return; }
        _tqHistoryCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _tqHistoryCancellation = cancellation;
        TqHistoryCheckButton.IsEnabled = false; TqHistoryDownloadButton.IsEnabled = false; TqHistoryCancelButton.IsEnabled = true;
        try
        {
            SetTqHistoryStatus("正在下载、质量校验并发布……");
            var publisher = new MarketHistoryPublisher();
            _tqHistoryOperation = publisher.DownloadAndPublishAsync(new TqSdkGateway(), credentials, request, _store, cancellation.Token);
            var result = await (Task<MarketOperationResult<HistoricalPublicationResult>>)_tqHistoryOperation;
            if (result.Success && result.Value is { } publication && publication.SnapshotId is not null)
            {
                RefreshSnapshotList();
                await LoadSnapshotAsync(publication.SnapshotId);
                SetTqHistoryStatus($"已发布快照 {publication.SnapshotId}；路径={(publication.Coverage?.UsedProfessionalDownloader == true ? "专业" : "普通")}；{publication.Coverage?.Rows} 行；{publication.Coverage?.First:O} → {publication.Coverage?.Last:O}");
            }
            else SetTqHistoryStatus($"历史发布未完成：{result.ErrorKind}；{result.Message}");
        }
        catch (OperationCanceledException) { SetTqHistoryStatus("历史下载已取消"); }
        catch (Exception ex) { SetTqHistoryStatus("历史发布失败：" + ex.GetType().Name); }
        finally { _tqHistoryOperation = null; _tqHistoryCancellation?.Dispose(); _tqHistoryCancellation = null; TqHistoryCheckButton.IsEnabled = true; TqHistoryDownloadButton.IsEnabled = true; TqHistoryCancelButton.IsEnabled = false; UpdateTqHistoryCalendarReadiness(); _contextGate.Release(); }
    }

    private void TqHistoryCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tqHistoryCancellation is { IsCancellationRequested: false }) { SetTqHistoryStatus("正在取消历史任务……"); _tqHistoryCancellation.Cancel(); }
    }

    private async void TqLiveStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tqLiveOperation is { IsCompleted: false }) return;
        if (_strategyDescriptor is null) { TqLiveStatusText.Text = "请先在策略页加载并校验策略"; return; }
        if (!TryGetTqCredentials(out var credentials, out var error)) { TqLiveStatusText.Text = error; return; }
        var symbol = GetTqLiveSymbol();
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Contains('@') || symbol.Contains("888", StringComparison.OrdinalIgnoreCase) || symbol.Contains("999", StringComparison.OrdinalIgnoreCase)) { TqLiveStatusText.Text = "请选择或输入完整具体合约，不能使用主连/指数/888/999"; return; }
        if (TqLiveDurationCombo.SelectedItem is not ComboBoxItem durationItem || !int.TryParse(durationItem.Tag?.ToString(), out var duration) || duration <= 0) { TqLiveStatusText.Text = "实时周期无效"; return; }
        if (!int.TryParse(LiveCooldownText.Text.Trim(), out var cooldownSeconds) || cooldownSeconds < 0) { TqLiveStatusText.Text = "冷却秒数无效，请填写不小于 0 的整数"; return; }
        var instanceId = string.IsNullOrWhiteSpace(LiveInstanceIdText.Text) ? symbol + "-" + duration : LiveInstanceIdText.Text.Trim();
        var strategyName = _strategyDescriptor.Name;
        IReadOnlyDictionary<string, JsonElement> strategyParameters;
        try { strategyParameters = SignalReplayRunner.ValidateParameters(_strategyDescriptor, BuildParametersOrDefaults()); }
        catch (Exception parameterError) { TqLiveStatusText.Text = "策略参数无效：" + parameterError.Message; return; }
        var strategyPath = _strategyDescriptor.StrategyPath;
        var warmupBars = SignalReplayRunner.RequiredWarmupBars(_strategyDescriptor, strategyParameters);
        var parametersJson = JsonSerializer.Serialize(strategyParameters);
        var parametersSummary = string.Join("，", strategyParameters.Select(item => $"{item.Key}={item.Value}"));
        var instance = new LiveReminderInstance(instanceId, symbol, duration, strategyName, _strategyDescriptor.Version, _strategyDescriptor.StrategySha256, LiveReminderState.Starting, LiveDesktopCheck.IsChecked == true, LiveSoundCheck.IsChecked == true, cooldownSeconds, null, null, null, null, null, DateTimeOffset.UtcNow);
        string instanceError = "提醒账本不可用";
        if (_liveNotifyStore is null || !_liveNotifyStore.TryCreateOrUpdateInstance(instance, out instanceError)) { TqLiveStatusText.Text = "提醒实例未启动：" + instanceError; return; }
        _liveNotifyStore.ConfigureStrategy(instanceId, strategyPath, strategyName, _strategyDescriptor.Version, _strategyDescriptor.StrategySha256, parametersJson, parametersSummary, warmupBars);
        _liveInstanceId = instanceId;
        RefreshLiveReminderList();
        _tqLiveCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _tqLiveCancellation = cancellation;
        TqLiveStartButton.IsEnabled = false; TqLiveStopButton.IsEnabled = true;
        try { _tqLiveOperation = RunTqLiveAsync(credentials, symbol, duration, instanceId, cancellation.Token); await _tqLiveOperation; }
        catch (OperationCanceledException) { TqLiveStatusText.Text = "实时会话已停止"; if (_liveNotifyStore is not null) { _liveNotifyStore.UpdateInstanceState(instanceId, LiveReminderState.Stopped); _liveNotifyStore.SetStrategyStatus(instanceId, "stopped"); } }
        catch (Exception ex) { TqLiveStatusText.Text = "实时会话失败：" + ex.GetType().Name; if (_liveNotifyStore is not null) _liveNotifyStore.UpdateInstanceState(instanceId, LiveReminderState.Failed); }
        finally
        {
            if (_liveStrategySession is not null) { await _liveStrategySession.DisposeAsync(); _liveStrategySession = null; }
            _tqLiveOperation = null; _tqLiveCancellation?.Dispose(); _tqLiveCancellation = null; TqLiveStartButton.IsEnabled = true; TqLiveStopButton.IsEnabled = false; RefreshLiveReminderList();
        }
    }

    private void TqLiveStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tqLiveCancellation is { IsCancellationRequested: false }) { TqLiveStatusText.Text = "正在停止提醒实例……"; _tqLiveCancellation.Cancel(); }
        else if (_liveInstanceId is not null && _liveNotifyStore is not null) { _liveNotifyStore.UpdateInstanceState(_liveInstanceId, LiveReminderState.Stopped); RefreshLiveReminderList(); }
    }

    private void LivePauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_liveInstanceId is null || _liveNotifyStore is null) { TqLiveStatusText.Text = "请先创建提醒实例"; return; }
        var instance = _liveNotifyStore.GetInstance(_liveInstanceId);
        if (instance is null) return;
        var paused = instance.NotificationsPausedUntil is { } until && until > DateTimeOffset.UtcNow;
        _liveNotifyStore.SetNotificationOptions(instance.InstanceId, instance.DesktopNotificationsEnabled, instance.SoundEnabled, paused ? null : DateTimeOffset.UtcNow.AddMinutes(30));
        TqLiveStatusText.Text = paused ? "通知已恢复；计算和信号记录继续" : "通知已暂停 30 分钟；计算和信号记录继续";
        RefreshLiveReminderList();
    }

    private void LiveSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_liveInstanceId is null || _liveNotifyStore is null) { TqLiveStatusText.Text = "请先创建提醒实例"; return; }
        var instance = _liveNotifyStore.GetInstance(_liveInstanceId);
        if (instance is null) return;
        _liveNotifyStore.SetNotificationOptions(instance.InstanceId, instance.DesktopNotificationsEnabled, !instance.SoundEnabled, instance.NotificationsPausedUntil);
        TqLiveStatusText.Text = instance.SoundEnabled ? "已静音；桌面通知和软件内记录继续" : "声音已开启";
        RefreshLiveReminderList();
    }

    private void LiveTestNotificationButton_Click(object sender, RoutedEventArgs e)
    {
        _liveNotificationSink?.ShowTest(this);
        TqLiveStatusText.Text = "测试通知已触发；不会写入真实信号账本";
    }

    private void RefreshLiveReminderList()
    {
        if (_liveNotifyStore is null || !IsInitialized) return;
        var instances = _liveNotifyStore.GetInstances();
        LiveReminderListText.Text = instances.Count == 0
            ? "尚无提醒实例"
            : string.Join(Environment.NewLine, instances.Select(instance =>
                $"{instance.InstanceId} | {instance.Symbol} | {PeriodLabel(instance.DurationSeconds)} | {instance.StrategyName} | {instance.State}" + Environment.NewLine +
                $"最近完成K线：{instance.LastCompleteBarEnd:MM-dd HH:mm}；最近计算：{instance.LastCalculationAt:HH:mm:ss}；最近信号：{instance.LastSignalSummary ?? "暂无"}"));
        var instanceLookup = instances.ToDictionary(item => item.InstanceId, StringComparer.Ordinal);
        var attempts = _liveNotifyStore.GetAttempts(_liveInstanceId).GroupBy(item => item.SignalId).ToDictionary(group => group.Key, group => string.Join("/", group.Select(item => $"{item.Channel}:{item.Status}")), StringComparer.Ordinal);
        var signals = _liveNotifyStore.GetSignals(_liveInstanceId).Take(50).ToList();
        LiveSignalHistoryText.Text = signals.Count == 0
            ? "尚无软件内信号记录"
            : string.Join(Environment.NewLine, signals.Select(signal =>
            $"{signal.BarEnd:MM-dd HH:mm:ss} | {signal.Symbol} | {PeriodLabel(signal.DurationSeconds)} | {instanceLookup.GetValueOrDefault(signal.InstanceId)?.StrategyName ?? signal.StrategyVersion} | {signal.Category} | {signal.Direction} | 来源:{signal.SourcePhase} | 通知:{attempts.GetValueOrDefault(signal.SignalId, "无尝试记录")}"));
        var strategyConfig = _liveInstanceId is null ? null : _liveNotifyStore.GetStrategyConfig(_liveInstanceId);
        LiveStrategyRuntimeText.Text = strategyConfig is null
            ? "策略状态：未配置"
            : $"策略状态：{StrategyStatusLabel(strategyConfig.Status)}　版本：{strategyConfig.StrategyVersion}{Environment.NewLine}参数：{strategyConfig.ParametersSummary}{Environment.NewLine}预热：{strategyConfig.WarmupBarsProcessed}/{strategyConfig.WarmupBarsRequired}　{(strategyConfig.LastError ?? "")}";
    }

    private static string StrategyStatusLabel(string status) => status switch
    {
        "loaded" => "已加载", "warming_up" => "预热中", "running" => "运行", "recovering" => "恢复中", "failed" => "策略故障", "stopped" => "已停止", _ => status
    };

    private static string PeriodLabel(int durationSeconds) => durationSeconds switch
    {
        60 => "1分钟", 300 => "5分钟", 900 => "15分钟", 1800 => "30分钟", 3600 => "60分钟", 86400 => "日线", _ => $"{durationSeconds}秒"
    };

    private async Task RunTqLiveAsync(TqSdkCredentials credentials, string symbol, int duration, string instanceId, CancellationToken cancellationToken)
    {
        var bridge = Path.Combine(AppContext.BaseDirectory, "worker", "qh_tqsdk_bridge.py");
        var coordinator = new LiveSessionCoordinator(_store.RootDirectory);
        var previous = coordinator.LoadState();
        var calendarNow = DateTimeOffset.Now;
        if (!HistoricalCalendarResolver.TryLoad(_store.RootDirectory, symbol, calendarNow, calendarNow.Add(TimeSpan.FromSeconds(duration)), out var tradingDays, out var sessionRanges, out var calendarSource, out _, out var calendarError))
            throw new InvalidOperationException(calendarError);
        _liveNotifyStore?.UpdateInstanceState(instanceId, LiveReminderState.Starting);
        TqLiveStatusText.Text = "阶段 1/2：准备项目隔离 TqSdk 依赖……";
        var runtime = await TqSdkConnectionWorkflow.PrepareAsync(new TqSdkGateway(), cancellationToken);
        if (!runtime.Success) throw new InvalidOperationException($"依赖准备未完成：{runtime.ErrorKind}; {runtime.Message}");
        _liveNotifyStore?.UpdateInstanceState(instanceId, LiveReminderState.WarmingUp);
        var strategyWorker = Path.Combine(AppContext.BaseDirectory, "worker", "qh_signal_worker.py");
        _liveStrategySession = await LiveStrategySession.StartAsync(_liveNotifyStore!, instanceId, strategyWorker, cancellationToken);
        TqLiveStatusText.Text = $"策略已加载（{_strategyDescriptor?.Version}），依赖已就绪（TqSdk {runtime.Value?.TargetVersion ?? "unknown"}），正在等待实时握手……";
        var session = new TqSdkLiveSession(PythonRuntime.ResolveExecutable(), bridge, credentials, [symbol], duration, previous?.LastCompleteBarEnd, tradingDays, sessionRanges, calendarSource);
        var sessionId = "wpf-" + Guid.NewGuid().ToString("N");
        await foreach (var item in session.RunEventsAsync(cancellationToken))
        {
            switch (item.Kind)
            {
                case LiveSessionEventKind.Connected:
                    await coordinator.AcceptEventAsync(sessionId, item, cancellationToken);
                    _liveNotifyStore?.UpdateInstanceState(instanceId, LiveReminderState.WarmingUp);
                    TqLiveStatusText.Text = "实时会话已连接，等待完成 bar……";
                    break;
                case LiveSessionEventKind.Paused:
                    await coordinator.AcceptEventAsync(sessionId, item, cancellationToken);
                    _liveNotifyStore?.UpdateInstanceState(instanceId, LiveReminderState.Paused);
                    TqLiveStatusText.Text = "实时会话已暂停，正在等待恢复；不消费新 bar";
                    break;
                case LiveSessionEventKind.Recovered:
                    if (item.Bar is not null)
                    {
                        var recovery = await coordinator.AcceptEventAsync(sessionId, item, cancellationToken);
                        if (_liveStrategySession is not null && _liveReminderEngine is not null) foreach (var recoveryBar in recovery) { var evaluation = await _liveStrategySession.EvaluateAsync(recoveryBar, cancellationToken); if (evaluation.Candidates.Count > 0) await _liveReminderEngine.AcceptCompletedBarAsync(instanceId, recoveryBar, evaluation.Candidates, cancellationToken, evaluation.SourcePhase); }
                        _liveNotifyStore?.UpdateInstanceState(instanceId, LiveReminderState.Recovering, item.Bar.BarEnd, DateTimeOffset.UtcNow);
                        RefreshLiveReminderList();
                        TqLiveStatusText.Text = recovery.Count > 0 ? $"已静默补缺：{item.Bar.BarEnd:O}" : "恢复 bar 已去重";
                    }
                    else if (item.RecoverySilent)
                    {
                        await coordinator.AcceptEventAsync(sessionId, item, cancellationToken);
                        TqLiveStatusText.Text = "实时序列已就绪，正在静默补缺……";
                    }
                    else
                    {
                        await coordinator.AcceptEventAsync(sessionId, item, cancellationToken);
                        TqLiveStatusText.Text = "实时会话已恢复，继续消费新 bar……";
                    }
                    break;
                case LiveSessionEventKind.Bar:
                    if (item.Bar is not null)
                    {
                        var accepted = await coordinator.AcceptEventAsync(sessionId, item, cancellationToken);
                        if (_liveStrategySession is not null && _liveReminderEngine is not null) foreach (var acceptedBar in accepted) { var evaluation = await _liveStrategySession.EvaluateAsync(acceptedBar, cancellationToken); if (evaluation.Candidates.Count > 0) await _liveReminderEngine.AcceptCompletedBarAsync(instanceId, acceptedBar, evaluation.Candidates, cancellationToken, evaluation.SourcePhase); }
                        _liveNotifyStore?.UpdateInstanceState(instanceId, LiveReminderState.Running, accepted.LastOrDefault()?.BarEnd, DateTimeOffset.UtcNow);
                        RefreshLiveReminderList();
                        if (accepted.Count > 0) TqLiveStatusText.Text = $"已记录完成 bar：{accepted[^1].Symbol} {accepted[^1].BarEnd:O}；恢复数据静默去重";
                    }
                    break;
                case LiveSessionEventKind.Failed:
                    await coordinator.AcceptEventAsync(sessionId, item, cancellationToken);
                    _liveNotifyStore?.UpdateInstanceState(instanceId, LiveReminderState.Failed);
                    TqLiveStatusText.Text = $"实时会话未运行：{item.ErrorKind}；{item.Message}";
                    throw new InvalidOperationException($"{item.ErrorKind}: {item.Message}");
            }
        }
    }

    private void TqDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _tqCredentialVault.Delete();
            TqPasswordBox.Clear();
            TqSdkStatusText.Text = "本机凭据已删除；默认离线";
        }
        catch { TqSdkStatusText.Text = "凭据删除失败；未显示详细异常，请检查当前 Windows 用户保护状态"; }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try { await ImportButtonCoreAsync(); }
        finally { _contextGate.Release(); }
    }

    private async Task ImportButtonCoreAsync()
    {
        var dialog = new OpenFileDialog { Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*", Title = "选择本地 K 线 CSV" };
        if (dialog.ShowDialog() != true) return;
        _taskCancellation?.Dispose();
        _taskCancellation = new CancellationTokenSource();
        SetTaskState(true);
        try
        {
            var progress = new Progress<string>(message => SetStatus(message));
            _importOperation = Task.Run(() => _importer.ImportAsync(dialog.FileName, _store, progress, _taskCancellation.Token), _taskCancellation.Token);
            var result = await _importOperation;
            ShowQuality(result);
            if (result.Published)
            {
                SetStatus($"导入成功：{result.SnapshotId}");
                DataStatusText.Text = "已有快照";
                await LoadSnapshotAsync(result.SnapshotId!);
                RefreshSnapshotList();
            }
            else SetStatus(result.Message);
        }
        catch (OperationCanceledException) { SetStatus("正在取消 → 已取消，未发布不完整快照"); }
        catch (Exception ex) { SetStatus("导入失败：" + ex.Message); AppendLog(ex.ToString()); }
        finally { _importOperation = null; SetTaskState(false); }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_taskCancellation is { IsCancellationRequested: false })
        {
            SetStatus("正在取消");
            _taskCancellation.Cancel();
        }
    }

    private async void LoadSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try { await LoadSnapshotButtonCoreAsync(); }
        finally { _contextGate.Release(); }
    }

    private async Task LoadSnapshotButtonCoreAsync()
    {
        if (SnapshotCombo.SelectedItem is not SnapshotInfo snapshot) { SetStatus("请选择本地快照"); return; }
        try { await LoadSnapshotAsync(snapshot.SnapshotId); SetStatus($"已加载：{snapshot.DisplayName}"); }
        catch (Exception ex) { SetStatus("加载快照失败：" + ex.Message); AppendLog(ex.ToString()); }
    }

    private void RefreshSnapshotList()
    {
        try
        {
            var snapshots = _store.GetSnapshots();
            SnapshotCombo.ItemsSource = snapshots;
            if (_currentSnapshotId is not null) SnapshotCombo.SelectedItem = snapshots.FirstOrDefault(item => item.SnapshotId == _currentSnapshotId);
            UpdateCurrentDataSelection();
            RefreshTqHistorySymbolOptions();
            RefreshTqLiveSymbolOptions();
        }
        catch (Exception ex) { AppendLog("读取本地快照清单失败：" + ex.Message); }
    }

    private void UpdateCurrentDataSelection()
    {
        if (_currentSnapshotInfo is null)
        {
            CurrentDataSelectionText.Text = "尚未加载数据；请从下方列表选择，或导入 CSV。";
            CurrentDataOnStrategyText.Text = "尚未加载数据；请先到“数据”页选择 15/30/60 分钟数据。";
            DataStatusText.Text = "未选择";
            _syncingPeriodSwitch = true;
            StrategyPeriodCombo.ItemsSource = Array.Empty<SnapshotInfo>();
            StrategyPeriodCombo.SelectedItem = null;
            _syncingPeriodSwitch = false;
            return;
        }

        var summary = $"当前已加载：{_currentSnapshotInfo.Symbol} · {_currentSnapshotInfo.PeriodDisplayName} · {_currentSnapshotInfo.FirstTimestamp:yyyy-MM-dd HH:mm}～{_currentSnapshotInfo.LastTimestamp:yyyy-MM-dd HH:mm} · {_currentSnapshotInfo.RowCount:N0} 根";
        CurrentDataSelectionText.Text = summary;
        CurrentDataOnStrategyText.Text = summary;
        DataStatusText.Text = $"{_currentSnapshotInfo.Symbol} · {_currentSnapshotInfo.PeriodDisplayName} · {_currentSnapshotInfo.RowCount:N0} 根";
        if (TqHistoryStartDatePicker.SelectedDate is null) TqHistoryStartDatePicker.SelectedDate = _currentSnapshotInfo.FirstTimestamp.Date;
        if (TqHistoryEndDatePicker.SelectedDate is null) TqHistoryEndDatePicker.SelectedDate = _currentSnapshotInfo.LastTimestamp.Date;
        try
        {
            var candidates = _store.GetSnapshots()
                .Where(item => string.Equals(item.Symbol, _currentSnapshotInfo.Symbol, StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => item.Period, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Period, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _syncingPeriodSwitch = true;
            StrategyPeriodCombo.ItemsSource = candidates;
            StrategyPeriodCombo.SelectedItem = candidates.FirstOrDefault(item => item.SnapshotId == _currentSnapshotInfo.SnapshotId);
            _syncingPeriodSwitch = false;
        }
        catch (Exception ex)
        {
            _syncingPeriodSwitch = false;
            AppendLog("读取周期切换列表失败：" + ex.Message);
        }
    }

    private async void StrategyPeriodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _syncingPeriodSwitch || StrategyPeriodCombo.SelectedItem is not SnapshotInfo snapshot || snapshot.SnapshotId == _currentSnapshotId) return;
        if (!await _contextGate.WaitAsync(0))
        {
            _syncingPeriodSwitch = true;
            StrategyPeriodCombo.SelectedItem = _currentSnapshotInfo;
            _syncingPeriodSwitch = false;
            SetStatus("当前已有回测或数据任务运行，请完成后再切换周期");
            return;
        }
        try
        {
            await LoadSnapshotAsync(snapshot.SnapshotId);
            SetStatus($"已自动切换：{snapshot.Symbol} · {snapshot.PeriodDisplayName} · {snapshot.RowCount:N0} 根");
        }
        catch (Exception ex)
        {
            _syncingPeriodSwitch = true;
            StrategyPeriodCombo.SelectedItem = _currentSnapshotInfo;
            _syncingPeriodSwitch = false;
            SetStatus("切换周期失败：" + ex.Message);
            AppendLog(ex.ToString());
        }
        finally { _contextGate.Release(); }
    }

    private void ChooseStrategyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_contextGate.Wait(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try
        {
        var dialog = new OpenFileDialog { Filter = "Python 策略 (*.py)|*.py|所有文件 (*.*)|*.*", Title = "选择本地可信策略" };
        if (dialog.ShowDialog() == true)
        {
            StrategyPathText.Text = dialog.FileName;
            InvalidateStrategySelection("已选择新策略，请重新加载/校验");
        }
        }
        finally { _contextGate.Release(); }
    }

    private async void LoadStrategyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try { await LoadStrategyAsync(resetParameters: true); }
        finally { _contextGate.Release(); }
    }

    private async void ResetStrategyDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try
        {
            if (_strategyDescriptor is null)
            {
                await LoadStrategyAsync(resetParameters: true);
                return;
            }

            StrategyParametersText.Text = JsonSerializer.Serialize(
                _strategyDescriptor.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Default),
                new JsonSerializerOptions { WriteIndented = false });
            BuildStrategyParameterForm();
            SetDefaultReplayRange();
            SetStatus("已恢复策略默认参数；历史 run 保持只读");
        }
        finally { _contextGate.Release(); }
    }

    private void CancelStrategyButton_Click(object sender, RoutedEventArgs e) => _strategyLoadCancellation?.Cancel();

    private void StrategyPathText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_restoringRunView) return;
        try
        {
            if (IsInitialized && _strategyDescriptor is not null && !string.Equals(Path.GetFullPath(StrategyPathText.Text.Trim()), _strategyDescriptor.StrategyPath, StringComparison.OrdinalIgnoreCase))
                InvalidateStrategySelection("策略路径已变化，请重新加载/校验");
        }
        catch { InvalidateStrategySelection("策略路径无效，请重新选择"); }
    }

    private void StrategyPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || StrategyPresetCombo is null || StrategyPathText is null) return;
        UpdateStrategyCardForPreset();
        if (_restoringRunView) return;
        if (StrategyPresetCombo.SelectedItem is not ComboBoxItem item) return;
        var fileName = item.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(fileName)) return;
        var path = Path.Combine(AppContext.BaseDirectory, "strategies", fileName);
        if (!File.Exists(path))
        {
            SetStatus("内置策略文件不存在，请检查完整包目录");
            return;
        }
        StrategyPathText.Text = path;
        if (_strategyDescriptor is not null && !string.Equals(_strategyDescriptor.StrategyPath, path, StringComparison.OrdinalIgnoreCase))
            InvalidateStrategySelection("已切换策略，请加载并校验");
    }

    private void UpdateStrategyCardForPreset()
    {
        if (!IsInitialized || StrategyPresetCombo is null || StrategyCardText is null || StrategyCardMetaText is null || StrategyRunMeaningText is null || StartBacktestButton is null) return;
        var fileName = (StrategyPresetCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "sma_ema_cross.py";
        var card = fileName switch
        {
            "sma_ema_cross_trade.py" => ("快慢均线交叉时产生开仓/反向意图；成交、费用和权益由本地交易回放模拟。", "类型：交易　结果：本地模拟成交、手续费和盈亏"),
            "ema_dynamic_long_trade.py" => ("EMA 趋势成立后按 EMA 参考价挂多头限价单；未触及的订单逐根撤销重挂，止损与动态保本按下一根有效 K 线开盘模拟。", "类型：交易　限价触发＋本地模拟成交、手续费和盈亏"),
            "period_signal.py" => ("指标预热完成后产生测试信号，适合检查数据、预热和回放链路。", "类型：信号　结果：只看信号，不生成成交和盈亏"),
            _ => ("快慢均线交叉时产生做多/做空信号；只做信号研究，不产生成交。", "类型：信号　结果：只看信号，不生成成交和盈亏")
        };
        StrategyCardText.Text = card.Item1;
        StrategyCardMetaText.Text = card.Item2;
        StrategyRunMeaningText.Text = fileName is "sma_ema_cross_trade.py" or "ema_dynamic_long_trade.py"
            ? "交易策略：会进入本地模拟成交，并计算手续费、净利和权益；不会向真实账户下单。"
            : "信号策略：只产生研究信号，不产生成交、手续费或盈亏。";
        SetBacktestButtonKind(fileName is "sma_ema_cross_trade.py" or "ema_dynamic_long_trade.py");
    }

    private void UpdateStrategyCardFromDescriptor()
    {
        if (_strategyDescriptor is null) return;
        var kind = string.Equals(_strategyDescriptor.Kind, "trade", StringComparison.OrdinalIgnoreCase) ? "交易" : "信号";
        StrategyCardMetaText.Text = $"类型：{kind}　当前参数预热：{CurrentStrategyWarmupBars()} 根　参数：{string.Join("、", _strategyDescriptor.Parameters.Keys.Select(ParameterDisplayName))}";
        StrategyCardText.Text = _strategyDescriptor.Name;
        StrategyRunMeaningText.Text = kind == "交易"
            ? "交易策略：会进入本地模拟成交，并计算手续费、净利和权益；不会向真实账户下单。"
            : "信号策略：只产生研究信号，不产生成交、手续费或盈亏。";
        SetBacktestButtonKind(kind == "交易");
        UpdateLiveStrategySelectionSummary();
    }

    private int CurrentStrategyWarmupBars()
    {
        if (_strategyDescriptor is null) return 0;
        try { return SignalReplayRunner.RequiredWarmupBars(_strategyDescriptor, BuildParametersOrDefaults()); }
        catch { return _strategyDescriptor.WarmupBars; }
    }

    private void SyncStrategyPresetSelectionToDescriptor()
    {
        if (_strategyDescriptor is null || StrategyPresetCombo is null) return;
        var strategyFileName = Path.GetFileName(_strategyDescriptor.StrategyPath);
        var targetItem = StrategyPresetCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), strategyFileName, StringComparison.OrdinalIgnoreCase));
        if (targetItem is null && string.Equals(_strategyDescriptor.Kind, "trade", StringComparison.OrdinalIgnoreCase))
            targetItem = StrategyPresetCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "sma_ema_cross_trade.py");
        if (targetItem is null || ReferenceEquals(StrategyPresetCombo.SelectedItem, targetItem)) return;
        var previous = _restoringRunView;
        _restoringRunView = true;
        try { StrategyPresetCombo.SelectedItem = targetItem; }
        finally { _restoringRunView = previous; }
    }

    private void UpdateLiveStrategySelectionSummary()
    {
        if (LiveStrategySelectionText is null) return;
        if (_strategyDescriptor is null)
        {
            LiveStrategySelectionText.Text = "尚未加载并校验策略；请先到策略页选择。";
            return;
        }
        var kind = string.Equals(_strategyDescriptor.Kind, "trade", StringComparison.OrdinalIgnoreCase) ? "交易" : "信号";
        LiveStrategySelectionText.Text = $"已加载并校验：{_strategyDescriptor.Name} v{_strategyDescriptor.Version} · {kind} · 当前参数预热 {CurrentStrategyWarmupBars()} 根 · 摘要 {_strategyDescriptor.StrategySha256[..12]}";
    }

    private void SetBacktestButtonKind(bool isTrade)
    {
        if (StartBacktestButton is null) return;
        StartBacktestButton.Content = isTrade ? "开始模拟交易回测（有盈亏）" : "开始信号回放（无盈亏）";
    }

    private void BuildStrategyParameterForm()
    {
        StrategyParameterFormPanel.Children.Clear();
        _strategyParameterTextBoxes.Clear();
        _strategyParameterComboBoxes.Clear();
        if (_strategyDescriptor is null)
        {
            StrategyParameterFormHint.Text = "加载策略后显示参数默认值和范围。";
            return;
        }
        var configuredValues = ReadConfiguredStrategyParameters();
        _syncingStrategyParameterForm = true;
        try
        {
            foreach (var pair in _strategyDescriptor.Parameters)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
                var label = new TextBlock { Text = ParameterDisplayName(pair.Key), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);
                var parameter = pair.Value;
                if (parameter.Choices is { Count: > 0 })
                {
                    var combo = new ComboBox { Height = 30, ToolTip = $"{pair.Key}；可选值：{string.Join(", ", parameter.Choices.Select(FormatParameterValue))}" };
                    foreach (var choice in parameter.Choices) combo.Items.Add(new ComboBoxItem { Content = FormatParameterValue(choice), Tag = choice });
                    var configuredText = configuredValues.TryGetValue(pair.Key, out var configured) ? FormatParameterValue(configured) : FormatParameterValue(parameter.Default);
                    combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), configuredText, StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0];
                    combo.SelectionChanged += (_, _) => SyncParametersJsonFromForm();
                    _strategyParameterComboBoxes[pair.Key] = combo;
                    Grid.SetColumn(combo, 1);
                    row.Children.Add(combo);
                }
                else
                {
                    var configuredText = configuredValues.TryGetValue(pair.Key, out var configured) ? FormatParameterValue(configured) : FormatParameterValue(parameter.Default);
                    var text = new TextBox { Height = 30, Text = configuredText, ToolTip = BuildParameterRangeHint(pair.Key, parameter) };
                    text.TextChanged += (_, _) => SyncParametersJsonFromForm();
                    _strategyParameterTextBoxes[pair.Key] = text;
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                }
                StrategyParameterFormPanel.Children.Add(row);
            }
            var usesBarCounts = _strategyDescriptor.Parameters.Keys.Any(name => name is "fast_period" or "slow_period" or "period");
            StrategyParameterFormHint.Text = usesBarCounts
                ? "快线/慢线等参数单位是 K 线根数，不是分钟；当前选 15/30/60 分钟周期后，根数对应不同实际时长。"
                : "表单值会转换为策略 JSON；范围和类型以策略声明为准。";
        }
        finally
        {
            _syncingStrategyParameterForm = false;
        }
        SyncParametersJsonFromForm();
    }

    private Dictionary<string, JsonElement> ReadConfiguredStrategyParameters()
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(StrategyParametersText.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return values;
            foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        }
        catch { }
        return values;
    }

    private static string ParameterDisplayName(string name) => name switch
    {
        "fast_period" => "快线根数",
        "slow_period" => "慢线根数",
        "average" => "均线类型",
        "period" => "指标根数",
        "quantity" => "数量",
        "scenario" => "场景",
        _ => name
    };

    private static string FormatParameterValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    private static string BuildParameterRangeHint(string name, StrategyParameter parameter)
    {
        var range = new List<string> { name };
        if (name is "fast_period" or "slow_period" or "period") range.Add("单位：K线根数");
        if (parameter.Min is { } min) range.Add($"最小 {FormatParameterValue(min)}");
        if (parameter.Max is { } max) range.Add($"最大 {FormatParameterValue(max)}");
        return string.Join("；", range);
    }

    private void SyncParametersJsonFromForm()
    {
        if (_syncingStrategyParameterForm || _strategyDescriptor is null) return;
        try
        {
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var pair in _strategyDescriptor.Parameters)
            {
                var raw = _strategyParameterComboBoxes.TryGetValue(pair.Key, out var combo)
                    ? (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ""
                    : _strategyParameterTextBoxes.TryGetValue(pair.Key, out var text) ? text.Text.Trim() : FormatParameterValue(pair.Value.Default);
                values[pair.Key] = ParseParameterValue(raw, pair.Value.Type);
            }
            _syncingStrategyParameterForm = true;
            StrategyParametersText.Text = JsonSerializer.Serialize(values);
        }
        catch (Exception ex)
        {
            StrategyParameterFormHint.Text = "参数表单暂未生成有效 JSON：" + ex.Message;
        }
        finally { _syncingStrategyParameterForm = false; }
    }

    private static JsonElement ParseParameterValue(string raw, string type)
    {
        return type.ToLowerInvariant() switch
        {
            "int" => JsonSerializer.SerializeToElement(int.Parse(raw, CultureInfo.InvariantCulture)),
            "float" or "number" or "decimal" => JsonSerializer.SerializeToElement(decimal.Parse(raw, CultureInfo.InvariantCulture)),
            "bool" => JsonSerializer.SerializeToElement(bool.Parse(raw)),
            _ => JsonSerializer.SerializeToElement(raw)
        };
    }

    private void StrategyParametersText_TextChanged(object sender, TextChangedEventArgs e)
    {
    }

    private void ApplyAdvancedParametersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_strategyDescriptor is null) { SetStatus("请先加载策略，再应用高级参数"); return; }
        try
        {
            using var document = JsonDocument.Parse(StrategyParametersText.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("参数必须是 JSON 对象");
            _syncingStrategyParameterForm = true;
            foreach (var pair in _strategyDescriptor.Parameters)
            {
                if (!document.RootElement.TryGetProperty(pair.Key, out var value)) continue;
                var formatted = FormatParameterValue(value);
                if (_strategyParameterTextBoxes.TryGetValue(pair.Key, out var text)) text.Text = formatted;
                if (_strategyParameterComboBoxes.TryGetValue(pair.Key, out var combo)) combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), formatted, StringComparison.OrdinalIgnoreCase));
            }
            SetStatus("高级 JSON 已应用到参数表单");
        }
        catch (Exception ex) { SetStatus("高级参数 JSON 无效：" + ex.Message); }
        finally { _syncingStrategyParameterForm = false; }
    }

    private void ApplyTradeFormButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = JsonSerializer.Deserialize<TradingConfig>(TradeConfigText.Text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } }) ?? new TradingConfig();
            config = config with
            {
                Lots = int.Parse(TradeLotsText.Text, CultureInfo.InvariantCulture),
                OpenFee = decimal.Parse(TradeOpenFeeText.Text, CultureInfo.InvariantCulture),
                OrdinaryCloseFee = decimal.Parse(TradeCloseFeeText.Text, CultureInfo.InvariantCulture),
                SlippageTicks = int.Parse(TradeSlippageText.Text, CultureInfo.InvariantCulture)
            };
            config.Validate();
            TradeConfigText.Text = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = false, Converters = { new JsonStringEnumConverter() } });
            SetStatus("交易设置已转换为回放配置");
        }
        catch (Exception ex) { SetStatus("交易设置无效：" + ex.Message); }
    }

    private async Task<bool> LoadStrategyAsync(bool resetParameters)
    {
        if (_strategyLoadOperation is { IsCompleted: false })
        {
            SetStatus("已有策略加载任务，请等待或取消");
            return false;
        }
        CancellationTokenSource? ownedCancellation = null;
        Task<StrategyDescriptor>? ownedOperation = null;
        try
        {
            var strategyPath = Path.GetFullPath(StrategyPathText.Text.Trim());
            var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "qh_signal_worker.py");
            if (!File.Exists(strategyPath) || !File.Exists(workerPath)) { SetStatus("策略或信号 Worker 不存在"); return false; }
            ownedCancellation = new CancellationTokenSource();
            _strategyLoadCancellation = ownedCancellation;
            CancelStrategyButton.IsEnabled = true;
            SetStatus("正在加载并校验策略");
            ownedOperation = StrategyCatalog.DescribeAsync(strategyPath, workerPath, ownedCancellation.Token);
            _strategyLoadOperation = ownedOperation;
            var descriptor = await ownedOperation;
            if (!string.Equals(Path.GetFullPath(StrategyPathText.Text.Trim()), descriptor.StrategyPath, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateStrategySelection("策略路径在加载期间发生变化，请重新加载");
                return false;
            }
            var rebuildParameterForm = resetParameters || _strategyDescriptor is null || !string.Equals(_strategyDescriptor.StrategySha256, descriptor.StrategySha256, StringComparison.OrdinalIgnoreCase);
            _strategyDescriptor = descriptor;
            if (resetParameters)
            {
                StrategyParametersText.Text = JsonSerializer.Serialize(_strategyDescriptor.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Default), new JsonSerializerOptions { WriteIndented = false });
                SetDefaultReplayRange();
            }
            if (rebuildParameterForm) BuildStrategyParameterForm();
            StrategyDescriptorText.Text = $"{_strategyDescriptor.Name} v{_strategyDescriptor.Version}{Environment.NewLine}类型={_strategyDescriptor.Kind}；摘要={_strategyDescriptor.StrategySha256[..12]}；当前参数预热={CurrentStrategyWarmupBars()} 根";
            UpdateStrategyCardFromDescriptor();
            SetStatus("策略已加载，可检查回放配置");
            return true;
        }
        catch (OperationCanceledException)
        {
            _strategyDescriptor = null;
            StrategyDescriptorText.Text = "策略加载已取消";
            UpdateLiveStrategySelectionSummary();
            SetStatus("策略加载已取消");
            return false;
        }
        catch (Exception ex)
        {
            _strategyDescriptor = null;
            StrategyDescriptorText.Text = "策略加载失败：" + ex.Message;
            UpdateLiveStrategySelectionSummary();
            SetStatus("策略加载失败");
            AppendLog(ex.ToString());
            return false;
        }
        finally
        {
            if (ReferenceEquals(_strategyLoadOperation, ownedOperation))
            {
                CancelStrategyButton.IsEnabled = false;
                if (ReferenceEquals(_strategyLoadCancellation, ownedCancellation)) _strategyLoadCancellation = null;
                _strategyLoadOperation = null;
                ownedCancellation?.Dispose();
            }
        }
    }

    private void InvalidateStrategySelection(string message)
    {
        _strategyLoadCancellation?.Cancel();
            _strategyDescriptor = null;
            StrategyDescriptorText.Text = message;
            UpdateLiveStrategySelectionSummary();
            StrategyParametersText.Text = "{}";
            StrategyParameterFormPanel.Children.Clear();
            _strategyParameterTextBoxes.Clear();
            _strategyParameterComboBoxes.Clear();
            var selectedFile = (StrategyPresetCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            SetBacktestButtonKind(selectedFile is "sma_ema_cross_trade.py" or "ema_dynamic_long_trade.py");
            StrategyParameterFormHint.Text = "加载策略后显示参数默认值和范围。";
            ClearSignalContext();
            SetStatus(message);
    }

    private void ClearSignalContext()
    {
        _viewingRunId = null;
        _lastSignalResult = null;
        _allSignals = [];
        _displayedSignals = [];
        _tradeSignals = [];
        _tradeFills = [];
        _tradeEquity = [];
        _tradeRunDirectory = null;
        _activeTradeRunId = null;
        _activeTradeRecord = null;
        RefreshTradeReport([]);
        RunIdText.Text = "";
        SignalGrid.ItemsSource = null;
        TradeFillGrid.ItemsSource = null;
        TradeResultText.Text = "尚无交易回放结果";
        TradeDetailText.Text = "";
        CurrentRunSummaryText.Text = "当前为新研究配置";
        OverviewText.Text = "尚无回测结果；完成回测或打开历史 run 后，这里会显示摘要。";
        OverviewRerunButton.IsEnabled = false;
        OverviewExportButton.IsEnabled = false;
        ResultTabs.SelectedIndex = 0;
        EquityCanvas.Children.Clear();
        ExportSignalsButton.IsEnabled = false;
        ExportTradeFillsButton.IsEnabled = false;
        ExportTradeEquityButton.IsEnabled = false;
        ExportTradeSummaryButton.IsEnabled = false;
        LoadTradeDetailButton.IsEnabled = false;
        ResultViewText.Text = "当前为新研究配置";
        DrawChart();
    }

    private string CurrentMarketSummary()
    {
        var bar = _bars.FirstOrDefault();
        return bar is null ? $"快照：{_currentSnapshotId ?? "未加载"}" : $"合约：{bar.Symbol} · 周期：{bar.Period ?? "未标注"}";
    }

    private string CurrentPositionSummary(string runId)
    {
        try
        {
            var latest = new TradeRunStore(_store.RootDirectory).ReadMany<PositionRecord>(runId, "positions.jsonl").LastOrDefault();
            return latest is null || latest.Quantity == 0 ? "空仓" : $"{latest.Direction} {latest.Quantity} 手";
        }
        catch { return "见持仓明细"; }
    }

    private void UpdateTradeOverview(TradeRunRecord record)
    {
        var strategy = string.IsNullOrWhiteSpace(record.StrategyName) ? Path.GetFileName(record.StrategySnapshotPath) : record.StrategyName;
        CurrentRunSummaryText.Text = $"当前 run：{record.RunId} · 状态：{record.Status} · {strategy}";
        OverviewText.Text = string.Join(Environment.NewLine,
            CurrentMarketSummary(),
            $"区间：{record.StartInclusive:yyyy-MM-dd HH:mm} ～ {record.EndExclusive:yyyy-MM-dd HH:mm}",
            $"策略：{strategy}",
            $"净收益：{record.NetPnl}　收益率：{record.ReturnRate}%　期末权益：{record.FinalEquity}",
            $"最大回撤：{record.MaxDrawdown}　成交数：{record.FillCount}　已平仓数：{record.ClosedTradeCount}",
            $"胜率：{(record.WinRate is null ? "不适用" : record.WinRate + "%")}　手续费：{record.TotalFees}　当前持仓：{CurrentPositionSummary(record.RunId)}");
        OverviewRerunButton.IsEnabled = !string.Equals(record.Status, "running", StringComparison.OrdinalIgnoreCase);
        OverviewExportButton.IsEnabled = true;
        ResultTabs.SelectedIndex = 0;
    }

    private void UpdateSignalOverview(SignalReplayResult result)
    {
        var strategy = _strategyDescriptor?.Name ?? Path.GetFileName(StrategyPathText.Text.Trim());
        CurrentRunSummaryText.Text = $"当前 run：{result.RunId} · 状态：{result.Status} · {strategy}";
        OverviewText.Text = string.Join(Environment.NewLine,
            CurrentMarketSummary(),
            $"区间：{StartInclusiveText.Text} ～ {EndExclusiveText.Text}",
            $"策略：{strategy}",
            $"信号数：{result.Signals.Count}　成交数：0　已平仓数：0",
            "净收益：不适用　收益率：不适用　期末权益：不适用　最大回撤：不适用",
            "胜率：不适用　手续费：0　当前持仓：信号策略不产生成交");
        OverviewRerunButton.IsEnabled = !string.Equals(result.Status, "running", StringComparison.OrdinalIgnoreCase);
        OverviewExportButton.IsEnabled = result.Signals.Count > 0;
        ResultTabs.SelectedIndex = 0;
    }

    private void OverviewEditStrategyButton_Click(object sender, RoutedEventArgs e)
    {
        WorkflowTabs.SelectedIndex = 1;
        if (_strategyDescriptor is not null)
        {
            StrategyParametersText.Text = JsonSerializer.Serialize(
                _strategyDescriptor.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Default),
                new JsonSerializerOptions { WriteIndented = false });
            BuildStrategyParameterForm();
            SetDefaultReplayRange();
            SetStatus("已返回策略页并恢复默认参数；旧 run 保持只读");
        }
        else
        {
            SetStatus("已返回策略页，可加载策略后使用默认参数重新回测；旧 run 保持只读");
        }
    }

    private void OverviewReloadDataButton_Click(object sender, RoutedEventArgs e)
    {
        WorkflowTabs.SelectedIndex = 0;
        SetStatus("已返回数据页，可重新选择合约、周期或行情快照");
    }

    private void GoToDataButton_Click(object sender, RoutedEventArgs e)
    {
        WorkflowTabs.SelectedIndex = 0;
        SetStatus("请在数据页选择已下载/已导入快照，或下载新的 15/30/60 分钟数据");
    }

    private void GoToStrategyButton_Click(object sender, RoutedEventArgs e)
    {
        WorkflowTabs.SelectedIndex = 1;
        SetStatus(_strategyDescriptor is null ? "请在策略页选择并加载校验策略" : "当前实时提醒将使用策略页已加载并校验的 descriptor");
    }

    private void OverviewRerunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewingRunId is not null) ClearSignalContext();
        WorkflowTabs.SelectedIndex = 1;
        StartBacktestButton_Click(sender, e);
    }

    private void OverviewExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTradeRecord is not null) ExportTradeSummaryButton_Click(sender, e);
        else if (_lastSignalResult is not null) ExportSignalsButton_Click(sender, e);
        else SetStatus("当前没有可导出的回测结果");
    }

    private void SetDefaultReplayRange()
    {
        if (_bars.Count == 0) return;
        var required = _strategyDescriptor is null ? 0 : SignalReplayRunner.RequiredWarmupBars(_strategyDescriptor, BuildParametersOrDefaults());
        var startIndex = Math.Min(Math.Max(required, 0), _bars.Count - 1);
        StartInclusiveText.Text = _bars[startIndex].Timestamp.ToString("O", CultureInfo.InvariantCulture);
        EndExclusiveText.Text = (_bars[^1].Timestamp + PeriodDuration(_bars[^1].Period)).ToString("O", CultureInfo.InvariantCulture);
    }

    private Dictionary<string, JsonElement> BuildParametersOrDefaults()
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (_strategyDescriptor is null) return result;
        foreach (var pair in _strategyDescriptor.Parameters) result[pair.Key] = pair.Value.Default.Clone();
        if (!string.IsNullOrWhiteSpace(StrategyParametersText.Text))
        {
            using var json = JsonDocument.Parse(StrategyParametersText.Text);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数 JSON 必须是对象");
            foreach (var property in json.RootElement.EnumerateObject()) result[property.Name] = property.Value.Clone();
        }
        return result;
    }

    private bool TryBuildReplayRequest(out SignalReplayRequest request, out string error)
    {
        request = default!;
        error = "";
        if (_currentSnapshotId is null || _strategyDescriptor is null) { error = "请先加载快照和策略"; return false; }
        if (!DateTimeOffset.TryParse(StartInclusiveText.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start) ||
            !DateTimeOffset.TryParse(EndExclusiveText.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end)) { error = "正式起止时间格式无效"; return false; }
        try
        {
            var parameters = SignalReplayRunner.ValidateParameters(_strategyDescriptor, BuildParametersOrDefaults());
            request = new SignalReplayRequest(_currentSnapshotId, _strategyDescriptor.StrategyPath, start, end, parameters, ExploreModeCheck.IsChecked == true);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private void CheckConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_contextGate.Wait(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try
        {
            if (!TryBuildReplayRequest(out var request, out var error)) { SetStatus("配置不可运行：" + error); return; }
            var bars = _store.QuerySnapshot(request.SnapshotId).Where(bar => bar.Timestamp < request.EndExclusive).ToArray();
            var coverage = SignalReplayRunner.EvaluateCoverage(_strategyDescriptor!, request.Parameters!, bars, request.StartInclusive, request.EndExclusive);
            var quality = _store.ReadQualityReport(request.SnapshotId);
            var qualityBlocked = !request.Explore && (quality.SuspectedGaps.Count > 0 || !quality.CanPublish);
            SetStatus(qualityBlocked ? $"配置阻止：严格质量门禁 gaps={quality.SuspectedGaps.Count}" : coverage.IsReady ? coverage.Message : "配置阻止：" + coverage.Message);
            AppendLog($"配置检查：{coverage.Message}; strict_blocked={qualityBlocked}; explore={request.Explore}");
        }
        catch (Exception ex) { SetStatus("配置检查失败：" + ex.Message); }
        finally { _contextGate.Release(); }
    }

    private async void ReplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try { await ReplayButtonCoreAsync(); }
        finally { _contextGate.Release(); }
    }

    private async void StartBacktestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try
        {
            if (_strategyDescriptor is null && !await LoadStrategyAsync(resetParameters: true)) return;
            if (_strategyDescriptor is null) { SetStatus("请先选择并加载策略"); return; }
            if (string.Equals(_strategyDescriptor.Kind, "trade", StringComparison.OrdinalIgnoreCase)) await TradeReplayButtonCoreAsync();
            else await ReplayButtonCoreAsync();
        }
        finally { _contextGate.Release(); }
    }

    private async Task ReplayButtonCoreAsync()
    {
        if (_replayOperation is { IsCompleted: false }) return;
        if (_viewingRunId is not null) { ClearSignalContext(); SetStatus("已退出只读结果视图，请确认新研究配置后再次开始"); return; }
        if (_currentSnapshotId is null || _bars.Count == 0) { SetStatus("请先加载已发布数据快照"); return; }
        if (!await LoadStrategyAsync(resetParameters: false)) return;
        if (!TryBuildReplayRequest(out var request, out var requestError)) { SetStatus("配置不可运行：" + requestError); return; }
        var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "qh_signal_worker.py");
        if (!File.Exists(workerPath)) { SetStatus("信号 Worker 不存在"); return; }
        _replayCancellation?.Dispose();
        _replayCancellation = new CancellationTokenSource();
        ReplayButton.IsEnabled = false;
        CancelReplayButton.IsEnabled = true;
        SetReplayControlsLocked(true);
        ExportSignalsButton.IsEnabled = false;
        try
        {
            var progress = new Progress<SignalReplayProgress>(item => SetStatus($"回放 {item.ProcessedRows}/{item.TotalRows}：{item.Stage}"));
            var runner = new SignalReplayRunner(_store, workerPath);
            _replayOperation = Task.Run(() => runner.ReplayAsync(request, progress, _replayCancellation.Token), _replayCancellation.Token);
            var result = await _replayOperation;
            _lastSignalResult = result;
            _allSignals = result.Signals;
            RunIdText.Text = result.RunId;
            ApplySignalFilter();
            UpdateSignalOverview(result);
            ExportSignalsButton.IsEnabled = result.Status == "completed";
            SetStatus(result.ErrorMessage is null ? $"信号回放完成：{result.Signals.Count} 条" : result.ErrorMessage);
            AppendLog($"run={result.RunId}; status={result.Status}; directory={result.RunDirectory}");
        }
        catch (OperationCanceledException) { SetStatus("信号回放已取消"); }
        catch (Exception ex) { SetStatus("信号回放失败：" + ex.Message); AppendLog(ex.ToString()); }
        finally
        {
            _replayOperation = null;
            _replayCancellation?.Dispose();
            _replayCancellation = null;
            ReplayButton.IsEnabled = true;
            CancelReplayButton.IsEnabled = false;
            SetReplayControlsLocked(false);
        }
    }

    private async void TradeReplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try { await TradeReplayButtonCoreAsync(); }
        finally { _contextGate.Release(); }
    }

    private async Task TradeReplayButtonCoreAsync()
    {
        if (_tradeReplayOperation is { IsCompleted: false }) return;
        if (_viewingRunId is not null) { ClearSignalContext(); SetStatus("已退出只读结果视图，请确认新研究配置后再次开始"); return; }
        if (_currentSnapshotId is null || _bars.Count == 0) { SetStatus("请先加载已发布数据快照"); return; }
        if (!await LoadStrategyAsync(resetParameters: false)) return;
        if (_strategyDescriptor is null || !string.Equals(_strategyDescriptor.Kind, "trade", StringComparison.OrdinalIgnoreCase)) { SetStatus("交易回放需要 kind=trade 策略"); return; }
        if (!DateTimeOffset.TryParse(StartInclusiveText.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start) ||
            !DateTimeOffset.TryParse(EndExclusiveText.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end)) { SetStatus("正式起止时间格式无效"); return; }
        try
        {
            var parameters = SignalReplayRunner.ValidateParameters(_strategyDescriptor, BuildParametersOrDefaults());
            var config = JsonSerializer.Deserialize<TradingConfig>(TradeConfigText.Text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } }) ?? new TradingConfig();
            config.Validate();
            _tradeReplayCancellation?.Dispose(); _tradeReplayCancellation = new CancellationTokenSource();
            TradeReplayButton.IsEnabled = false; CancelTradeReplayButton.IsEnabled = true; SetReplayControlsLocked(true);
            var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "qh_signal_worker.py");
            var request = new TradeReplayRequest(_currentSnapshotId, _strategyDescriptor.StrategyPath, start, end, config, parameters, ExploreModeCheck.IsChecked == true);
            var progress = new Progress<TradeReplayProgress>(item => SetStatus($"交易回放 {item.ProcessedRows}/{item.TotalRows}：{item.Stage}"));
            _tradeReplayOperation = Task.Run(() => new TradeReplayRunner(_store, workerPath).ReplayAsync(request, progress, _tradeReplayCancellation.Token), _tradeReplayCancellation.Token);
            var result = await _tradeReplayOperation;
            RunIdText.Text = result.RunId;
            var tradeStore = new TradeRunStore(_store.RootDirectory);
            var record = tradeStore.ReadRecord(result.RunId);
            _tradeRunDirectory = result.RunDirectory; _activeTradeRunId = result.RunId; _activeTradeRecord = record;
            _tradeFills = result.Fills;
            RefreshTradeReport(result.ClosedTrades);
            _tradeSignals = await Task.Run(() => tradeStore.ReadMany<SignalEvent>(result.RunId, "signals.jsonl"));
            _tradeEquity = await Task.Run(() => tradeStore.ReadEquitySample(result.RunId));
            TradeFillGrid.ItemsSource = _tradeFills;
            _allSignals = _tradeSignals; _displayedSignals = _tradeSignals; SignalGrid.ItemsSource = _displayedSignals;
            _lastSignalResult = new SignalReplayResult(record.RunId, record.Status, _tradeSignals, result.RunDirectory, record.ErrorMessage);
            TradeResultText.Text = $"{record.Status}；意图 {record.FormalIntentCount}；成交 {record.FillCount}；已平 {record.ClosedTradeCount}{Environment.NewLine}毛利 {record.TotalGrossPnl}；手续费 {record.TotalFees}；净利 {record.NetPnl}；权益 {record.FinalEquity}；收益率 {record.ReturnRate}%；最大回撤 {record.MaxDrawdown}；胜率 {(record.WinRate is null ? "不适用" : record.WinRate + "%")}{Environment.NewLine}正式覆盖：{record.CoverageMessage}；原始 run：{record.RunId}";
            TradeConfigText.Text = JsonSerializer.Serialize(record.Config, new JsonSerializerOptions { WriteIndented = false, Converters = { new JsonStringEnumConverter() } });
            UpdateTradeOverview(record);
            ExportTradeFillsButton.IsEnabled = true; ExportTradeEquityButton.IsEnabled = true; ExportTradeSummaryButton.IsEnabled = true; LoadTradeDetailButton.IsEnabled = true; ExportSignalsButton.IsEnabled = _tradeSignals.Count > 0;
            DrawEquityCurve(); DrawChart();
            SetStatus(result.ErrorMessage is null ? "交易回放完成" : result.ErrorMessage);
            AppendLog($"trade_run={result.RunId}; status={result.Status}; directory={result.RunDirectory}");
        }
        catch (OperationCanceledException) { SetStatus("交易回放已取消"); }
        catch (Exception ex) { SetStatus("交易回放失败：" + ex.Message); AppendLog(ex.ToString()); }
        finally
        {
            _tradeReplayOperation = null; _tradeReplayCancellation?.Dispose(); _tradeReplayCancellation = null;
            TradeReplayButton.IsEnabled = true; CancelTradeReplayButton.IsEnabled = false; SetReplayControlsLocked(false);
        }
    }

    private void CancelTradeReplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tradeReplayCancellation is { IsCancellationRequested: false }) { SetStatus("正在取消交易回放"); _tradeReplayCancellation.Cancel(); }
    }

    private void CopyTradeConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_contextGate.Wait(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try
        {
            if (_viewingRunId is not null) { ClearSignalContext(); SetStatus("已复制当前配置到新研究上下文；开始交易回放将创建新 run"); }
            else SetStatus("当前已经是新研究上下文");
        }
        finally { _contextGate.Release(); }
    }

    private void ExportTradeFillsButton_Click(object sender, RoutedEventArgs e) => ExportTradeFile("成交 CSV", "fills.csv", (store, runId, path) => store.ExportFillsCsv(runId, path));
    private void ExportTradeEquityButton_Click(object sender, RoutedEventArgs e) => ExportTradeFile("权益 CSV", "equity.csv", (store, runId, path) => store.ExportEquityCsv(runId, path));
    private void ExportTradeSummaryButton_Click(object sender, RoutedEventArgs e) => ExportTradeFile("交易摘要 JSON", "run.json", (store, runId, path) => store.ExportSummaryJson(runId, path));

    private void ExportTradeFile(string title, string defaultName, Action<TradeRunStore, string, string> export)
    {
        if (_activeTradeRunId is null || _tradeRunDirectory is null) { SetStatus("请先打开或完成交易 run"); return; }
        var dialog = new SaveFileDialog { Title = title, FileName = defaultName, Filter = "文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        try { export(new TradeRunStore(_store.RootDirectory), _activeTradeRunId, dialog.FileName); SetStatus($"已导出：{dialog.FileName}"); }
        catch (Exception ex) { SetStatus("导出失败：" + ex.Message); AppendLog(ex.ToString()); }
    }

    private async void LoadTradeDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tradeRunDirectory is null || TradeDetailCombo.SelectedItem is not ComboBoxItem item) return;
        var fileName = item.Content?.ToString(); if (string.IsNullOrWhiteSpace(fileName)) return;
        try
        {
            var path = Path.Combine(_tradeRunDirectory, fileName);
            TradeDetailText.Text = await Task.Run(() => File.Exists(path) ? string.Join(Environment.NewLine, File.ReadLines(path).Take(200)) : $"不存在：{fileName}");
        }
        catch (Exception ex) { TradeDetailText.Text = ex.Message; }
    }

    private void SetReplayControlsLocked(bool locked)
    {
        ImportButton.IsEnabled = !locked;
        LoadSnapshotButton.IsEnabled = !locked;
        SnapshotCombo.IsEnabled = !locked;
        StrategyPeriodCombo.IsEnabled = !locked;
        StartBacktestButton.IsEnabled = !locked;
        StrategyPresetCombo.IsEnabled = !locked;
        ChooseStrategyButton.IsEnabled = !locked;
        LoadStrategyButton.IsEnabled = !locked;
        CancelStrategyButton.IsEnabled = false;
        CheckConfigButton.IsEnabled = !locked;
        OpenRunButton.IsEnabled = !locked;
        OverviewEditStrategyButton.IsEnabled = !locked;
        OverviewReloadDataButton.IsEnabled = !locked;
        OverviewRerunButton.IsEnabled = !locked && (_activeTradeRecord is not null || _lastSignalResult is not null);
        OverviewExportButton.IsEnabled = !locked && (_activeTradeRecord is not null || _lastSignalResult is not null);
        ApplyFilterButton.IsEnabled = !locked;
        ApplyAdvancedParametersButton.IsEnabled = !locked;
        ApplyTradeFormButton.IsEnabled = !locked;
        TradeLotsText.IsEnabled = !locked;
        TradeOpenFeeText.IsEnabled = !locked;
        TradeCloseFeeText.IsEnabled = !locked;
        TradeSlippageText.IsEnabled = !locked;
        StrategyPathText.IsEnabled = !locked;
        StrategyParametersText.IsEnabled = !locked;
        StartInclusiveText.IsEnabled = !locked;
        EndExclusiveText.IsEnabled = !locked;
        ExploreModeCheck.IsEnabled = !locked;
        TradeConfigText.IsEnabled = !locked;
        ReplayButton.IsEnabled = !locked;
        TradeReplayButton.IsEnabled = !locked;
        CopyTradeConfigButton.IsEnabled = !locked;
    }

    private void CancelReplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_replayCancellation is { IsCancellationRequested: false })
        {
            SetStatus("正在取消信号回放");
            _replayCancellation.Cancel();
        }
    }

    private async void OpenRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try { await OpenRunButtonCoreAsync(); }
        finally { _contextGate.Release(); }
    }

    private async Task OpenRunButtonCoreAsync()
    {
        var runId = RunIdText.Text.Trim();
        if (runId.Length == 0) { SetStatus("请输入要打开的 run ID"); return; }
        try
        {
            var tradeStore = new TradeRunStore(_store.RootDirectory);
            var tradeRecordPath = Path.Combine(tradeStore.RunsDirectory, runId, "run.json");
            if (File.Exists(tradeRecordPath))
            {
                var tradeRecord = tradeStore.ReadRecord(runId);
                if (_store.GetSnapshots().All(item => item.SnapshotId != tradeRecord.SnapshotId)) { SetStatus($"交易 run 绑定的快照不存在：{tradeRecord.SnapshotId}"); return; }
                await LoadSnapshotAsync(tradeRecord.SnapshotId, clearSignalContext: true, setDefaultRange: false);
                var fills = await Task.Run(() => tradeStore.ReadMany<FillRecord>(runId, "fills.jsonl"));
                var curve = await Task.Run(() => tradeStore.ReadEquitySample(runId));
                _tradeSignals = await Task.Run(() => tradeStore.ReadMany<SignalEvent>(runId, "signals.jsonl"));
                _restoringRunView = true;
                try
                {
                    StrategyPathText.Text = tradeRecord.StrategySnapshotPath;
                    StrategyParametersText.Text = JsonSerializer.Serialize(tradeRecord.Parameters);
                    StartInclusiveText.Text = tradeRecord.StartInclusive.ToString("O", CultureInfo.InvariantCulture);
                    EndExclusiveText.Text = tradeRecord.EndExclusive.ToString("O", CultureInfo.InvariantCulture);
                    ExploreModeCheck.IsChecked = tradeRecord.Explore;
                }
                finally { _restoringRunView = false; }
                await LoadStrategyAsync(resetParameters: false);
                SyncStrategyPresetSelectionToDescriptor();
                _tradeFills = fills; _tradeEquity = curve; _tradeRunDirectory = Path.Combine(tradeStore.RunsDirectory, runId); _activeTradeRunId = runId;
                _activeTradeRecord = tradeRecord;
                RefreshTradeReport(await Task.Run(() => tradeStore.ReadMany<ClosedTradeRecord>(runId, "closed-trades.jsonl")));
                TradeFillGrid.ItemsSource = _tradeFills; RunIdText.Text = runId; _viewingRunId = runId;
                _allSignals = _tradeSignals; _displayedSignals = _tradeSignals; SignalGrid.ItemsSource = _displayedSignals;
                _lastSignalResult = new SignalReplayResult(tradeRecord.RunId, tradeRecord.Status, _tradeSignals, _tradeRunDirectory, tradeRecord.ErrorMessage);
                TradeConfigText.Text = JsonSerializer.Serialize(tradeRecord.Config, new JsonSerializerOptions { WriteIndented = false, Converters = { new JsonStringEnumConverter() } });
                TradeResultText.Text = $"只读交易结果：快照 {tradeRecord.SnapshotId}；区间 {tradeRecord.StartInclusive:O}～{tradeRecord.EndExclusive:O}；探索={tradeRecord.Explore}；{tradeRecord.Status}；成交 {tradeRecord.FillCount}；净利 {tradeRecord.NetPnl}；权益 {tradeRecord.FinalEquity}；胜率 {(tradeRecord.WinRate is null ? "不适用" : tradeRecord.WinRate + "%")}";
                UpdateTradeOverview(tradeRecord);
                ResultViewText.Text = "只读交易结果：配置/区间/探索模式已从原 run 恢复；复制后可创建新 run";
                ExportTradeFillsButton.IsEnabled = true; ExportTradeEquityButton.IsEnabled = true; ExportTradeSummaryButton.IsEnabled = true; LoadTradeDetailButton.IsEnabled = true; ExportSignalsButton.IsEnabled = _tradeSignals.Count > 0;
                DrawEquityCurve(); DrawChart();
                _viewportCount = Math.Min(240, _bars.Count);
                _viewportStart = Math.Max(0, _bars.Count - _viewportCount);
                DrawChart();
                SetStatus($"已只读打开交易 run：{runId}"); return;
            }
            var store = new SignalRunStore(_store.RootDirectory);
            var record = store.ReadRecord(runId);
            var snapshot = _store.GetSnapshots().FirstOrDefault(item => item.SnapshotId == record.SnapshotId);
            if (snapshot is null) { SetStatus($"run 绑定的快照不存在：{record.SnapshotId}"); return; }
            await LoadSnapshotAsync(record.SnapshotId, clearSignalContext: true, setDefaultRange: false);
            if (!string.Equals(_currentSnapshotId, record.SnapshotId, StringComparison.Ordinal)) { SetStatus("无法加载 run 绑定快照"); return; }
            _allSignals = store.ReadSignals(runId);
            _lastSignalResult = new SignalReplayResult(record.RunId, record.Status, _allSignals, Path.Combine(store.RunsDirectory, runId), record.ErrorMessage);
            _viewingRunId = runId;
            RunIdText.Text = runId;
            ResultViewText.Text = $"只读结果视图：快照 {record.SnapshotId}；不会重算原 run";
            ApplySignalFilter();
            UpdateSignalOverview(new SignalReplayResult(record.RunId, record.Status, _allSignals, Path.Combine(store.RunsDirectory, runId), record.ErrorMessage));
            ExportSignalsButton.IsEnabled = true;
            SetStatus($"已只读打开 run：{runId}，快照={record.SnapshotId}，状态={record.Status}，信号={_allSignals.Count}");
        }
        catch (Exception ex) { SetStatus("打开 run 失败：" + ex.Message); AppendLog(ex.ToString()); }
    }

    private void ApplyFilterButton_Click(object sender, RoutedEventArgs e) => ApplySignalFilter();

    private void ApplySignalFilter()
    {
        var category = string.IsNullOrWhiteSpace(CategoryFilterText.Text) ? null : CategoryFilterText.Text.Trim();
        var direction = string.IsNullOrWhiteSpace(DirectionFilterText.Text) ? null : DirectionFilterText.Text.Trim();
        DateTimeOffset? from = DateTimeOffset.TryParse(FromFilterText.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fromValue) ? fromValue : null;
        DateTimeOffset? to = DateTimeOffset.TryParse(ToFilterText.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var toValue) ? toValue : null;
        _displayedSignals = _allSignals.Where(signal =>
            (category is null || string.Equals(signal.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (direction is null || string.Equals(signal.Direction, direction, StringComparison.OrdinalIgnoreCase)) &&
            (from is null || signal.BarEnd >= from) && (to is null || signal.BarEnd < to)).ToArray();
        SignalGrid.ItemsSource = _displayedSignals;
        DrawChart();
    }

    private void SignalGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SignalGrid.SelectedItem is SignalEvent signal) LocateSignal(signal);
    }

    private void ExportSignalsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSignalResult is null) return;
        var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = $"{_lastSignalResult.RunId}-signals.csv" };
        if (dialog.ShowDialog() != true) return;
        new SignalRunStore(_store.RootDirectory).ExportCsv(_displayedSignals, dialog.FileName);
        SetStatus("信号已导出：" + dialog.FileName);
    }

    private static TimeSpan PeriodDuration(string period)
    {
        if (period.Length < 2 || !int.TryParse(period[..^1], out var value)) throw new InvalidDataException("不支持的周期：" + period);
        return char.ToLowerInvariant(period[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => throw new InvalidDataException("不支持的周期：" + period)
        };
    }

    private void StartWorkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_worker is { HasExited: false }) return;
        var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "qh_worker.py");
        if (!File.Exists(workerPath)) { SetStatus("找不到 Worker：" + workerPath); return; }
        try
        {
            var startInfo = new ProcessStartInfo(PythonRuntime.ResolveExecutable(), $"\"{workerPath}\"")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            var worker = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            worker.OutputDataReceived += WorkerOutputDataReceived;
            worker.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) PostToUi(() => AppendLog("Worker stderr: " + args.Data)); };
            worker.Exited += (_, _) => PostToUi(() => WorkerExited(worker));
            if (!worker.Start()) { worker.Dispose(); SetStatus("Worker 启动失败"); return; }
            _worker = worker;
            _workerProtocolCancellation?.Cancel();
            _workerProtocolCancellation = new CancellationTokenSource();
            _workerReady = false;
            _workerCancelPending = false;
            _workerStopAck = false;
            worker.BeginOutputReadLine();
            worker.BeginErrorReadLine();
            StartWorkerButton.IsEnabled = false;
            StopWorkerButton.IsEnabled = true;
            WorkerStatusText.Text = "Worker：已启动，等待 ready";
            _ = MonitorWorkerReadyAsync(worker, _workerProtocolCancellation.Token);
        }
        catch (Exception ex)
        {
            SetStatus("Worker 启动失败：" + ex.Message);
            AppendLog(ex.ToString());
            _worker?.Dispose();
            _worker = null;
        }
    }

    private void WorkerOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        PostToUi(() =>
        {
            try
            {
                using var json = JsonDocument.Parse(args.Data);
                var type = json.RootElement.GetProperty("type").GetString();
                switch (type)
                {
                    case "ready":
                        _workerReady = true;
                        WorkerStatusText.Text = "Worker：ready";
                        SendWorkerCommand("run");
                        CancelWorkerButton.IsEnabled = true;
                        break;
                    case "heartbeat": WorkerStatusText.Text = "Worker：运行中 " + DateTime.Now.ToString("HH:mm:ss"); break;
                    case "cancelled": _workerCancelPending = false; WorkerStatusText.Text = "Worker：已确认取消"; CancelWorkerButton.IsEnabled = false; break;
                    case "stopped": _workerStopAck = true; WorkerStatusText.Text = "Worker：已停止"; break;
                    case "error": AppendLog("Worker error: " + json.RootElement.GetProperty("message").GetString()); break;
                }
            }
            catch (Exception ex) { AppendLog("无法解析 Worker 消息：" + ex.Message); }
        });
    }

    private void CancelWorkerButton_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Worker 正在取消");
        _workerCancelPending = true;
        SendWorkerCommand("cancel");
        if (_worker is { HasExited: false } worker && _workerProtocolCancellation is not null)
            _ = MonitorWorkerCancelAsync(worker, _workerProtocolCancellation.Token);
    }

    private async void StopWorkerButton_Click(object sender, RoutedEventArgs e)
    {
        await StopWorkerAsync("用户停止");
    }

    private void SendWorkerCommand(string command)
    {
        try
        {
            if (_worker is not { HasExited: false }) { AppendLog($"Worker 命令未发送（进程不存在）：{command}"); return; }
            _worker.StandardInput.WriteLine(JsonSerializer.Serialize(new { command }));
            _worker.StandardInput.Flush();
        }
        catch (Exception ex) { AppendLog("发送 Worker 命令失败：" + ex.Message); }
    }

    private void WorkerExited(Process worker)
    {
        StartWorkerButton.IsEnabled = true;
        StopWorkerButton.IsEnabled = false;
        CancelWorkerButton.IsEnabled = false;
        WorkerStatusText.Text = worker.ExitCode == 0 ? "Worker：已退出" : $"Worker：异常退出 ({worker.ExitCode})";
        _workerProtocolCancellation?.Cancel();
        _workerReady = false;
        _workerCancelPending = false;
        if (ReferenceEquals(_worker, worker)) _worker = null;
        worker.Dispose();
    }

    private async Task StopWorkerAsync(string reason)
    {
        var worker = _worker;
        if (worker is null || worker.HasExited) return;
        SetStatus(reason + "：正在停止 Worker");
        _workerStopAck = false;
        SendWorkerCommand("stop");
        try
        {
            var exitTask = worker.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2))) != exitTask && !worker.HasExited)
            {
                AppendLog("Worker 未在超时内退出，执行受管理进程树清理");
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
            if (!_workerStopAck) AppendLog("Worker 已退出但未收到 stopped 确认");
        }
        catch (Exception ex)
        {
            AppendLog("Worker 停止异常：" + ex.Message);
            try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch { }
        }
    }

    private async Task MonitorWorkerReadyAsync(Process worker, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        catch (OperationCanceledException) { return; }
        if (!token.IsCancellationRequested && !worker.HasExited && !_workerReady)
            PostToUi(() => { WorkerStatusText.Text = "Worker：ready 超时"; AppendLog("Worker 启动后 3 秒内未收到 ready"); });
    }

    private async Task MonitorWorkerCancelAsync(Process worker, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
        catch (OperationCanceledException) { return; }
        if (!token.IsCancellationRequested && !worker.HasExited && _workerCancelPending)
            PostToUi(() => { WorkerStatusText.Text = "Worker：取消确认超时"; AppendLog("Worker 取消命令 2 秒内未收到 cancelled"); });
    }

    private void PostToUi(Action action)
    {
        try
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException) { }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closingInProgress) return;

        var hasImport = _importOperation is { IsCompleted: false };
        var hasQuery = _queryOperation is { IsCompleted: false };
        var hasReplay = _replayOperation is { IsCompleted: false } || _tradeReplayOperation is { IsCompleted: false };
        var hasStrategyLoad = _strategyLoadOperation is { IsCompleted: false };
        var hasWorker = _worker is { HasExited: false };
        var hasTqProbe = _tqProbeOperation is { IsCompleted: false };
        var hasTqMarket = _tqHistoryOperation is { IsCompleted: false } || _tqLiveOperation is { IsCompleted: false };
        if (hasImport || hasQuery || hasReplay || hasStrategyLoad || hasWorker || hasTqProbe || hasTqMarket)
        {
            var choice = MessageBox.Show(
                "当前有导入、查询或 Worker 任务。选择“是”停止任务并退出；选择“否”取消退出。",
                "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                SetStatus("已取消退出");
                return;
            }
        }

        _closingInProgress = true;
        SetStatus("正在停止任务并退出");
        try
        {
            if (!await StopActiveOperationsAndWaitAsync("窗口关闭"))
            {
                MessageBox.Show("任务尚未确认终止，窗口保持打开。请稍后再次退出或查看日志。", "退出未完成", MessageBoxButton.OK, MessageBoxImage.Error);
                _closingInProgress = false;
                return;
            }
        }
        finally
        {
            var allStopped = (_importOperation is null || _importOperation.IsCompleted) &&
                             (_queryOperation is null || _queryOperation.IsCompleted) &&
                             (_replayOperation is null || _replayOperation.IsCompleted) &&
                             (_tradeReplayOperation is null || _tradeReplayOperation.IsCompleted) &&
                             (_strategyLoadOperation is null || _strategyLoadOperation.IsCompleted) &&
                             (_tqProbeOperation is null || _tqProbeOperation.IsCompleted) &&
                             (_tqHistoryOperation is null || _tqHistoryOperation.IsCompleted) &&
                             (_tqLiveOperation is null || _tqLiveOperation.IsCompleted) &&
                             (_worker is null || _worker.HasExited);
            if (_closingInProgress && allStopped)
            {
                _allowClose = true;
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(Close));
            }
        }
    }

    private async Task<bool> StopActiveOperationsAndWaitAsync(string reason)
    {
        _taskCancellation?.Cancel();
        _queryCancellation?.Cancel();
        _replayCancellation?.Cancel();
        _tradeReplayCancellation?.Cancel();
        _strategyLoadCancellation?.Cancel();
        _tqProbeCancellation?.Cancel();
        _tqHistoryCancellation?.Cancel();
        _tqLiveCancellation?.Cancel();

        var importStopped = await WaitForTaskTerminationAsync(_importOperation, "导入");
        var queryStopped = await WaitForTaskTerminationAsync(_queryOperation, "查询");
        var replayStopped = await WaitForTaskTerminationAsync(_replayOperation, "信号回放");
        var tradeReplayStopped = await WaitForTaskTerminationAsync(_tradeReplayOperation, "交易回放");
        var strategyLoadStopped = await WaitForTaskTerminationAsync(_strategyLoadOperation, "策略加载");
        var tqProbeStopped = await WaitForTaskTerminationAsync(_tqProbeOperation, "TqSdk 连接测试");
        var tqHistoryStopped = await WaitForTaskTerminationAsync(_tqHistoryOperation, "TqSdk 历史任务");
        var tqLiveStopped = await WaitForTaskTerminationAsync(_tqLiveOperation, "TqSdk 实时会话");
        await StopWorkerAsync(reason);
        var workerStopped = _worker is null || _worker.HasExited;
        return importStopped && queryStopped && replayStopped && tradeReplayStopped && strategyLoadStopped && tqProbeStopped && tqHistoryStopped && tqLiveStopped && workerStopped;
    }

    private async Task<bool> WaitForTaskTerminationAsync(Task? task, string taskName)
    {
        if (task is null || task.IsCompleted) return true;
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != task)
        {
            AppendLog($"{taskName} 在退出等待窗口内未终止");
            return false;
        }
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog($"{taskName} 退出时结束并记录异常：{ex.Message}"); }
        return true;
    }

    private async Task LoadLatestSnapshotAsync()
    {
        if (!await _contextGate.WaitAsync(0)) { SetStatus("已有研究上下文任务运行中"); return; }
        try { await LoadLatestSnapshotCoreAsync(); }
        finally { _contextGate.Release(); }
    }

    private async Task LoadLatestSnapshotCoreAsync()
    {
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        var queryCancellation = new CancellationTokenSource();
        _queryCancellation = queryCancellation;
        var latestOperation = Task.Run(() => _store.GetLatestSnapshot(), queryCancellation.Token);
        _queryOperation = latestOperation;
        try
        {
            var latest = await latestOperation;
            RefreshSnapshotList();
            if (latest is null) return;
            await LoadSnapshotAsync(latest.SnapshotId);
            UpdateCurrentDataSelection();
        }
        catch (Exception ex) { AppendLog("读取快照失败：" + ex.Message); }
        finally
        {
            if (ReferenceEquals(_queryOperation, latestOperation)) _queryOperation = null;
            if (ReferenceEquals(_queryCancellation, queryCancellation))
            {
                _queryCancellation.Dispose();
                _queryCancellation = null;
            }
        }
    }

    private async Task LoadSnapshotAsync(string snapshotId, bool clearSignalContext = true, bool setDefaultRange = true)
    {
        if (clearSignalContext) ClearSignalContext();
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        var queryCancellation = new CancellationTokenSource();
        _queryCancellation = queryCancellation;
        var queryToken = queryCancellation.Token;
        var queryOperation = Task.Run(() =>
        {
            queryToken.ThrowIfCancellationRequested();
            var bars = _store.QuerySnapshot(snapshotId, queryToken);
            queryToken.ThrowIfCancellationRequested();
            return (Bars: bars, Indicators: Indicators.Calculate(bars, 3), Quality: _store.ReadQualityReport(snapshotId));
        }, queryToken);
        _queryOperation = queryOperation;
        try
        {
            var data = await queryOperation;
            _currentSnapshotId = snapshotId;
            _currentSnapshotInfo = _store.GetSnapshots().FirstOrDefault(item => item.SnapshotId == snapshotId);
            SnapshotCombo.SelectedItem = _currentSnapshotInfo;
            UpdateCurrentDataSelection();
            _bars = data.Bars;
            _indicators = data.Indicators;
            if (_currentSnapshotInfo is null) DataStatusText.Text = $"{_bars.Count:N0} 根";
            if (setDefaultRange) SetDefaultReplayRange();
            _viewportStart = 0;
            _viewportCount = Math.Min(240, _bars.Count);
            if (_bars.Count > 0)
            {
                ChartTitleText.Text = $"{_bars[0].Symbol} · {_bars[0].Period}";
                ChartRangeText.Text = $"{_bars[0].Timestamp:yyyy-MM-dd HH:mm} ～ {_bars[^1].Timestamp:yyyy-MM-dd HH:mm} · {_bars.Count} 根";
            }
            ShowQualityReport(data.Quality, "已加载快照质量报告");
            DrawChart();
        }
        finally
        {
            if (ReferenceEquals(_queryOperation, queryOperation)) _queryOperation = null;
            if (ReferenceEquals(_queryCancellation, queryCancellation))
            {
                _queryCancellation.Dispose();
                _queryCancellation = null;
            }
        }
    }

    private void ShowQuality(ImportResult result)
    {
        ShowQualityReport(result.Report, result.Message);
    }

    private void ShowQualityReport(QualityReport report, string message)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(message);
        builder.AppendLine($"输入行：{report.InputRows}，去重：{report.DuplicateRows}，乱序：{report.OutOfOrderRows}，发布候选：{report.PublishedRows}");
        builder.AppendLine($"混合序列：{report.MixedSeriesRows}，冲突：{report.ConflictRows}，非法：{report.InvalidRows}，未完成：{report.IncompleteRows}");
        builder.AppendLine($"时段表：{report.SessionTableVersion}");
        builder.AppendLine($"覆盖：{report.FirstTimestamp:yyyy-MM-dd HH:mm:ss} ～ {report.LastTimestamp:yyyy-MM-dd HH:mm:ss}");
        if (report.SuspectedGaps.Count > 0) { builder.AppendLine("疑似缺口："); foreach (var gap in report.SuspectedGaps) builder.AppendLine("- " + gap); }
        foreach (var issue in report.Issues.Take(20)) builder.AppendLine($"[{issue.Code}] 行 {issue.Row}: {issue.Message}");
        QualityText.Text = builder.ToString();
    }

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        if (_bars.Count == 0 || ChartCanvas.ActualWidth <= 10 || ChartCanvas.ActualHeight <= 10) return;
        EnsureChartAverages();
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        var chartHeight = height * 0.80;
        var count = Math.Clamp(_viewportCount, 1, _bars.Count);
        _viewportStart = Math.Clamp(_viewportStart, 0, Math.Max(0, _bars.Count - count));
        var visibleEnd = Math.Min(_bars.Count, _viewportStart + count);
        var visible = _bars.Skip(_viewportStart).Take(visibleEnd - _viewportStart).ToArray();
        var min = (double)visible.Min(x => x.Low);
        var max = (double)visible.Max(x => x.High);
        foreach (var value in _fastAverage.Skip(_viewportStart).Take(count).Concat(_slowAverage.Skip(_viewportStart).Take(count)))
        {
            if (value is not { } price) continue;
            min = Math.Min(min, (double)price); max = Math.Max(max, (double)price);
        }
        var padding = Math.Max(1, (max - min) * 0.06);
        min -= padding; max += padding;
        var range = Math.Max(max - min, 1);
        var volumeMax = Math.Max((double)visible.Max(x => x.Volume), 1);
        var stride = Math.Max(1, (int)Math.Ceiling(visible.Length / Math.Max(1, width)));
        var candleWidth = Math.Max(1, Math.Min(18, width / visible.Length * stride * 0.68));
        _chartMin = min;
        _chartMax = max;
        _chartHeight = chartHeight;
        _chartWidth = width;
        double X(double index) => (index + 0.5) / visible.Length * width;
        double Y(decimal price) => chartHeight - 10 - ((double)price - min) / range * (chartHeight - 20);
        for (var grid = 0; grid <= 4; grid++)
        {
            var y = 10 + grid * (chartHeight - 20) / 4;
            ChartCanvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = Brushes.Gainsboro, StrokeDashArray = [2, 4], IsHitTestVisible = false });
            AddPriceAxisLabel((decimal)(max - grid * range / 4), y);
            if (grid == 4) continue;
            var index = grid * (visible.Length - 1) / 3;
            var date = new TextBlock { Text = visible[index].Timestamp.ToString("MM-dd HH:mm"), FontSize = 10, Foreground = Brushes.SlateGray, IsHitTestVisible = false };
            Canvas.SetLeft(date, Math.Clamp(X(index) - 30, 0, Math.Max(0, width - 80))); Canvas.SetTop(date, height - 15); ChartCanvas.Children.Add(date);
        }
        for (var localIndex = 0; localIndex < visible.Length; localIndex += stride)
        {
            var bar = visible[localIndex];
            var last = Math.Min(visible.Length - 1, localIndex + stride - 1);
            var close = visible[last].Close;
            var high = bar.High; var low = bar.Low; var volumeValue = bar.Volume;
            for (var i = localIndex + 1; i <= last; i++) { high = Math.Max(high, visible[i].High); low = Math.Min(low, visible[i].Low); volumeValue = Math.Max(volumeValue, visible[i].Volume); }
            var rising = close >= bar.Open;
            var brush = rising ? Brushes.Firebrick : Brushes.SeaGreen;
            var x = X((localIndex + last) / 2.0);
            var wick = new Line { X1 = x, X2 = x, Y1 = Y(high), Y2 = Y(low), Stroke = brush, StrokeThickness = 1 };
            ChartCanvas.Children.Add(wick);
            var bodyTop = Y(Math.Max(bar.Open, close));
            var bodyBottom = Y(Math.Min(bar.Open, close));
            var body = new Rectangle { Width = candleWidth, Height = Math.Max(1, bodyBottom - bodyTop), Fill = rising ? Brushes.Transparent : brush, Stroke = brush, StrokeThickness = 1 };
            Canvas.SetLeft(body, x - candleWidth / 2); Canvas.SetTop(body, bodyTop); ChartCanvas.Children.Add(body);
            var volume = new Rectangle { Width = candleWidth, Height = (double)volumeValue / volumeMax * Math.Max(1, height - chartHeight - 18), Fill = brush, Opacity = 0.35 };
            Canvas.SetLeft(volume, x - candleWidth / 2); Canvas.SetTop(volume, height - volume.Height - 17); ChartCanvas.Children.Add(volume);
        }
        var fastLine = new Polyline { Stroke = Brushes.DarkOrange, StrokeThickness = 1.5, IsHitTestVisible = false };
        var slowLine = new Polyline { Stroke = Brushes.RoyalBlue, StrokeThickness = 1.5, IsHitTestVisible = false };
        for (var localIndex = 0; localIndex < visible.Length; localIndex++)
        {
            var absoluteIndex = _viewportStart + localIndex;
            if (localIndex % stride != 0 && localIndex != visible.Length - 1) continue;
            if (_fastAverage[absoluteIndex] is { } fast) fastLine.Points.Add(new Point(X(localIndex), Y(fast)));
            if (_slowAverage[absoluteIndex] is { } slow) slowLine.Points.Add(new Point(X(localIndex), Y(slow)));
        }
        ChartCanvas.Children.Add(fastLine); ChartCanvas.Children.Add(slowLine);
        var signalLookup = _displayedSignals.Concat(_tradeSignals).Where(s => s.SnapshotId == _currentSnapshotId).DistinctBy(s => (s.RunId, s.Sequence)).ToLookup(s => s.BarStart);
        var fillLookup = _tradeFills.ToLookup(f => f.BarEnd);
        for (var localIndex = 0; localIndex < visible.Length; localIndex++)
        {
            var bar = visible[localIndex]; var x = X(localIndex);
            foreach (var signal in signalLookup[bar.Timestamp].Where(s => s.Symbol == bar.Symbol))
            {
                var marker = new Ellipse { Width = 10, Height = 10, Fill = signal.Direction.Equals("long", StringComparison.OrdinalIgnoreCase) ? Brushes.IndianRed : Brushes.SteelBlue, Stroke = Brushes.White, StrokeThickness = 1, ToolTip = $"{signal.BarEnd:O} {signal.Category} {signal.Description}", IsHitTestVisible = false };
                Canvas.SetLeft(marker, x - 5); Canvas.SetTop(marker, signal.Direction.Equals("long", StringComparison.OrdinalIgnoreCase) ? Y(bar.Low) + 8 : Y(bar.High) - 18);
                ChartCanvas.Children.Add(marker);
            }
            var markerIndex = 0;
            foreach (var fill in fillLookup[GetBarEnd(bar)])
            {
                var buy = (fill.Action == "open") == (fill.Direction == "long");
                var brush = buy ? Brushes.Firebrick : Brushes.SeaGreen;
                var label = (buy ? "买" : "卖") + (fill.Action == "open" ? "开" : "平");
                var marker = new TextBlock { Text = label, FontSize = 10, Background = brush, Foreground = Brushes.White, Padding = new Thickness(2, 0, 2, 0), ToolTip = $"#{fill.FillSequence} {fill.BarEnd:O}\n{label} {fill.Quantity}手 @ {fill.Price}\n费用 {fill.Fee}" };
                Canvas.SetLeft(marker, Math.Clamp(x - 12, 0, Math.Max(0, width - 28))); Canvas.SetTop(marker, Math.Clamp(Y(fill.Price) + (markerIndex++ % 2 == 0 ? 6 : -20), 16, Math.Max(16, chartHeight - 20)));
                ChartCanvas.Children.Add(marker);
                ChartCanvas.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = brush, Margin = new Thickness(Math.Max(0, x - 2), Math.Max(0, Y(fill.Price) - 2), 0, 0), IsHitTestVisible = false });
            }
        }
        ChartRangeText.Text = $"{visible[0].Timestamp:yyyy-MM-dd HH:mm} ～ {visible[^1].Timestamp:yyyy-MM-dd HH:mm} · 可见 {visible.Length:N0} / {_bars.Count:N0} 根" + (stride > 1 ? $" · 每{stride}根合并显示（悬停为原始K线，量柱为组内最大量）" : "");
        _crosshairVertical = new Line { Stroke = Brushes.SlateGray, StrokeThickness = 1, StrokeDashArray = [3, 3], IsHitTestVisible = false, Visibility = Visibility.Hidden };
        _crosshairHorizontal = new Line { Stroke = Brushes.SlateGray, StrokeThickness = 1, StrokeDashArray = [3, 3], IsHitTestVisible = false, Visibility = Visibility.Hidden };
        _crosshairLabel = new TextBlock { Background = Brushes.White, Foreground = Brushes.Black, Padding = new Thickness(4), IsHitTestVisible = false, Visibility = Visibility.Hidden };
        ChartCanvas.Children.Add(_crosshairVertical);
        ChartCanvas.Children.Add(_crosshairHorizontal);
        ChartCanvas.Children.Add(_crosshairLabel);
        DrawEquityCurve();
    }

    private void AddPriceAxisLabel(decimal price, double top)
    {
        var label = new TextBlock { Text = price.ToString("0.####", CultureInfo.InvariantCulture), Foreground = Brushes.DimGray, FontSize = 11, IsHitTestVisible = false };
        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, top);
        ChartCanvas.Children.Add(label);
    }

    private void DrawEquityCurve()
    {
        EquityCanvas.Children.Clear();
        if (_tradeEquity.Count < 2 || EquityCanvas.ActualWidth <= 10 || EquityCanvas.ActualHeight <= 10) return;
        var width = EquityCanvas.ActualWidth; var height = EquityCanvas.ActualHeight; var min = _tradeEquity.Min(point => point.Equity); var max = _tradeEquity.Max(point => point.Equity); var range = Math.Max(max - min, 1m);
        var line = new Polyline { Stroke = Brushes.DarkCyan, StrokeThickness = 1.5, Points = new PointCollection() };
        for (var index = 0; index < _tradeEquity.Count; index++)
        {
            var x = index * width / Math.Max(1, _tradeEquity.Count - 1); var y = height - ((double)(_tradeEquity[index].Equity - min) / (double)range) * Math.Max(1, height - 12) - 6;
            line.Points.Add(new Point(x, y));
        }
        EquityCanvas.Children.Add(line);
        var label = new TextBlock { Text = $"权益 {min:0.##}～{max:0.##}", Foreground = Brushes.DimGray, FontSize = 10, IsHitTestVisible = false };
        Canvas.SetLeft(label, 4); Canvas.SetTop(label, 2); EquityCanvas.Children.Add(label);
    }

    private void EquityCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawEquityCurve();

    private void TradeFillGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TradeFillGrid.SelectedItem is FillRecord fill) LocateFill(fill);
    }

    private bool LocateSignal(SignalEvent signal)
    {
        if (_currentSnapshotInfo is null || !string.Equals(signal.SnapshotId, _currentSnapshotInfo.SnapshotId, StringComparison.Ordinal) || !string.Equals(signal.Symbol, _currentSnapshotInfo.Symbol, StringComparison.Ordinal))
        {
            SetStatus("信号不属于当前图表快照，未执行定位");
            return false;
        }
        var match = _bars.Select((bar, index) => (bar, index)).FirstOrDefault(item => item.bar.Timestamp == signal.BarStart && item.bar.Symbol == signal.Symbol);
        if (match.bar is null)
        {
            SetStatus("当前快照没有匹配的信号 K 线，未执行定位");
            return false;
        }
        _viewportStart = Math.Clamp(match.index - Math.Max(1, _viewportCount / 3), 0, Math.Max(0, _bars.Count - _viewportCount));
        DrawChart();
        SetStatus($"已定位信号：{signal.BarEnd:O} {signal.Category} {signal.Direction}");
        return true;
    }

    private bool LocateFill(FillRecord fill)
    {
        var match = _bars.Select((bar, index) => (bar, index)).FirstOrDefault(item => GetBarEnd(item.bar) == fill.BarEnd);
        if (match.bar is null) { SetStatus("当前快照没有匹配的成交 K 线"); return false; }
        _viewportStart = Math.Clamp(match.index - Math.Max(1, _viewportCount / 3), 0, Math.Max(0, _bars.Count - _viewportCount));
        DrawChart(); SetStatus($"已定位成交：{fill.BarEnd:O} {fill.Action} {fill.Price}");
        return true;
    }

    private DateTimeOffset GetBarEnd(Bar bar) => bar.Timestamp + PeriodDuration(bar.Period);

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();
    private void ChartCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_bars.Count == 0 || ChartCanvas.ActualWidth <= 0) return;
        var point = e.GetPosition(ChartCanvas);
        if (_isPanning)
        {
            var delta = (int)Math.Round((_panOrigin.X - point.X) / ChartCanvas.ActualWidth * _viewportCount);
            _viewportStart = Math.Clamp(_panStart + delta, 0, Math.Max(0, _bars.Count - _viewportCount));
            DrawChart();
        }
        UpdateCrosshair(point);
    }

    private void ChartCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_bars.Count == 0) return;
        ZoomChart(e.Delta, e.GetPosition(ChartCanvas));
        e.Handled = true;
    }

    private void ZoomChart(int delta, Point point)
    {
        if (_bars.Count == 0) return;
        var oldCount = Math.Clamp(_viewportCount, 1, _bars.Count);
        var focus = _viewportStart + Math.Clamp((int)(point.X / Math.Max(1, ChartCanvas.ActualWidth) * oldCount), 0, oldCount - 1);
        var newCount = Math.Clamp((int)Math.Round(oldCount * (delta > 0 ? 0.8 : 1.25)), Math.Min(20, _bars.Count), _bars.Count);
        var ratio = point.X / Math.Max(1, ChartCanvas.ActualWidth);
        _viewportCount = newCount;
        _viewportStart = Math.Clamp(focus - (int)Math.Round(newCount * ratio), 0, Math.Max(0, _bars.Count - newCount));
        DrawChart();
        UpdateCrosshair(point);
    }

    private void ChartCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_bars.Count == 0) return;
        _isPanning = true;
        _panOrigin = e.GetPosition(ChartCanvas);
        _panStart = _viewportStart;
        ChartCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void ChartCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        ChartCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateCrosshair(Point point)
    {
        if (_crosshairVertical is null || _crosshairHorizontal is null || _crosshairLabel is null || _bars.Count == 0) return;
        var width = Math.Max(1, _chartWidth);
        var count = Math.Clamp(_viewportCount, 1, _bars.Count);
        var localIndex = Math.Clamp((int)(point.X / width * count), 0, count - 1);
        var absoluteIndex = Math.Clamp(_viewportStart + localIndex, 0, _bars.Count - 1);
        var bar = _bars[absoluteIndex];
        var range = Math.Max(_chartMax - _chartMin, 1);
        var price = _chartMax - Math.Clamp((point.Y - 10) / Math.Max(1, _chartHeight - 20), 0, 1) * range;
        var x = (localIndex + 0.5) / count * width;
        _crosshairVertical.X1 = _crosshairVertical.X2 = x;
        _crosshairVertical.Y1 = 0; _crosshairVertical.Y2 = ChartCanvas.ActualHeight;
        _crosshairHorizontal.X1 = 0; _crosshairHorizontal.X2 = width;
        _crosshairHorizontal.Y1 = _crosshairHorizontal.Y2 = Math.Clamp(point.Y, 0, ChartCanvas.ActualHeight);
        _crosshairLabel.Text = $"{bar.Timestamp:MM-dd HH:mm:ss}  O{bar.Open} H{bar.High} L{bar.Low} C{bar.Close}  价轴≈{price:0.####}";
        Canvas.SetLeft(_crosshairLabel, Math.Clamp(x + 6, 0, Math.Max(0, width - 260)));
        Canvas.SetTop(_crosshairLabel, 4);
        _crosshairVertical.Visibility = Visibility.Visible;
        _crosshairHorizontal.Visibility = Visibility.Visible;
        _crosshairLabel.Visibility = Visibility.Visible;
        StatusText.Text = $"{bar.Timestamp:yyyy-MM-dd HH:mm:ss} · O {bar.Open} H {bar.High} L {bar.Low} C {bar.Close} · 量 {bar.Volume} · 可见 {count}/{_bars.Count}";
    }

    private void SetTaskState(bool running)
    {
        SetReplayControlsLocked(running);
        ImportButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
    }

    private void UpdateWorkflowHint()
    {
        var hasData = _currentSnapshotId is not null && _bars.Count > 0;
        var hasStrategy = _strategyDescriptor is not null;
        var hasResult = _activeTradeRecord is not null || string.Equals(_lastSignalResult?.Status, "completed", StringComparison.OrdinalIgnoreCase);
        var hasSymbol = !string.IsNullOrWhiteSpace(GetTqHistorySymbol());
        var step = hasResult
            ? "步骤 8/8：查看结果　→　下一步：查看摘要、权益曲线或导出"
            : hasStrategy && hasData
                ? "步骤 7/8：开始回测　→　下一步：查看结果"
                : hasStrategy
                    ? "步骤 6/8：设置参数　→　下一步：检查回放配置"
                    : hasData
                        ? "步骤 5/8：选择策略　→　下一步：加载并校验策略"
                        : hasSymbol
                            ? "步骤 3/8：选择行情来源和日期区间　→　下一步：检查覆盖或下载"
                            : "步骤 1/8：选择合约和周期　→　下一步：选择行情来源与日期区间";
        WorkflowStepText.Text = step;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        UpdateWorkflowHint();
    }
    private void AppendLog(string message) => LogText.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");

    public async Task<bool> RunInternalSelfCheckAsync(string outputPath)
    {
        var started = Stopwatch.StartNew();
        var checks = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["note"] = "离屏/内部自动检查，不替代人工点击或全新系统验收"
        };
        var imagePath = Path.ChangeExtension(outputPath, ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        try
        {
            UpdateLayout();
            checks["first_layout_ms"] = started.Elapsed.TotalMilliseconds;
            checks["startup_markers"] = App.StartupMarkers();
            checks["startup_ready_file"] = App.StartupReadyPath is not null && File.Exists(App.StartupReadyPath);
            var requiredControls = new object?[]
            {
                ImportButton, SnapshotCombo, LoadSnapshotButton, StrategyPathText, ReplayButton,
                TradeReplayButton, TradeFillGrid, EquityCanvas, SignalGrid, OpenRunButton,
                CopyTradeConfigButton, ExportSignalsButton, TradeDetailCombo, TqAccountText, TqPasswordBox, TqTestButton,
                TqHistorySymbolCombo, TqHistorySymbolText, TqHistoryDurationCombo, TqHistoryModeCombo, TqHistoryStartDatePicker, TqHistoryEndDatePicker, TqHistoryStartText, TqHistoryEndText, TqHistoryCalendarStatusText, GenerateLocalCalendarButton, TqHistoryDownloadButton, StrategyPeriodCombo,
                TqLiveSymbolCombo, TqLiveSymbolHintText, TqLiveSymbolText, TqLiveDurationCombo, LiveStrategySelectionText, GoToStrategyButton, TqLiveStartButton, TqLiveStopButton, LiveStrategyRuntimeText
            };
            var gateHeld = _contextGate.Wait(0);
            var gateBlocksSecond = gateHeld && !_contextGate.Wait(0);
            if (gateHeld) _contextGate.Release();
            var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "qh_signal_worker.py");
            var pythonPath = PythonRuntime.ResolveExecutable();
            checks["required_controls"] = requiredControls.All(control => control is not null);
            checks["context_gate_non_reentrant"] = gateBlocksSecond;
            checks["worker_exists"] = File.Exists(workerPath);
            checks["python_path"] = pythonPath;
            checks["python_exists"] = pythonPath == "python" || File.Exists(pythonPath);

            var tenThousandCsv = Path.Combine(_store.RootDirectory, "self-check-10000.csv");
            CreateSelfCheckBars(tenThousandCsv, 10_000);
            var importWatch = Stopwatch.StartNew();
            var largeImport = await Task.Run(() => _importer.ImportAsync(tenThousandCsv, _store, cancellationToken: CancellationToken.None));
            importWatch.Stop();
            var p02Loads = new List<double>();
            for (var loadIndex = 0; loadIndex < 5; loadIndex++)
            {
                var p02Watch = Stopwatch.StartNew();
                await LoadSnapshotAsync(largeImport.SnapshotId!, clearSignalContext: true, setDefaultRange: false);
                UpdateLayout();
                DrawChart();
                p02Watch.Stop();
                p02Loads.Add(p02Watch.Elapsed.TotalMilliseconds);
            }
            var largeViewportBefore = _viewportCount;
            ZoomChart(120, new Point(Math.Max(1, ChartCanvas.ActualWidth / 2), Math.Max(1, ChartCanvas.ActualHeight / 2)));
            var largeViewportAfter = _viewportCount;
            checks["p02"] = new
            {
                published = largeImport.Published,
                rows = largeImport.Report.PublishedRows,
                import_ms = importWatch.Elapsed.TotalMilliseconds,
                load_and_draw_ms = p02Loads,
                first_load_and_draw_ms = p02Loads[0],
                five_runs = p02Loads.Count == 5,
                cache_note = "same isolated data root; OS file cache is not cleared, so runs 2-5 may be warm",
                ten_thousand_zoom = largeImport.Published && _bars.Count == 10_000 && largeViewportAfter != largeViewportBefore && ChartCanvas.Children.Count > 10
            };

            var largeSignalPath = Path.Combine(AppContext.BaseDirectory, "strategies", "period_signal.py");
            _restoringRunView = true;
            StrategyPathText.Text = largeSignalPath;
            StrategyParametersText.Text = "{\"period\":1}";
            StartInclusiveText.Text = _bars[1].Timestamp.ToString("O", CultureInfo.InvariantCulture);
            EndExclusiveText.Text = (_bars[^1].Timestamp + PeriodDuration(_bars[^1].Period)).ToString("O", CultureInfo.InvariantCulture);
            ExploreModeCheck.IsChecked = false;
            _restoringRunView = false;
            var dispatcherProbe = await RunWithDispatcherProbeAsync(ReplayButtonCoreAsync, tick =>
            {
                if (tick == 1 && _bars.Count == 10_000)
                    ZoomChart(120, new Point(Math.Max(1, ChartCanvas.ActualWidth / 2), Math.Max(1, ChartCanvas.ActualHeight / 2)));
            });
            checks["p03_dispatcher_probe"] = new { ticks = dispatcherProbe.Ticks, max_interval_ms = dispatcherProbe.MaxIntervalMs, elapsed_ms = dispatcherProbe.ElapsedMs, replay_during_probe = dispatcherProbe.Ticks > 0, zoom_during_probe = dispatcherProbe.ActionObserved, max_interval_under_1s = dispatcherProbe.MaxIntervalMs < 1000, ten_thousand_bars = _bars.Count == 10_000 };

            var f01Path = Path.Combine(AppContext.BaseDirectory, "fixtures", "g3-bars-f01.csv");
            var f01Import = await Task.Run(() => _importer.ImportAsync(f01Path, _store, cancellationToken: CancellationToken.None));
            if (!f01Import.Published) throw new InvalidDataException("F01 自检夹具未发布");
            await LoadSnapshotAsync(f01Import.SnapshotId!, clearSignalContext: true, setDefaultRange: false);
            var start = DateTimeOffset.Parse("2026-10-01T09:00:00+08:00", CultureInfo.InvariantCulture);
            var end = DateTimeOffset.Parse("2026-10-01T09:03:00+08:00", CultureInfo.InvariantCulture);
            var signalPath = Path.Combine(AppContext.BaseDirectory, "strategies", "period_signal.py");
            _restoringRunView = true;
            StrategyPathText.Text = signalPath;
            StrategyParametersText.Text = "{\"period\":1}";
            StartInclusiveText.Text = start.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture);
            EndExclusiveText.Text = end.ToString("O", CultureInfo.InvariantCulture);
            ExploreModeCheck.IsChecked = false;
            _restoringRunView = false;
            await ReplayButtonCoreAsync();
            var signalResult = _lastSignalResult ?? throw new InvalidOperationException("信号 UI 回放没有绑定结果");
            checks["signal_replay"] = new { signalResult.Status, signals = signalResult.Signals.Count, signalResult.RunId, ui_request_path = true };

            var tradePath = Path.Combine(AppContext.BaseDirectory, "strategies", "trade_sequence.py");
            _restoringRunView = true;
            StrategyPathText.Text = tradePath;
            StrategyParametersText.Text = "{\"scenario\":\"f01\"}";
            StartInclusiveText.Text = start.ToString("O", CultureInfo.InvariantCulture);
            EndExclusiveText.Text = end.ToString("O", CultureInfo.InvariantCulture);
            TradeConfigText.Text = JsonSerializer.Serialize(new TradingConfig(SlippageTicks: 1), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            ExploreModeCheck.IsChecked = false;
            _restoringRunView = false;
            await TradeReplayButtonCoreAsync();
            var tradeRunId = _activeTradeRunId ?? throw new InvalidOperationException("交易 UI 回放没有绑定 run");
            var tradeStore = new TradeRunStore(_store.RootDirectory);
            var tradeRecord = tradeStore.ReadRecord(tradeRunId);
            checks["trade_replay"] = new { tradeRecord.Status, intents = tradeRecord.FormalIntentCount, fills = tradeRecord.FillCount, closed = tradeRecord.ClosedTradeCount, gross = tradeRecord.TotalGrossPnl, fees = tradeRecord.TotalFees, net = tradeRecord.NetPnl, RunId = tradeRunId, equity = tradeRecord.FinalEquity, ui_request_path = true };
            var f01Business = tradeRecord.Status == "completed" && tradeRecord.FormalIntentCount == 2 && tradeRecord.FillCount == 2 && tradeRecord.ClosedTradeCount == 1 && tradeRecord.TotalGrossPnl == 20m && tradeRecord.TotalFees == 5m && tradeRecord.NetPnl == 15m && tradeRecord.FinalEquity == 10015m;
            checks["f01_business"] = new { f01Business, gross = tradeRecord.TotalGrossPnl, fees = tradeRecord.TotalFees, net = tradeRecord.NetPnl, equity = tradeRecord.FinalEquity };
            var tradeCurvePassed = _tradeEquity.Count >= 2 && EquityCanvas.Children.OfType<Polyline>().Any();
            checks["equity_curve_non_empty"] = tradeCurvePassed;
            var signalLocated = _tradeSignals.Count > 0 && LocateSignal(_tradeSignals[0]);
            var fillLocated = _tradeFills.Count > 0 && LocateFill(_tradeFills[0]);
            checks["signal_location"] = signalLocated;
            checks["fill_location"] = fillLocated;
            checks["tables_rendered"] = new { signals = SignalGrid.Items.Count, fills = TradeFillGrid.Items.Count, chart_children = ChartCanvas.Children.Count, equity_children = EquityCanvas.Children.Count, summary = TradeResultText.Text.Contains("净利 15") };
            var oldViewport = _viewportCount;
            ZoomChart(120, new Point(Math.Max(1, ChartCanvas.ActualWidth / 2), Math.Max(1, ChartCanvas.ActualHeight / 2)));
            var zoomPath = _viewportCount != oldViewport && _bars.Count == 3;
            checks["zoom_path"] = ((dynamic)checks["p02"]!).ten_thousand_zoom;

            var exportRoot = Path.Combine(_store.RootDirectory, "self-check-exports");
            Directory.CreateDirectory(exportRoot);
            var fillsExport = Path.Combine(exportRoot, "fills.csv");
            var equityExport = Path.Combine(exportRoot, "equity.csv");
            var summaryExport = Path.Combine(exportRoot, "run.json");
            var signalExport = Path.Combine(exportRoot, "signals.csv");
            tradeStore.ExportFillsCsv(tradeRunId, fillsExport);
            tradeStore.ExportEquityCsv(tradeRunId, equityExport);
            tradeStore.ExportSummaryJson(tradeRunId, summaryExport);
            new SignalRunStore(_store.RootDirectory).ExportCsv(_tradeSignals, signalExport);
            var exportedFills = File.Exists(fillsExport) && File.ReadLines(fillsExport).Count() == tradeRecord.FillCount + 1 && File.ReadAllText(fillsExport).Contains(",103,", StringComparison.Ordinal) && File.ReadAllText(fillsExport).Contains(",105,", StringComparison.Ordinal);
            var exportedEquity = File.Exists(equityExport) && File.ReadLines(equityExport).Count() >= 2 && File.ReadAllText(equityExport).Contains(",10015,", StringComparison.Ordinal);
            var exportedSummary = File.Exists(summaryExport) && JsonDocument.Parse(File.ReadAllText(summaryExport)).RootElement.GetProperty("NetPnl").GetDecimal() == 15m;
            var exportedSignals = File.Exists(signalExport) && File.ReadLines(signalExport).Count() == _tradeSignals.Count + 1;
            checks["exports"] = new { fills = exportedFills, equity = exportedEquity, summary = exportedSummary, signals = exportedSignals, fills_rows = File.Exists(fillsExport) ? File.ReadLines(fillsExport).Count() - 1 : -1, signal_rows = File.Exists(signalExport) ? File.ReadLines(signalExport).Count() - 1 : -1 };

            var collisionGateHeld = await _contextGate.WaitAsync(0);
            if (!collisionGateHeld) throw new InvalidOperationException("入口互斥自检无法取得 context gate");
            try
            {
                _restoringRunView = true;
                StrategyPathText.Text = Path.Combine(AppContext.BaseDirectory, "strategies", "trade_timeout.py");
                StrategyParametersText.Text = "{}";
                StartInclusiveText.Text = start.ToString("O", CultureInfo.InvariantCulture);
                EndExclusiveText.Text = end.ToString("O", CultureInfo.InvariantCulture);
                _restoringRunView = false;
                var collisionSnapshotBefore = _currentSnapshotId;
                var collisionStartBefore = StartInclusiveText.Text;
                var collisionEndBefore = EndExclusiveText.Text;
                var collisionStrategyBefore = StrategyPathText.Text;
                var collisionOperation = TradeReplayButtonCoreAsync();
                await Task.Delay(250);
                ImportButton_Click(this, new RoutedEventArgs());
                LoadSnapshotButton_Click(this, new RoutedEventArgs());
                OpenRunButton_Click(this, new RoutedEventArgs());
                CopyTradeConfigButton_Click(this, new RoutedEventArgs());
                var entryBlocked = StatusText.Text.Contains("已有研究上下文任务运行中", StringComparison.Ordinal);
                var cancelAvailable = CancelTradeReplayButton.IsEnabled;
                CancelTradeReplayButton_Click(this, new RoutedEventArgs());
                await collisionOperation;
                checks["entry_collision"] = new { gate_held = true, entry_blocked = entryBlocked, cancel_available = cancelAvailable, snapshot_unchanged = _currentSnapshotId == collisionSnapshotBefore, start_unchanged = StartInclusiveText.Text == collisionStartBefore, end_unchanged = EndExclusiveText.Text == collisionEndBefore, strategy_unchanged = StrategyPathText.Text == collisionStrategyBefore, trade_cleanup = _tradeReplayOperation is null && _tradeReplayCancellation is null };
            }
            finally { _contextGate.Release(); }

            var originalRunJsonHash = GetFileHash(Path.Combine(tradeStore.RunsDirectory, tradeRunId, "run.json"));
            await LoadSnapshotAsync(largeImport.SnapshotId!, clearSignalContext: true, setDefaultRange: false);
            RunIdText.Text = tradeRunId;
            await OpenRunButtonCoreAsync();
            var reopened = _activeTradeRecord;
            var reopenedFields = new { strategy = StrategyPathText.Text, parameters = StrategyParametersText.Text, start = StartInclusiveText.Text, end = EndExclusiveText.Text, explore = ExploreModeCheck.IsChecked == true, config = TradeConfigText.Text };
            checks["readonly_reopen"] = new { bound_snapshot = reopened?.SnapshotId == f01Import.SnapshotId && _currentSnapshotId == f01Import.SnapshotId, chart_bound = _bars.Count == 3 && ChartCanvas.Children.Count > 10, start = reopened?.StartInclusive == start && reopenedFields.start == start.ToString("O", CultureInfo.InvariantCulture), end = reopened?.EndExclusive == end && reopenedFields.end == end.ToString("O", CultureInfo.InvariantCulture), explore = reopened?.Explore == false && !reopenedFields.explore, strategy = reopened is not null && Path.GetFileName(reopened.StrategySnapshotPath) == Path.GetFileName(reopenedFields.strategy), parameters = reopened is not null && JsonSerializer.Serialize(reopened.Parameters) == reopenedFields.parameters, config = reopened is not null && JsonSerializer.Deserialize<TradingConfig>(reopenedFields.config, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } }) is not null, read_only = _viewingRunId == tradeRunId };
            CopyTradeConfigButton_Click(this, new RoutedEventArgs());
            var copiedContext = _viewingRunId is null && _currentSnapshotId == f01Import.SnapshotId;
            var copiedFields = new { strategy = StrategyPathText.Text, parameters = StrategyParametersText.Text, start = StartInclusiveText.Text, end = EndExclusiveText.Text, explore = ExploreModeCheck.IsChecked == true, config = TradeConfigText.Text };
            await TradeReplayButtonCoreAsync();
            var copiedRunId = _activeTradeRunId;
            var copiedRecord = copiedRunId is null ? null : tradeStore.ReadRecord(copiedRunId);
            var copiedConfig = copiedRecord is null ? null : JsonSerializer.Serialize(copiedRecord.Config, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            checks["copy_config"] = new { new_context = copiedContext, new_run = copiedRunId is not null && copiedRunId != tradeRunId, snapshot = copiedRecord?.SnapshotId == reopened?.SnapshotId, strategy = copiedRecord is not null && Path.GetFileName(copiedRecord.StrategySnapshotPath) == Path.GetFileName(copiedFields.strategy), parameters = copiedRecord is not null && JsonSerializer.Serialize(copiedRecord.Parameters) == copiedFields.parameters, start = copiedRecord?.StartInclusive == reopened?.StartInclusive && copiedFields.start == reopened?.StartInclusive.ToString("O", CultureInfo.InvariantCulture), end = copiedRecord?.EndExclusive == reopened?.EndExclusive && copiedFields.end == reopened?.EndExclusive.ToString("O", CultureInfo.InvariantCulture), explore = copiedRecord?.Explore == copiedFields.explore, config = copiedRecord is not null && copiedConfig == copiedFields.config, original_unchanged = GetFileHash(Path.Combine(tradeStore.RunsDirectory, tradeRunId, "run.json")) == originalRunJsonHash };

            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            var renderedImage = SaveSelfCheckImage(imagePath);
            checks["rendered_png"] = new { path = imagePath, width = renderedImage.Width, height = renderedImage.Height, non_transparent_pixels = renderedImage.NonTransparentPixels, non_zero_bytes = renderedImage.NonZeroBytes, non_white_pixels = renderedImage.NonWhitePixels, has_visible_content = renderedImage.HasVisibleContent, chart_visible = renderedImage.ChartVisible, equity_visible = renderedImage.EquityVisible, result_visible = renderedImage.ResultVisible };
            checks["rendered_result"] = new { chart_children = ChartCanvas.Children.Count, equity_children = EquityCanvas.Children.Count, signal_rows = SignalGrid.Items.Count, fill_rows = TradeFillGrid.Items.Count, result_text = TradeResultText.Text.Contains("净利") };

            _restoringRunView = true;
            StrategyPathText.Text = tradePath;
            StrategyParametersText.Text = "{\"scenario\":\"end\"}";
            StartInclusiveText.Text = start.ToString("O", CultureInfo.InvariantCulture);
            EndExclusiveText.Text = end.ToString("O", CultureInfo.InvariantCulture);
            _restoringRunView = false;
            await TradeReplayButtonCoreAsync();
            var endRun = _activeTradeRunId is null ? null : tradeStore.ReadRecord(_activeTradeRunId);
            var unfilledPath = _activeTradeRunId is null ? "" : Path.Combine(tradeStore.RunsDirectory, _activeTradeRunId, "unfilled.jsonl");
            checks["last_bar_unfilled"] = new { status = endRun?.Status, reason = File.Exists(unfilledPath) && File.ReadAllText(unfilledPath).Contains("no_next_valid_bar", StringComparison.Ordinal), run = _activeTradeRunId };

            _restoringRunView = true;
            StrategyPathText.Text = tradePath;
            StrategyParametersText.Text = "{\"scenario\":\"zero\"}";
            _restoringRunView = false;
            await TradeReplayButtonCoreAsync();
            var zeroRun = _activeTradeRunId is null ? null : tradeStore.ReadRecord(_activeTradeRunId);
            checks["zero_trade"] = new { status = zeroRun?.Status, fills = zeroRun?.FillCount ?? -1, win_rate_not_applicable = zeroRun?.WinRate is null };

            _restoringRunView = true;
            StrategyPathText.Text = Path.Combine(AppContext.BaseDirectory, "strategies", "trade_timeout.py");
            StrategyParametersText.Text = "{}";
            _restoringRunView = false;
            var cancelTask = TradeReplayButtonCoreAsync();
            await Task.Delay(750);
            var cancelWatch = Stopwatch.StartNew();
            CancelTradeReplayButton_Click(this, new RoutedEventArgs());
            await cancelTask;
            cancelWatch.Stop();
            var cancelledRun = _activeTradeRunId is null ? null : tradeStore.ReadRecord(_activeTradeRunId);
            checks["cancel"] = new { status = cancelledRun?.Status ?? "unknown", latency_ms = cancelWatch.Elapsed.TotalMilliseconds, run = _activeTradeRunId, ui_cancel = cancelledRun?.Status == "cancelled", preflight = false };

            var runningCloseStrategyPath = Path.Combine(_store.RootDirectory, "self-check-open-then-hang.py");
            File.WriteAllText(runningCloseStrategyPath, string.Join(Environment.NewLine, new[]
            {
                "import json",
                "import os",
                "import sys",
                "import time",
                "STRATEGY = {'name': 'self-check open then hang', 'version': '1.0.0', 'kind': 'trade', 'warmup_bars': 0, 'parameters': {}}",
                "def create_strategy(parameters): return Strategy()",
                "class Strategy:",
                "    def on_bar(self, context):",
                "        if context.bar_index == 0:",
                "            return {'signal': {'category': 'open', 'direction': 'long', 'description': 'self-check open', 'reference_price': context.bar['close']}, 'intent': {'action': 'open', 'direction': 'long', 'quantity': 1, 'description': 'self-check open', 'reference_price': context.bar['close']}}",
                "        sys.stderr.write('SELF_CHECK_SECOND_BAR_BLOCKED pid=%d simulation=%s\\n' % (os.getpid(), json.dumps(context.simulation, sort_keys=True, default=str)))",
                "        sys.stderr.flush()",
                "        time.sleep(30)",
                "        return None"
            }), new UTF8Encoding(false));
            _restoringRunView = true;
            StrategyPathText.Text = runningCloseStrategyPath;
            StrategyParametersText.Text = "{}";
            StartInclusiveText.Text = start.ToString("O", CultureInfo.InvariantCulture);
            EndExclusiveText.Text = end.ToString("O", CultureInfo.InvariantCulture);
            _restoringRunView = false;
            var knownTradeRuns = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(tradeStore.RunsDirectory))
            {
                foreach (var directory in Directory.GetDirectories(tradeStore.RunsDirectory))
                {
                    var name = Path.GetFileName(directory);
                    if (name is not null) knownTradeRuns.Add(name);
                }
            }
            var runningCloseOperation = TradeReplayButtonCoreAsync();
            string? runningCloseRunId = null;
            int? runningCloseWorkerPid = null;
            string? runningCloseWorkerName = null;
            string? runningCloseWorkerExecutable = null;
            string? runningCloseWorkerStartTime = null;
            var runningCloseTaskActiveBeforeStop = false;
            var runningCloseRunWasRunningBeforeStop = false;
            var runningCloseWorkerLiveBeforeStop = false;
            var runningCloseSecondBarBlockedBeforeStop = false;
            var runningCloseSimulationConfirmed = false;
            var observeDeadline = Stopwatch.StartNew();
            while (observeDeadline.Elapsed < TimeSpan.FromSeconds(2))
            {
                var candidate = Directory.Exists(tradeStore.RunsDirectory)
                    ? Directory.GetDirectories(tradeStore.RunsDirectory).Select(Path.GetFileName).Where(name => name is not null && !knownTradeRuns.Contains(name)).Cast<string>().OrderByDescending(name => name, StringComparer.Ordinal).FirstOrDefault()
                    : null;
                if (candidate is not null)
                {
                    runningCloseRunId = candidate;
                    var candidateDirectory = Path.Combine(tradeStore.RunsDirectory, candidate);
                    var workerStderr = Path.Combine(candidateDirectory, "worker-stderr.log");
                    if (File.Exists(workerStderr))
                    {
                        using var stderrReader = new StreamReader(new FileStream(workerStderr, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                        var stderrText = stderrReader.ReadToEnd();
                        var marker = stderrText.Split('\n').FirstOrDefault(line => line.Contains("SELF_CHECK_SECOND_BAR_BLOCKED", StringComparison.Ordinal));
                        if (marker is not null)
                        {
                            runningCloseSecondBarBlockedBeforeStop = true;
                            runningCloseSimulationConfirmed = marker.Contains("simulation=", StringComparison.Ordinal) && marker.Contains("\"account\":", StringComparison.Ordinal) && marker.Contains("\"position\":", StringComparison.Ordinal) && marker.Contains("\"fees\": 2", StringComparison.Ordinal);
                            var pidPart = marker.Split(' ').FirstOrDefault(part => part.StartsWith("pid=", StringComparison.Ordinal));
                            if (pidPart is not null && int.TryParse(pidPart[4..], out var pid))
                            {
                                runningCloseWorkerPid = pid;
                                try
                                {
                                    using var workerProcess = Process.GetProcessById(pid);
                                    runningCloseWorkerName = workerProcess.ProcessName;
                                    runningCloseWorkerExecutable = workerProcess.MainModule?.FileName;
                                    runningCloseWorkerStartTime = workerProcess.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                                    runningCloseWorkerLiveBeforeStop = !workerProcess.HasExited &&
                                        (string.Equals(runningCloseWorkerExecutable, pythonPath, StringComparison.OrdinalIgnoreCase) ||
                                         workerProcess.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase));
                                }
                                catch { }
                            }
                        }
                    }
                    try { runningCloseRunWasRunningBeforeStop = tradeStore.ReadRecord(candidate).Status == "running"; } catch { }
                    runningCloseTaskActiveBeforeStop = !runningCloseOperation.IsCompleted;
                    if (runningCloseTaskActiveBeforeStop && runningCloseRunWasRunningBeforeStop && runningCloseWorkerLiveBeforeStop && runningCloseSecondBarBlockedBeforeStop && runningCloseSimulationConfirmed) break;
                }
                await Task.Delay(50);
            }
            var runningCloseStopRequestedAt = DateTimeOffset.UtcNow;
            var runningCloseDrainWatch = Stopwatch.StartNew();
            var runningCloseDrained = await StopActiveOperationsAndWaitAsync("自检运行中退出收尾");
            var runningCloseStopCompletedAt = DateTimeOffset.UtcNow;
            await runningCloseOperation;
            runningCloseDrainWatch.Stop();
            var runningCloseObjectsClean = _tradeReplayOperation is null && _tradeReplayCancellation is null;
            var runningCloseRecord = runningCloseRunId is null ? null : tradeStore.ReadRecord(runningCloseRunId);
            var runningCloseWorkerGone = true;
            var runningCloseWorkerPidReused = false;
            if (runningCloseWorkerPid is int observedPid)
            {
                try
                {
                    using var observedProcess = Process.GetProcessById(observedPid);
                    var currentExecutable = observedProcess.MainModule?.FileName;
                    var currentStartTime = observedProcess.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                    runningCloseWorkerPidReused = !string.Equals(currentExecutable, runningCloseWorkerExecutable, StringComparison.OrdinalIgnoreCase) || !string.Equals(currentStartTime, runningCloseWorkerStartTime, StringComparison.Ordinal);
                    runningCloseWorkerGone = observedProcess.HasExited || runningCloseWorkerPidReused;
                }
                catch (ArgumentException) { runningCloseWorkerGone = true; }
            }
            var runningCloseEvidenceDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "running-close-evidence");
            Directory.CreateDirectory(runningCloseEvidenceDirectory);
            if (runningCloseRunId is not null)
            {
                var runningCloseSourceDirectory = Path.Combine(tradeStore.RunsDirectory, runningCloseRunId);
                foreach (var fileName in new[] { "run.json", "fills.jsonl", "equity.jsonl", "intents.jsonl", "orders.jsonl", "unfilled.jsonl", "worker.log", "worker-stderr.log", "replay-metrics.json" })
                {
                    var source = Path.Combine(runningCloseSourceDirectory, fileName);
                    if (File.Exists(source)) File.Copy(source, Path.Combine(runningCloseEvidenceDirectory, fileName), true);
                }
            }
            _restoringRunView = true;
            StrategyPathText.Text = tradePath;
            StrategyParametersText.Text = "{\"scenario\":\"f01\"}";
            StartInclusiveText.Text = start.ToString("O", CultureInfo.InvariantCulture);
            EndExclusiveText.Text = end.ToString("O", CultureInfo.InvariantCulture);
            _restoringRunView = false;
            await TradeReplayButtonCoreAsync();
            var rerunRecord = _activeTradeRunId is null ? null : tradeStore.ReadRecord(_activeTradeRunId);
            checks["running_close_drain"] = new
            {
                run = runningCloseRunId,
                worker_pid = runningCloseWorkerPid,
                worker_name = runningCloseWorkerName,
                worker_executable = runningCloseWorkerExecutable,
                worker_start_time = runningCloseWorkerStartTime,
                stop_requested_at = runningCloseStopRequestedAt,
                stop_completed_at = runningCloseStopCompletedAt,
                task_active_before_stop = runningCloseTaskActiveBeforeStop,
                run_was_running_before_stop = runningCloseRunWasRunningBeforeStop,
                worker_live_before_stop = runningCloseWorkerLiveBeforeStop,
                second_bar_blocked_before_stop = runningCloseSecondBarBlockedBeforeStop,
                simulation_confirmed_before_stop = runningCloseSimulationConfirmed,
                drained = runningCloseDrained,
                drain_ms = runningCloseDrainWatch.Elapsed.TotalMilliseconds,
                status = runningCloseRecord?.Status,
                error_kind = runningCloseRecord?.ErrorKind,
                created_at = runningCloseRecord?.CreatedAt,
                finished_at = runningCloseRecord?.FinishedAt,
                partial = runningCloseRecord is not null && runningCloseRecord.Status != "completed",
                fills = runningCloseRecord?.FillCount ?? -1,
                fees = runningCloseRecord?.TotalFees ?? -1,
                equity = runningCloseRecord?.FinalEquity ?? -1,
                worker_gone = runningCloseWorkerGone,
                worker_pid_reused = runningCloseWorkerPidReused,
                objects_clean = runningCloseObjectsClean,
                evidence_directory = runningCloseEvidenceDirectory,
                rerun_status = rerunRecord?.Status,
                rerun_completed = rerunRecord?.Status == "completed"
            };
            checks["cleanup"] = new { replay = _replayOperation is null, trade_replay = _tradeReplayOperation is null, strategy_load = _strategyLoadOperation is null };

            UpdateLayout();
            checks["chart_children"] = ChartCanvas.Children.Count;
            checks["equity_children"] = EquityCanvas.Children.Count;
            var passedConditions = new
            {
                required_controls = checks["required_controls"] is true,
                context_gate_non_reentrant = checks["context_gate_non_reentrant"] is true,
                startup_ready = checks["startup_ready_file"] is true,
                entry_collision = ((dynamic)checks["entry_collision"]!).entry_blocked && ((dynamic)checks["entry_collision"]!).cancel_available && ((dynamic)checks["entry_collision"]!).snapshot_unchanged && ((dynamic)checks["entry_collision"]!).start_unchanged && ((dynamic)checks["entry_collision"]!).end_unchanged && ((dynamic)checks["entry_collision"]!).strategy_unchanged && ((dynamic)checks["entry_collision"]!).trade_cleanup,
                worker_exists = checks["worker_exists"] is true,
                python_exists = checks["python_exists"] is true,
                signal_completed = signalResult.Status == "completed",
                signal_count = signalResult.Signals.Count == 2,
                trade_completed = tradeRecord.Status == "completed",
                f01_business = f01Business,
                equity_curve = tradeCurvePassed,
                signal_location = signalLocated,
                fill_location = fillLocated,
                zoom_path = ((dynamic)checks["p02"]!).ten_thousand_zoom,
                ten_thousand_zoom = ((dynamic)checks["p02"]!).ten_thousand_zoom,
                exports = ((dynamic)checks["exports"]!).fills && ((dynamic)checks["exports"]!).equity && ((dynamic)checks["exports"]!).summary && ((dynamic)checks["exports"]!).signals,
                rendered_tables = ((dynamic)checks["tables_rendered"]!).signals == ((dynamic)checks["trade_replay"]!).fills && ((dynamic)checks["tables_rendered"]!).fills == ((dynamic)checks["trade_replay"]!).fills && ((dynamic)checks["tables_rendered"]!).chart_children > 10 && ((dynamic)checks["tables_rendered"]!).equity_children > 0 && ((dynamic)checks["tables_rendered"]!).summary,
                readonly_reopen = ((dynamic)checks["readonly_reopen"]!).bound_snapshot && ((dynamic)checks["readonly_reopen"]!).chart_bound && ((dynamic)checks["readonly_reopen"]!).start && ((dynamic)checks["readonly_reopen"]!).end && ((dynamic)checks["readonly_reopen"]!).explore && ((dynamic)checks["readonly_reopen"]!).strategy && ((dynamic)checks["readonly_reopen"]!).parameters && ((dynamic)checks["readonly_reopen"]!).config && ((dynamic)checks["readonly_reopen"]!).read_only,
                copy_config = ((dynamic)checks["copy_config"]!).new_context && ((dynamic)checks["copy_config"]!).new_run && ((dynamic)checks["copy_config"]!).snapshot && ((dynamic)checks["copy_config"]!).strategy && ((dynamic)checks["copy_config"]!).parameters && ((dynamic)checks["copy_config"]!).start && ((dynamic)checks["copy_config"]!).end && ((dynamic)checks["copy_config"]!).explore && ((dynamic)checks["copy_config"]!).config && ((dynamic)checks["copy_config"]!).original_unchanged,
                zero_trade = ((dynamic)checks["zero_trade"]!).status == "completed" && ((dynamic)checks["zero_trade"]!).fills == 0 && ((dynamic)checks["zero_trade"]!).win_rate_not_applicable,
                last_bar_unfilled = ((dynamic)checks["last_bar_unfilled"]!).reason,
                cancel_observed = ((dynamic)checks["cancel"]!).ui_cancel,
                running_close_drain = ((dynamic)checks["running_close_drain"]!).task_active_before_stop && ((dynamic)checks["running_close_drain"]!).run_was_running_before_stop && ((dynamic)checks["running_close_drain"]!).worker_live_before_stop && ((dynamic)checks["running_close_drain"]!).second_bar_blocked_before_stop && ((dynamic)checks["running_close_drain"]!).simulation_confirmed_before_stop && ((dynamic)checks["running_close_drain"]!).drained && ((dynamic)checks["running_close_drain"]!).status == "cancelled" && ((dynamic)checks["running_close_drain"]!).error_kind == "cancelled" && ((dynamic)checks["running_close_drain"]!).fills == 1 && ((dynamic)checks["running_close_drain"]!).fees == 2 && ((dynamic)checks["running_close_drain"]!).equity == 9998 && ((dynamic)checks["running_close_drain"]!).worker_gone && !((dynamic)checks["running_close_drain"]!).worker_pid_reused && ((dynamic)checks["running_close_drain"]!).objects_clean && ((dynamic)checks["running_close_drain"]!).rerun_completed,
                cleanup = ((dynamic)checks["cleanup"]!).replay && ((dynamic)checks["cleanup"]!).trade_replay && ((dynamic)checks["cleanup"]!).strategy_load,
                p02_five_runs = ((dynamic)checks["p02"]!).five_runs && ((dynamic)checks["p02"]!).ten_thousand_zoom,
                p03_dispatcher = ((dynamic)checks["p03_dispatcher_probe"]!).replay_during_probe && ((dynamic)checks["p03_dispatcher_probe"]!).zoom_during_probe && ((dynamic)checks["p03_dispatcher_probe"]!).max_interval_under_1s,
                p03_elapsed = ((dynamic)checks["p03_dispatcher_probe"]!).elapsed_ms > 0,
                result_rendered = ((dynamic)checks["rendered_result"]!).chart_children > 10 && ((dynamic)checks["rendered_result"]!).equity_children > 0 && ((dynamic)checks["rendered_result"]!).signal_rows == ((dynamic)checks["trade_replay"]!).fills && ((dynamic)checks["rendered_result"]!).fill_rows == ((dynamic)checks["trade_replay"]!).fills && ((dynamic)checks["rendered_result"]!).result_text && ((dynamic)checks["rendered_png"]!).has_visible_content && ((dynamic)checks["rendered_png"]!).chart_visible && ((dynamic)checks["rendered_png"]!).equity_visible && ((dynamic)checks["rendered_png"]!).result_visible
            };
            checks["passed_conditions"] = passedConditions;
             checks["passed"] = passedConditions.required_controls && passedConditions.context_gate_non_reentrant && passedConditions.startup_ready && passedConditions.entry_collision && passedConditions.worker_exists && passedConditions.python_exists && passedConditions.signal_completed && passedConditions.signal_count && passedConditions.trade_completed && passedConditions.f01_business && passedConditions.equity_curve && passedConditions.signal_location && passedConditions.fill_location && passedConditions.zoom_path && passedConditions.ten_thousand_zoom && passedConditions.exports && passedConditions.rendered_tables && passedConditions.readonly_reopen && passedConditions.copy_config && passedConditions.zero_trade && passedConditions.last_bar_unfilled && passedConditions.cancel_observed && passedConditions.running_close_drain && passedConditions.cleanup && passedConditions.p02_five_runs && passedConditions.p03_dispatcher && passedConditions.p03_elapsed && passedConditions.result_rendered;
            checks["elapsed_ms"] = started.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            try { await StopActiveOperationsAndWaitAsync("自检异常收尾"); } catch (Exception cleanupError) { checks["cleanup_error"] = cleanupError.ToString(); }
            checks["passed"] = false;
            checks["error"] = ex.ToString();
            SaveSelfCheckImage(imagePath);
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return checks.TryGetValue("passed", out var passed) && passed is true;
    }

    private async Task<(int Ticks, double MaxIntervalMs, double ElapsedMs, bool ActionObserved)> RunWithDispatcherProbeAsync(Func<Task> operation, Action<int>? onTick = null)
    {
        var ticks = 0;
        var maxIntervalMs = 0.0;
        var elapsed = Stopwatch.StartNew();
        var previousTick = Stopwatch.GetTimestamp();
        var actionObserved = false;
        var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(20), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            maxIntervalMs = Math.Max(maxIntervalMs, (now - previousTick) * 1000.0 / Stopwatch.Frequency);
            previousTick = now;
            ticks++;
            if (onTick is not null)
            {
                onTick(ticks);
                actionObserved |= ticks == 1;
            }
        }, Dispatcher);
        timer.Start();
        try { await operation(); }
        finally { timer.Stop(); }
        elapsed.Stop();
        return (ticks, maxIntervalMs, elapsed.Elapsed.TotalMilliseconds, actionObserved);
    }

    private static string GetFileHash(string path) => File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : "";

    private static void CreateSelfCheckBars(string path, int count)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("timestamp,symbol,exchange,period,trading_day,open,high,low,close,volume,open_interest,is_complete");
        var first = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < count; index++)
        {
            var timestamp = first.AddDays(index);
            var close = 100m + index % 10;
            writer.WriteLine($"{timestamp:O},TEST,TEST,1d,{timestamp:yyyy-MM-dd},{close - 1},{close + 1},{close - 2},{close},1,1,true");
        }
    }

    private async Task BindSelfCheckTradeResultAsync(TradeReplayResult result)
    {
        var store = new TradeRunStore(_store.RootDirectory);
        var record = store.ReadRecord(result.RunId);
        _tradeRunDirectory = result.RunDirectory;
        _activeTradeRunId = result.RunId;
        _activeTradeRecord = record;
        _tradeFills = result.Fills;
        _tradeSignals = await Task.Run(() => store.ReadMany<SignalEvent>(result.RunId, "signals.jsonl"));
        _tradeEquity = await Task.Run(() => store.ReadEquitySample(result.RunId));
        _allSignals = _tradeSignals;
        _displayedSignals = _tradeSignals;
        SignalGrid.ItemsSource = _displayedSignals;
        TradeFillGrid.ItemsSource = _tradeFills;
        RunIdText.Text = result.RunId;
        _lastSignalResult = new SignalReplayResult(record.RunId, record.Status, _tradeSignals, result.RunDirectory, record.ErrorMessage);
        UpdateLayout();
        DrawEquityCurve();
        DrawChart();
    }

    private sealed record SelfCheckImageStats(int Width, int Height, int NonTransparentPixels, int NonZeroBytes, int NonWhitePixels, bool HasVisibleContent, bool ChartVisible, bool EquityVisible, bool ResultVisible);

    private sealed record CapturedVisual(BitmapSource Bitmap, int Width, int Height, int NonTransparentPixels, int NonZeroBytes, int NonWhitePixels, bool HasVisibleContent);

    private static CapturedVisual DescribeBitmap(BitmapSource bitmap, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var nonTransparentPixels = 0;
        var nonZeroBytes = 0;
        var nonWhitePixels = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var alpha = pixels[offset + 3];
            if (blue != 0 || green != 0 || red != 0 || alpha != 0)
                nonZeroBytes += (blue != 0 ? 1 : 0) + (green != 0 ? 1 : 0) + (red != 0 ? 1 : 0);
            if (alpha != 0) nonTransparentPixels++;
            if (alpha > 0 && (red < 245 || green < 245 || blue < 245)) nonWhitePixels++;
        }
        return new CapturedVisual(bitmap, width, height, nonTransparentPixels, nonZeroBytes, nonWhitePixels, nonTransparentPixels > 0 && nonWhitePixels > 0);
    }

    private static CapturedVisual CaptureVisual(Visual visual, FrameworkElement element)
    {
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return DescribeBitmap(bitmap, width, height);
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private SelfCheckImageStats SaveSelfCheckImage(string imagePath)
    {
        UpdateLayout();
        var chart = CaptureVisual(ChartCanvas, ChartCanvas);
        var equity = CaptureVisual(EquityCanvas, EquityCanvas);
        var result = CaptureVisual(TradeResultText, TradeResultText);
        var root = Content is FrameworkElement content
            ? CaptureVisual(content, content)
            : new CapturedVisual(new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32), 1, 1, 0, 0, 0, false);
        var directory = Path.GetDirectoryName(imagePath)!;
        SaveBitmap(root.Bitmap, imagePath);
        SaveBitmap(chart.Bitmap, Path.Combine(directory, Path.GetFileNameWithoutExtension(imagePath) + "-chart.png"));
        SaveBitmap(equity.Bitmap, Path.Combine(directory, Path.GetFileNameWithoutExtension(imagePath) + "-equity.png"));
        SaveBitmap(result.Bitmap, Path.Combine(directory, Path.GetFileNameWithoutExtension(imagePath) + "-result.png"));
        return new SelfCheckImageStats(root.Width, root.Height, root.NonTransparentPixels, root.NonZeroBytes, root.NonWhitePixels, root.HasVisibleContent, chart.HasVisibleContent, equity.HasVisibleContent, result.HasVisibleContent);
    }
}
