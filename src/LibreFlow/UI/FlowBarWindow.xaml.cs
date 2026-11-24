using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibreFlow.Native;
using LibreFlow.Services;

namespace LibreFlow.UI;

/// <summary>
/// The small pill at the bottom of the screen. It never takes keyboard focus, so the text
/// box the user is dictating into stays active while they click it.
/// </summary>
public partial class FlowBarWindow : Window
{
    private const int BarCount = 11;
    private const double IdleWidth = 66, RecordingWidth = 118, HandsFreeWidth = 166, BusyWidth = 62;

    private enum Mode { Idle, Recording, Busy }

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _history = new double[BarCount];
    private readonly DispatcherTimer _bubbleTimer = new();
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly DispatcherTimer _placeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IntPtr _handle;
    private int _lastRaiseLogTick;
    private Mode _mode = Mode.Idle;
    private float _level;
    private double _smoothed;
    private TimeSpan _lastShift;
    private bool _autocorrectOn = true;

    public FlowBarWindow()
    {
        InitializeComponent();
        try
        {
            Logo.Source = new BitmapImage(new Uri(AppPaths.Asset("logo-white-96.png")));
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cargar el logo", ex);
        }

        for (var i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle { Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Brushes.White, Margin = new Thickness(1.5, 0, 1.5, 0) };
            Bars.Children.Add(_bars[i]);
        }

        _bubbleTimer.Tick += (_, _) => HideBubble();
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            if (_mode == Mode.Idle && Pill.IsMouseOver)
                ShowBubble("Mantené Ctrl + Win para dictar · doble toque o clic para manos libres", isError: false, seconds: 0);
        };
        _placeTimer.Tick += (_, _) => KeepInPlace();
        Loaded += (_, _) =>
        {
            KeepInPlace();
            _placeTimer.Start();
        };
    }

    public event Action? LogoClicked;
    public event Action? StopClicked;
    public event Action? CancelClicked;
    public event Action? AutocorrectClicked;
    public event Action? DashboardRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        var style = Win32.GetWindowLongPtr(_handle, Win32.GWL_EXSTYLE).ToInt64();
        Win32.SetWindowLongPtr(_handle, Win32.GWL_EXSTYLE, new IntPtr(style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
    }

    /// <summary>
    /// Runs every second. Keeps the bar on the monitor the user is working on, and puts it
    /// back on top when Windows has slipped it under a normal window: the "always on top"
    /// flag stays set when that happens, so nothing else would ever bring it back.
    /// </summary>
    private void KeepInPlace()
    {
        if (_handle == IntPtr.Zero || !IsVisible) return;
        MoveToActiveMonitor();
        if (!IsUnderNormalWindow()) return;

        Win32.SetWindowPos(_handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        var now = Environment.TickCount;
        if (unchecked(now - _lastRaiseLogTick) > 60_000)
        {
            _lastRaiseLogTick = now;
            Log.Info("La barrita había quedado debajo de otra ventana; se volvió a subir");
        }
    }

    private bool IsUnderNormalWindow()
    {
        var above = Win32.GetWindow(_handle, Win32.GW_HWNDPREV);
        for (var i = 0; i < 300 && above != IntPtr.Zero; i++)
        {
            var topmost = (Win32.GetWindowLongPtr(above, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOPMOST) != 0;
            if (!topmost && Win32.IsWindowVisible(above)) return true;
            above = Win32.GetWindow(above, Win32.GW_HWNDPREV);
        }
        return false;
    }

    /// <summary>Bottom centre of the work area of the monitor that holds the active window.</summary>
    private void MoveToActiveMonitor()
    {
        var active = Win32.GetForegroundWindow();
        if (active == IntPtr.Zero || active == _handle) return;
        var monitor = Win32.MonitorFromWindow(active, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return;

        // Monitors can have different scaling; the window is laid out in the target monitor's pixels.
        var scale = Win32.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var x = info.rcWork.Left + (info.rcWork.Right - info.rcWork.Left - width) / 2;
        var y = info.rcWork.Bottom - height;

        if (Win32.GetWindowRect(_handle, out var current) && current.Left == x && current.Top == y) return;
        Win32.SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    // ---------- state (safe to call from any thread) ----------

    public void SetIdle() => OnUi(() => Enter(Mode.Idle, IdleWidth));

    public void SetRecording(bool handsFree) => OnUi(() =>
    {
        KeepInPlace();
        CancelButton.Visibility = StopButton.Visibility = handsFree ? Visibility.Visible : Visibility.Collapsed;
        Enter(Mode.Recording, handsFree ? HandsFreeWidth : RecordingWidth);
    });

    public void SetProcessing() => OnUi(() => Enter(Mode.Busy, BusyWidth));

    /// <summary>Called from the audio thread with the latest loudness (0..1).</summary>
    public void PushLevel(float level) => Volatile.Write(ref _level, level);

    public void ShowMessage(string text, bool isError) => OnUi(() => ShowBubble(text, isError, isError ? 6 : 3));

    public void SetAutocorrect(bool on) => OnUi(() =>
    {
        _autocorrectOn = on;
        AcText.Opacity = on ? 1 : 0.4;
        AcStrike.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    });

    /// <summary>A tiny pulse on "Aa" each time a word is corrected.</summary>
    public void FlashCorrection() => OnUi(() =>
    {
        var pulse = new DoubleAnimation(1.28, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase() };
        AcScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        AcScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    });

    public void SetBarVisible(bool visible) => OnUi(() =>
    {
        if (visible && !IsVisible)
        {
            Show();
            KeepInPlace();
        }
        else if (!visible && IsVisible)
        {
            Hide();
        }
    });

    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Enter(Mode mode, double width)
    {
        var wasRecording = _mode == Mode.Recording;
        _mode = mode;
        IdlePanel.Visibility = mode == Mode.Idle ? Visibility.Visible : Visibility.Collapsed;
        RecPanel.Visibility = mode == Mode.Recording ? Visibility.Visible : Visibility.Collapsed;
        BusyPanel.Visibility = mode == Mode.Busy ? Visibility.Visible : Visibility.Collapsed;
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase() });

        if (mode == Mode.Recording && !wasRecording)
        {
            Array.Clear(_history);
            _smoothed = 0;
            CompositionTarget.Rendering += OnRender;
        }
        else if (mode != Mode.Recording && wasRecording)
        {
            CompositionTarget.Rendering -= OnRender;
        }

        if (mode == Mode.Busy) StartDots();
        else StopDots();
        if (mode != Mode.Idle) HideBubble();
    }

    private void OnRender(object? sender, EventArgs e)
    {
        var raw = Volatile.Read(ref _level);
        // Speech sits around 0.02–0.2 RMS; stretch that into a lively 0..1 range.
        var target = Math.Clamp(Math.Pow(raw * 9, 0.7), 0, 1);
        _smoothed += (target - _smoothed) * (target > _smoothed ? 0.5 : 0.18);

        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now - _lastShift > TimeSpan.FromMilliseconds(45))
        {
            _lastShift = now;
            Array.Copy(_history, 1, _history, 0, BarCount - 1);
            _history[BarCount - 1] = _smoothed;
        }

        for (var i = 0; i < BarCount; i++)
        {
            // Taller in the middle, like the logo.
            var centre = 1 - Math.Abs(i - (BarCount - 1) / 2.0) / ((BarCount - 1) / 2.0);
            var shape = 0.45 + 0.55 * centre;
            _bars[i].Height = 3 + 17 * Math.Max(_history[i], _smoothed * 0.35) * shape;
        }
    }

    private void StartDots()
    {
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
        {
            var animation = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(480))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 160),
            };
            dots[i].BeginAnimation(OpacityProperty, animation);
        }
    }

    private void StopDots()
    {
        foreach (var dot in new[] { Dot1, Dot2, Dot3 }) dot.BeginAnimation(OpacityProperty, null);
    }

    private void ShowBubble(string text, bool isError, double seconds)
    {
        BubbleText.Text = text;
        Bubble.BorderBrush = new SolidColorBrush(isError ? Color.FromArgb(0xB0, 0xF0, 0x55, 0x55) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        _bubbleTimer.Stop();
        if (seconds > 0)
        {
            _bubbleTimer.Interval = TimeSpan.FromSeconds(seconds);
            _bubbleTimer.Start();
        }
    }

    private void HideBubble()
    {
        _bubbleTimer.Stop();
        Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
    }

    // ---------- mouse ----------

    private void OnPillEnter(object sender, MouseEventArgs e)
    {
        if (_mode == Mode.Idle) _hintTimer.Start();
    }

    private void OnPillLeave(object sender, MouseEventArgs e)
    {
        _hintTimer.Stop();
        if (!_bubbleTimer.IsEnabled) HideBubble();
    }

    private void OnLogoClick(object sender, MouseButtonEventArgs e) => LogoClicked?.Invoke();

    private void OnAutocorrectClick(object sender, MouseButtonEventArgs e) => AutocorrectClicked?.Invoke();

    private void OnStopClick(object sender, RoutedEventArgs e) => StopClicked?.Invoke();

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelClicked?.Invoke();

    private void OnPillRightClick(object sender, MouseButtonEventArgs e) => DashboardRequested?.Invoke();
}
