using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TokenPulse.Models;
using TokenPulse.Services;
using Forms = System.Windows.Forms;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using ShapePath = System.Windows.Shapes.Path;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace TokenPulse;

public partial class MainWindow : Window
{
    private readonly CodexUsageService _codexUsageService = new();
    private readonly ClaudeUsageService _claudeUsageService = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private Forms.NotifyIcon? _trayIcon;
    private bool _isRefreshing;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CreateTrayIcon();
        PositionNearWorkArea();
        _refreshTimer.Start();
        await RefreshUsageAsync();
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("위젯 열기", null, (_, _) => Dispatcher.Invoke(ShowWidget));
        menu.Items.Add("새로고침", null, (_, _) => Dispatcher.InvokeAsync(RefreshUsageAsync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Agent Usage",
            Icon = LoadApplicationIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWidget);
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var icon = Environment.ProcessPath is not null
            ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;
        return icon ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private void PositionNearWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;
    }

    private async Task RefreshUsageAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        UpdatedText.Text = "사용량 확인 중…";
        StartRefreshAnimation();

        try
        {
            var codexTask = _codexUsageService.GetUsageAsync(_lifetime.Token);
            var claudeTask = _claudeUsageService.GetUsageAsync(_lifetime.Token);
            await Task.WhenAll(codexTask, claudeTask);

            UpdateProvider(
                codexTask.Result,
                CodexPlanText,
                CodexStatusBadge,
                CodexStatusText,
                CodexRemainingText,
                CodexWindowLabel,
                CodexResetText,
                null,
                null,
                null,
                CodexArc,
                (MediaBrush)FindResource("CodexBrush"));
            CodexResetDateText.Text = codexTask.Result.IsAvailable
                && codexTask.Result.Primary?.ResetsAt is { } codexReset
                    ? $"{codexReset.ToLocalTime():M월 d일}"
                    : "";

            UpdateProvider(
                claudeTask.Result,
                ClaudePlanText,
                ClaudeStatusBadge,
                ClaudeStatusText,
                ClaudeRemainingText,
                ClaudeWindowLabel,
                ClaudeResetText,
                ClaudeWeeklyBar,
                ClaudeSecondaryLabel,
                ClaudeWeeklyText,
                ClaudeArc,
                (MediaBrush)FindResource("ClaudeBrush"));
            ClaudeWeeklyResetText.Text = claudeTask.Result.Secondary is not null
                ? FormatReset(claudeTask.Result.Secondary.ResetsAt)
                : "주간 초기화 정보 없음";

            var bothAvailable = codexTask.Result.IsAvailable && claudeTask.Result.IsAvailable;
            LiveDot.Fill = bothAvailable
                ? (MediaBrush)FindResource("SuccessBrush")
                : (MediaBrush)FindResource("WarningBrush");
            LiveText.Text = bothAvailable ? "Live" : "확인 필요";
            UpdatedText.Text = $"방금 업데이트됨 · {DateTime.Now:HH:mm}";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            StopRefreshAnimation();
            _isRefreshing = false;
        }
    }

    private void UpdateProvider(
        UsageSnapshot snapshot,
        System.Windows.Controls.TextBlock planText,
        System.Windows.Controls.Border statusBadge,
        System.Windows.Controls.TextBlock statusText,
        System.Windows.Controls.TextBlock remainingText,
        System.Windows.Controls.TextBlock windowLabel,
        System.Windows.Controls.TextBlock resetText,
        System.Windows.Controls.ProgressBar? secondaryBar,
        System.Windows.Controls.TextBlock? secondaryLabel,
        System.Windows.Controls.TextBlock? secondaryText,
        ShapePath arc,
        MediaBrush accentBrush)
    {
        planText.Text = snapshot.Plan;
        windowLabel.Text = snapshot.PrimaryLabel;
        if (secondaryLabel is not null)
        {
            secondaryLabel.Text = snapshot.SecondaryLabel;
        }

        if (!snapshot.IsAvailable || snapshot.Primary is null)
        {
            statusText.Text = "확인 필요";
            statusText.Foreground = (MediaBrush)FindResource("WarningBrush");
            statusBadge.Background = new SolidColorBrush(MediaColor.FromArgb(24, 255, 198, 109));
            statusBadge.ToolTip = snapshot.Error;
            remainingText.Text = "--%";
            resetText.Text = snapshot.Error ?? "사용량을 확인할 수 없습니다.";
            resetText.ToolTip = snapshot.Error;
            if (secondaryBar is not null)
            {
                secondaryBar.Value = 0;
            }
            if (secondaryText is not null)
            {
                secondaryText.Text = "--%";
            }
            SetArc(arc, 0);
            return;
        }

        statusText.Text = "정상";
        statusText.Foreground = accentBrush;
        statusBadge.Background = accentBrush == FindResource("CodexBrush")
            ? new SolidColorBrush(MediaColor.FromArgb(21, 43, 141, 255))
            : new SolidColorBrush(MediaColor.FromArgb(21, 255, 157, 92));
        statusBadge.ToolTip = null;

        var remaining = snapshot.Primary.RemainingPercent;
        remainingText.Text = $"{Math.Round(remaining):0}%";
        resetText.Text = FormatReset(snapshot.Primary.ResetsAt);
        resetText.ToolTip = null;
        SetArc(arc, remaining);

        if (snapshot.Secondary is not null && secondaryBar is not null && secondaryText is not null)
        {
            secondaryBar.Value = snapshot.Secondary.RemainingPercent;
            secondaryText.Text = $"{Math.Round(snapshot.Secondary.RemainingPercent):0}%";
        }
        else if (secondaryBar is not null && secondaryText is not null)
        {
            secondaryBar.Value = 0;
            secondaryText.Text = "정보 없음";
        }
    }

    private static string FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return "초기화 시각 정보 없음";
        }

        var remaining = resetsAt.Value.ToLocalTime() - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "곧 초기화";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}일 {remaining.Hours}시간 후 초기화";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}시간 {remaining.Minutes}분 후 초기화";
        }

        return $"{Math.Max(1, remaining.Minutes)}분 후 초기화";
    }

    private static void SetArc(ShapePath path, double percent)
    {
        const double size = 92;
        const double padding = 12;
        var value = Math.Clamp(percent, 0, 100);

        if (value <= 0)
        {
            path.Data = Geometry.Empty;
            return;
        }

        if (value >= 99.999)
        {
            path.Data = new EllipseGeometry(
                new Rect(padding, padding, size - (padding * 2), size - (padding * 2)));
            return;
        }

        var center = new WpfPoint(size / 2, size / 2);
        var radius = (size / 2) - padding;
        var start = new WpfPoint(center.X, center.Y - radius);
        var angle = value / 100 * 360;
        var radians = (angle - 90) * Math.PI / 180;
        var end = new WpfPoint(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            end,
            new WpfSize(radius, radius),
            0,
            angle > 180,
            SweepDirection.Clockwise,
            true));

        path.Data = new PathGeometry([figure]);
    }

    private void StartRefreshAnimation()
    {
        var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        RefreshGlyph.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
        RefreshGlyph.RenderTransform = new RotateTransform();
        RefreshGlyph.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void StopRefreshAnimation()
    {
        RefreshGlyph.RenderTransform?.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsButton.ContextMenu is null)
        {
            return;
        }

        SettingsButton.ContextMenu.PlacementTarget = SettingsButton;
        SettingsButton.ContextMenu.IsOpen = true;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshUsageAsync();

    private void Topmost_Click(object sender, RoutedEventArgs e) =>
        Topmost = TopmostMenuItem.IsChecked;

    private void OpenCodexUsage_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://chatgpt.com/codex/settings/usage");

    private void OpenClaudeUsage_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://claude.ai/settings/usage");

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void ShowWidget()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _refreshTimer.Stop();
        _lifetime.Cancel();
        _trayIcon?.Icon?.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        System.Windows.Application.Current.Shutdown();
    }
}
