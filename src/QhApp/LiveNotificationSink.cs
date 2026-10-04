using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Qh.Core;

namespace QhApp;

internal sealed class LiveNotificationSink : ILiveNotificationSink
{
    private readonly Window _owner;

    public LiveNotificationSink(Window owner) => _owner = owner;

    public void ShowDesktop(LiveReminderInstance instance, LiveSignalLedgerRecord signal)
    {
        var popup = new Window
        {
            Owner = _owner,
            Title = "期货提醒",
            Width = 360,
            Height = 150,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    new TextBlock { Text = $"{signal.Direction} · {signal.Category}", FontSize = 18, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = $"{instance.Symbol} · {PeriodLabel(instance.DurationSeconds)} · {signal.BarEnd:MM-dd HH:mm}", Margin = new Thickness(0, 8, 0, 0) },
                    new TextBlock { Text = signal.Description, Foreground = System.Windows.Media.Brushes.DarkSlateGray, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap }
                }
            }
        };
        popup.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); if (popup.IsVisible) popup.Close(); };
        timer.Start();
    }

    public void PlaySound(LiveReminderInstance instance, LiveSignalLedgerRecord signal) => SystemSounds.Asterisk.Play();

    public void ShowTest(Window owner)
    {
        var popup = new Window
        {
            Owner = owner,
            Title = "测试通知（不会写入信号账本）",
            Width = 360,
            Height = 120,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock { Text = "这是测试通知，不代表真实策略信号。", Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap, FontSize = 16 }
        };
        popup.Show();
        SystemSounds.Asterisk.Play();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { timer.Stop(); if (popup.IsVisible) popup.Close(); };
        timer.Start();
    }

    private static string PeriodLabel(int durationSeconds) => durationSeconds switch
    {
        60 => "1分钟",
        300 => "5分钟",
        900 => "15分钟",
        1800 => "30分钟",
        3600 => "60分钟",
        86400 => "日线",
        _ => $"{durationSeconds}秒"
    };
}
