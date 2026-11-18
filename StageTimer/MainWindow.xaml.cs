using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace StageTimer;

public partial class MainWindow : Window
{
    private enum TimerState { Idle, Running, Paused, Finished }
    private enum Phase { Normal, Warning, Overtime }

    private const int HotkeyStartPause = 1;
    private const int HotkeyReset = 2;
    private const int HotkeyCompact = 3;
    private const int HotkeyToggleVisible = 4;

    private const double NormalMinWidth = 460, NormalMinHeight = 320;
    private const double CompactMinWidth = 220, CompactMinHeight = 90;

    private static readonly Brush DigitNormal = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush DigitWarning = Frozen(0xFF, 0xB0, 0x20);
    private static readonly Brush DigitOvertime = Frozen(0xFF, 0x3B, 0x30);
    private static readonly Brush PanelBackground = Frozen(0x0D, 0x0D, 0x0D);
    private static readonly Brush PanelBorder = Frozen(0x2A, 0x2A, 0x2A);
    private static readonly Brush PanelOvertimeBackground = Frozen(0xC8, 0x10, 0x2E);
    private static readonly Brush PanelOvertimeBorder = Frozen(0xFF, 0x3B, 0x30);
    private static readonly Brush PlayBackground = Frozen(0x22, 0xA4, 0x5D);
    private static readonly Brush PauseBackground = Frozen(0xE0, 0x8E, 0x0B);
    private static readonly Brush NeutralBackground = Frozen(0x3A, 0x3A, 0x3A);
    private static readonly Brush PinOn = Frozen(0xFF, 0x5A, 0x4E);
    private static readonly Brush PinOff = Frozen(0x80, 0x80, 0x80);

    private readonly AppSettings _settings;
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly List<string> _hotkeyFailures = new();

    private TimerState _state = TimerState.Idle;
    private bool _alertPlayed;
    private bool _compact;
    private Rect _normalBounds;
    private IntPtr _hwnd;

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private double _dragStartLeft, _dragStartTop;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        _tickTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _tickTimer.Tick += (_, _) => UpdateDisplay();

        // PowerPoint 进入放映时可能把自己的窗口提到最前，定期重新声明置顶以保证计时器不被盖住。
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _topmostTimer.Tick += (_, _) => EnforceTopmost();

        RestoreWindowBounds();
        ApplyWindowSettings();
        UpdateControls();
        UpdateDisplay();

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ───────────────────────── 窗口初始化 / 置顶 / 快捷键 ─────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        ApplyNoActivate();
        RegisterHotkeys();
        EnforceTopmost();
        _topmostTimer.Start();
    }

    private void ApplyWindowSettings()
    {
        Opacity = _settings.Opacity;
        Topmost = _settings.AlwaysOnTop;
        PinButton.Foreground = _settings.AlwaysOnTop ? PinOn : PinOff;
        PinButton.Content = _settings.AlwaysOnTop ? "\uE718" : "\uE77A";
        EnforceTopmost();
    }

    /// <summary>
    /// WS_EX_NOACTIVATE：点击计时器按钮时不激活本窗口，键盘焦点留在 PowerPoint，
    /// 翻页笔 / 方向键仍然控制幻灯片。
    /// </summary>
    private void ApplyNoActivate()
    {
        if (_hwnd == IntPtr.Zero) return;
        long style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        style = _settings.NoActivate
            ? style | NativeMethods.WS_EX_NOACTIVATE
            : style & ~NativeMethods.WS_EX_NOACTIVATE;
        style |= NativeMethods.WS_EX_APPWINDOW; // 保留任务栏按钮
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, style);
    }

    private void EnforceTopmost()
    {
        if (_hwnd == IntPtr.Zero || !_settings.AlwaysOnTop) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void RegisterHotkeys()
    {
        RegisterHotkey(HotkeyStartPause, Key.S, "Ctrl+Alt+S");
        RegisterHotkey(HotkeyReset, Key.R, "Ctrl+Alt+R");
        RegisterHotkey(HotkeyCompact, Key.M, "Ctrl+Alt+M");
        RegisterHotkey(HotkeyToggleVisible, Key.H, "Ctrl+Alt+H");
    }

    private void RegisterHotkey(int id, Key key, string label)
    {
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, mods, vk))
            _hotkeyFailures.Add(label);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case HotkeyStartPause: ToggleStartPause(); break;
            case HotkeyReset: ResetTimer(); break;
            case HotkeyCompact: ToggleCompact(); break;
            case HotkeyToggleVisible: ToggleVisible(); break;
            default: return IntPtr.Zero;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void ToggleVisible()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            EnforceTopmost();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        for (int id = HotkeyStartPause; id <= HotkeyToggleVisible; id++)
            NativeMethods.UnregisterHotKey(_hwnd, id);

        Rect bounds = _compact ? _normalBounds : new Rect(Left, Top, Width, Height);
        if (_compact)
        {
            _settings.CompactWidth = Width;
            _settings.CompactHeight = Height;
        }
        _settings.Left = bounds.Left;
        _settings.Top = bounds.Top;
        _settings.Width = bounds.Width;
        _settings.Height = bounds.Height;
        _settings.Save();
    }

    private void RestoreWindowBounds()
    {
        if (_settings.Width is double w && w > 0) Width = Math.Max(w, NormalMinWidth);
        if (_settings.Height is double h && h > 0) Height = Math.Max(h, NormalMinHeight);

        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        if (_settings.Left is double left && _settings.Top is double top &&
            virtualScreen.IntersectsWith(new Rect(left, top, Width, Height)))
        {
            Left = left;
            Top = top;
        }
        else
        {
            Rect work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }
    }

    // ───────────────────────── 计时逻辑 ─────────────────────────

    private void ToggleStartPause()
    {
        switch (_state)
        {
            case TimerState.Idle:
                if (_settings.DurationSeconds <= 0) return;
                _stopwatch.Restart();
                _alertPlayed = false;
                _state = TimerState.Running;
                break;
            case TimerState.Running:
                _stopwatch.Stop();
                _state = TimerState.Paused;
                break;
            case TimerState.Paused:
                _stopwatch.Start();
                _state = TimerState.Running;
                break;
            case TimerState.Finished:
                ResetTimer();
                return;
        }

        if (_state == TimerState.Running) _tickTimer.Start(); else _tickTimer.Stop();
        UpdateControls();
        UpdateDisplay();
    }

    private void ResetTimer()
    {
        _stopwatch.Reset();
        _tickTimer.Stop();
        _state = TimerState.Idle;
        UpdateControls();
        UpdateDisplay();
    }

    private void AdjustDuration(int deltaMinutes, int deltaSeconds)
    {
        if (_state != TimerState.Idle) return;

        int minutes = Math.Clamp(_settings.DurationSeconds / 60 + deltaMinutes, 0, 999);
        int seconds = ((_settings.DurationSeconds % 60) + deltaSeconds + 60) % 60;
        _settings.DurationSeconds = minutes * 60 + seconds;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        int shownSeconds;
        Phase phase;
        bool flashOn = false;

        if (_state == TimerState.Idle)
        {
            shownSeconds = _settings.DurationSeconds;
            phase = Phase.Normal;
        }
        else
        {
            double remaining = _settings.DurationSeconds - _stopwatch.Elapsed.TotalSeconds;
            if (remaining > 0)
            {
                shownSeconds = (int)Math.Ceiling(remaining);
                phase = _settings.WarningSeconds > 0 && shownSeconds <= _settings.WarningSeconds
                    ? Phase.Warning
                    : Phase.Normal;
            }
            else
            {
                OnReachedZero();
                shownSeconds = _settings.AllowOvertime ? (int)Math.Floor(-remaining) : 0;
                phase = Phase.Overtime;
                flashOn = _state == TimerState.Running && _settings.FlashOnOvertime &&
                          (int)(-remaining * 2) % 2 == 1;
            }
        }

        MinutesText.Text = (shownSeconds / 60).ToString("00");
        SecondsText.Text = (shownSeconds % 60).ToString("00");

        // 超时：面板红底白字；开启闪烁时在红底和黑底红字之间每 0.5 秒切换。
        bool redPanels = phase == Phase.Overtime && !flashOn;
        Brush digit = phase switch
        {
            Phase.Warning => DigitWarning,
            Phase.Overtime => redPanels ? DigitNormal : DigitOvertime,
            _ => DigitNormal,
        };
        Brush panelBg = redPanels ? PanelOvertimeBackground : PanelBackground;
        Brush panelBorder = phase == Phase.Overtime ? PanelOvertimeBorder : PanelBorder;

        MinutesText.Foreground = SecondsText.Foreground = digit;
        MinutesPanel.Background = SecondsPanel.Background = panelBg;
        MinutesPanel.BorderBrush = SecondsPanel.BorderBrush = panelBorder;
        ColonTop.Fill = ColonBottom.Fill = phase == Phase.Overtime ? DigitOvertime : digit;

        StatusLabel.Visibility = phase == Phase.Overtime ? Visibility.Visible : Visibility.Hidden;
        StatusLabel.Text = _settings.AllowOvertime ? "超时" : "时间到";
    }

    private void OnReachedZero()
    {
        if (_state != TimerState.Running) return;

        if (!_alertPlayed)
        {
            _alertPlayed = true;
            if (_settings.PlaySound) SystemSounds.Exclamation.Play();
        }

        if (!_settings.AllowOvertime)
        {
            _stopwatch.Stop();
            _tickTimer.Stop();
            _state = TimerState.Finished;
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        bool idle = _state == TimerState.Idle;
        MinutesArrows.Visibility = SecondsArrows.Visibility =
            idle && !_compact ? Visibility.Visible : Visibility.Hidden;

        bool resettable = _state is TimerState.Running or TimerState.Paused;
        ResetButton.Visibility = resettable ? Visibility.Visible : Visibility.Hidden;
        // 紧凑模式下重置按钮常驻，未开始时置灰，避免按钮位置跳动。
        CompactResetButton.IsEnabled = resettable;

        (object content, Brush background, string tip) = _state switch
        {
            TimerState.Running => ((object)"\uE769", PauseBackground, "暂停 (Ctrl+Alt+S)"),
            TimerState.Finished => ("\uE72C", NeutralBackground, "重置 (Ctrl+Alt+R)"),
            _ => ("\uE768", PlayBackground, idle ? "开始 (Ctrl+Alt+S)" : "继续 (Ctrl+Alt+S)"),
        };
        foreach (var button in new[] { StartPauseButton, CompactStartPauseButton })
        {
            button.Content = content;
            button.Background = background;
            button.ToolTip = tip;
        }
    }

    // ───────────────────────── 紧凑模式 ─────────────────────────

    private void ToggleCompact()
    {
        _compact = !_compact;
        if (_compact)
        {
            _normalBounds = new Rect(Left, Top, Width, Height);
            HeaderBar.Visibility = Visibility.Collapsed;
            ControlBar.Visibility = Visibility.Collapsed;
            CompactControls.Visibility = Visibility.Visible;
            TimerArea.Margin = new Thickness(8);
            MinWidth = CompactMinWidth;
            MinHeight = CompactMinHeight;
            Width = Math.Max(_settings.CompactWidth ?? 420, CompactMinWidth);
            Height = Math.Max(_settings.CompactHeight ?? 150, CompactMinHeight);
        }
        else
        {
            _settings.CompactWidth = Width;
            _settings.CompactHeight = Height;
            HeaderBar.Visibility = Visibility.Visible;
            ControlBar.Visibility = Visibility.Visible;
            CompactControls.Visibility = Visibility.Collapsed;
            TimerArea.Margin = new Thickness(12);
            MinWidth = NormalMinWidth;
            MinHeight = NormalMinHeight;
            Width = _normalBounds.Width;
            Height = _normalBounds.Height;
        }
        UpdateControls();
    }

    // ───────────────────────── 鼠标：拖动 / 双击 / 缩放 ─────────────────────────

    // 手动实现拖动：WS_EX_NOACTIVATE 窗口使用系统 DragMove 时不会实时跟随鼠标。
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleCompact();
            e.Handled = true;
            return;
        }

        NativeMethods.GetCursorPos(out _dragStartCursor);
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragging = RootBorder.CaptureMouse();
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        NativeMethods.GetCursorPos(out var cursor);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Left = _dragStartLeft + (cursor.X - _dragStartCursor.X) / dpi.DpiScaleX;
        Top = _dragStartTop + (cursor.Y - _dragStartCursor.Y) / dpi.DpiScaleY;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) RootBorder.ReleaseMouseCapture();
    }

    private void Root_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void Minutes_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustDuration(Math.Sign(e.Delta), 0);
        e.Handled = true;
    }

    private void Seconds_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustDuration(0, Math.Sign(e.Delta));
        e.Handled = true;
    }

    // ───────────────────────── 键盘（仅在关闭“不抢焦点”时窗口才能获得键盘输入） ─────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        switch (e.Key)
        {
            case Key.Space: ToggleStartPause(); break;
            case Key.R: ResetTimer(); break;
            case Key.Up: AdjustDuration(1, 0); break;
            case Key.Down: AdjustDuration(-1, 0); break;
            case Key.Right: AdjustDuration(0, 1); break;
            case Key.Left: AdjustDuration(0, -1); break;
            case Key.Escape when _compact: ToggleCompact(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ───────────────────────── 按钮 ─────────────────────────

    private void StartPause_Click(object sender, RoutedEventArgs e) => ToggleStartPause();
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetTimer();
    private void MinutesUp_Click(object sender, RoutedEventArgs e) => AdjustDuration(1, 0);
    private void MinutesDown_Click(object sender, RoutedEventArgs e) => AdjustDuration(-1, 0);
    private void SecondsUp_Click(object sender, RoutedEventArgs e) => AdjustDuration(0, 1);
    private void SecondsDown_Click(object sender, RoutedEventArgs e) => AdjustDuration(0, -1);
    private void Compact_Click(object sender, RoutedEventArgs e) => ToggleCompact();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        ApplyWindowSettings();
        _settings.Save();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings.Clone()) { Owner = this, Topmost = true };
        if (dialog.ShowDialog() != true) return;

        AppSettings updated = dialog.Settings;
        _settings.WarningSeconds = updated.WarningSeconds;
        _settings.AllowOvertime = updated.AllowOvertime;
        _settings.FlashOnOvertime = updated.FlashOnOvertime;
        _settings.PlaySound = updated.PlaySound;
        _settings.Opacity = updated.Opacity;
        _settings.AlwaysOnTop = updated.AlwaysOnTop;
        _settings.NoActivate = updated.NoActivate;
        _settings.Normalize();

        ApplyWindowSettings();
        ApplyNoActivate();
        UpdateDisplay();
        _settings.Save();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        new HelpWindow(_hotkeyFailures) { Owner = this, Topmost = true }.ShowDialog();
    }
}
