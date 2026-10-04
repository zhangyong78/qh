using System.IO;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;

namespace QhApp;

public partial class App : System.Windows.Application
{
    private static Stopwatch StartupClock = Stopwatch.StartNew();
    private static double? _loadedMs;
    private static double? _contentRenderedMs;
    private static double? _firstDispatcherMs;
    private static string? _startupReadyPath;

    internal static object StartupMarkers() => new { loaded_ms = _loadedMs, content_rendered_ms = _contentRenderedMs, first_dispatcher_ms = _firstDispatcherMs, startup_ready_path = _startupReadyPath };
    internal static string? StartupReadyPath => _startupReadyPath;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        StartupClock = Stopwatch.StartNew();
        _loadedMs = null;
        _contentRenderedMs = null;
        _firstDispatcherMs = null;
        _startupReadyPath = null;
        base.OnStartup(e);
        var selfCheckIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--self-check", StringComparison.OrdinalIgnoreCase));
        if (selfCheckIndex >= 0)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            var output = selfCheckIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[selfCheckIndex + 1]) : Path.Combine(AppContext.BaseDirectory, "self-check.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            _startupReadyPath = Path.Combine(Path.GetDirectoryName(output)!, "startup-ready.json");
            var dataRootIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, "--self-check-data-root", StringComparison.OrdinalIgnoreCase));
            var dataRoot = dataRootIndex >= 0 && dataRootIndex + 1 < e.Args.Length
                ? Path.GetFullPath(e.Args[dataRootIndex + 1])
                : Path.Combine(Path.GetTempPath(), "QhResearch-self-check", Guid.NewGuid().ToString("N"));
            var window = new MainWindow(dataRoot, autoLoadLatest: false) { ShowInTaskbar = false, WindowState = System.Windows.WindowState.Normal, Width = 1280, Height = 820 };
            MainWindow = window;
            window.Loaded += (_, _) => _loadedMs ??= StartupClock.Elapsed.TotalMilliseconds;
            window.ContentRendered += (_, _) => _contentRenderedMs ??= StartupClock.Elapsed.TotalMilliseconds;
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                _firstDispatcherMs ??= StartupClock.Elapsed.TotalMilliseconds;
                File.WriteAllText(_startupReadyPath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    marker = "first_dispatcher_business_ready",
                    process_id = Environment.ProcessId,
                    timestamp = DateTimeOffset.UtcNow,
                    elapsed_ms = StartupClock.Elapsed.TotalMilliseconds
                }), new System.Text.UTF8Encoding(false));
                _ = RunSelfCheckAsync(window, output);
            }));
            return;
        }

        var dataRootArgument = Array.FindIndex(e.Args, argument => string.Equals(argument, "--data-root", StringComparison.OrdinalIgnoreCase));
        var runArgument = Array.FindIndex(e.Args, argument => string.Equals(argument, "--trade-run", StringComparison.OrdinalIgnoreCase));
        var dataRootPath = dataRootArgument >= 0 && dataRootArgument + 1 < e.Args.Length
            ? Path.GetFullPath(e.Args[dataRootArgument + 1]) : null;
        var runId = runArgument >= 0 && runArgument + 1 < e.Args.Length ? e.Args[runArgument + 1] : null;
        MainWindow = new MainWindow(dataRootPath, autoLoadLatest: runId is null);
        if (runId is not null)
        {
            var window = (MainWindow)MainWindow;
            window.Loaded += async (_, _) => await window.OpenTradeRunAsync(runId);
        }
        MainWindow.Show();
    }

    private async Task RunSelfCheckAsync(MainWindow window, string output)
    {
        try
        {
            var result = await window.RunInternalSelfCheckAsync(output);
            Shutdown(result ? 0 : 1);
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
            Shutdown(1);
        }
    }
}
