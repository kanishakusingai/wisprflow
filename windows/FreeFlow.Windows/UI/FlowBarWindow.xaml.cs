using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using FreeFlow.Windows.Interop;

namespace FreeFlow.Windows.UI;

public enum FlowBarState
{
    Hidden,
    Starting,
    Recording,
    Transcribing,
    Message,
}

/// <summary>
/// Floating Wispr Flow-style capsule above the taskbar. Mirrors FlowBarView from the macOS
/// version (Sources/RecordingOverlay.swift): same sizes, colors and waveform math.
/// </summary>
public partial class FlowBarWindow : Window
{
    private const int BarCount = 21;
    private const double BarWidth = 2.5;
    private const double BarSpacing = 2.5;
    private const double MinBarHeight = 3;
    private const double MaxBarHeight = 24;
    private const double Center = (BarCount - 1) / 2.0;
    private const double BottomMargin = 12; // gap above the taskbar, in DIPs

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly DateTime _epoch = DateTime.UtcNow;
    private FlowBarState _state = FlowBarState.Hidden;
    private DateTime _recordingStartedUtc;
    private double _audioLevel;
    private double _displayLevel;
    private bool _renderHooked;
    private int _hideGeneration;
    private IntPtr _hwnd;

    public event Action? StopRequested;

    public FlowBarWindow()
    {
        InitializeComponent();
        for (var i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = MinBarHeight,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Fill = Brushes.White,
                Margin = new Thickness(BarSpacing / 2, 0, BarSpacing / 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _bars[i] = bar;
            Bars.Children.Add(bar);
        }
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyWindowStyles(clickThrough: true);
        };
    }

    public FlowBarState State => _state;

    // ---------- Public state changes (call on UI thread) ----------

    public void ShowStarting()
    {
        SetPanels(live: false, init: true, message: false);
        _state = FlowBarState.Starting;
        AnimatePillWidth(96);
        Present(clickThrough: true);
    }

    public void ShowRecording(bool toggleMode, bool commandMode = false)
    {
        _recordingStartedUtc = DateTime.UtcNow;
        _audioLevel = 0;
        _displayLevel = 0;
        _state = FlowBarState.Recording;

        SetPanels(live: true, init: false, message: false);
        LiveDot.Visibility = Visibility.Visible;
        Sparkle.Visibility = Visibility.Collapsed;
        TimerText.Visibility = Visibility.Visible;
        StopButton.Visibility = toggleMode ? Visibility.Visible : Visibility.Collapsed;
        TimerText.Text = "0:00";

        AnimatePillWidth(196 + (toggleMode ? 30 : 0) + (commandMode ? 70 : 0));
        // Only accept clicks when there is a stop button to press.
        Present(clickThrough: !toggleMode);
    }

    public void ShowTranscribing()
    {
        _state = FlowBarState.Transcribing;
        SetPanels(live: true, init: false, message: false);
        LiveDot.Visibility = Visibility.Collapsed;
        Sparkle.Visibility = Visibility.Visible;
        TimerText.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        // Keep current width so the bar doesn't jump.
        Present(clickThrough: true);
    }

    public void ShowMessage(string text, bool isError, TimeSpan duration)
    {
        _state = FlowBarState.Message;
        SetPanels(live: false, init: false, message: true);
        MessageText.Text = text.Length > 90 ? text[..89] + "…" : text;
        MessageIconBg.Fill = new SolidColorBrush(isError ? Color.FromArgb(0xEB, 0xFF, 0x3B, 0x30) : Color.FromRgb(0x30, 0xD1, 0x58));
        MessageIconGlyph.Text = isError ? "!" : "✓";

        var width = Math.Min(440, Math.Max(190, MessageText.Text.Length * 6.8 + 64));
        AnimatePillWidth(width);
        Present(clickThrough: true);

        var generation = ++_hideGeneration;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation == _hideGeneration && _state == FlowBarState.Message) Dismiss();
        };
        timer.Start();
    }

    /// <summary>0..1 microphone loudness. Safe to call from any thread.</summary>
    public void SetAudioLevel(float level) => _audioLevel = Math.Clamp(level, 0f, 1f);

    public void Dismiss()
    {
        if (_state == FlowBarState.Hidden) return;
        _state = FlowBarState.Hidden;
        var generation = ++_hideGeneration;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var slide = new DoubleAnimation(10, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (generation != _hideGeneration) return; // re-shown meanwhile
            Hide();
            StopRendering();
        };
        Root.BeginAnimation(OpacityProperty, fade);
        RootOffset.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // ---------- Internals ----------

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void SetPanels(bool live, bool init, bool message)
    {
        LivePanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        InitPanel.Visibility = init ? Visibility.Visible : Visibility.Collapsed;
        MessagePanel.Visibility = message ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Present(bool clickThrough)
    {
        _hideGeneration++;
        var wasHidden = !IsVisible || Root.Opacity < 0.99;

        if (!IsVisible)
        {
            Root.Opacity = 0;
            Show();
        }
        PositionOnActiveMonitor();
        ApplyWindowStyles(clickThrough);
        StartRendering();

        if (wasHidden)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RootOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
    }

    private void AnimatePillWidth(double width)
    {
        var from = double.IsNaN(PillHost.Width) ? width : PillHost.ActualWidth > 0 ? PillHost.ActualWidth : PillHost.Width;
        PillHost.BeginAnimation(WidthProperty, new DoubleAnimation(from, width, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Bottom-center of the monitor you're working on, just above the taskbar.</summary>
    private void PositionOnActiveMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;

        var foreground = NativeMethods.GetForegroundWindow();
        var screen = foreground != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(foreground)
            : System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var work = screen.WorkingArea; // physical pixels, excludes taskbar

        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = work.Left + work.Width / 2, Y = work.Top + work.Height / 2 },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        double scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        var widthPx = (int)Math.Round(Width * scale);
        var heightPx = (int)Math.Round(Height * scale);
        // The window has ~22 DIPs of transparent padding around the pill.
        var paddingPx = (int)Math.Round(((Height - 40) / 2) * scale);
        var x = work.Left + (work.Width - widthPx) / 2;
        var y = work.Bottom - heightPx + paddingPx - (int)Math.Round(BottomMargin * scale);

        NativeMethods.SetWindowPos(_hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Never steal focus from the app you're dictating into; optionally let clicks pass through.</summary>
    private void ApplyWindowStyles(bool clickThrough)
    {
        if (_hwnd == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        style = clickThrough ? style | NativeMethods.WS_EX_TRANSPARENT : style & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }

    private void StartRendering()
    {
        if (_renderHooked) return;
        CompositionTarget.Rendering += OnRendering;
        _renderHooked = true;
    }

    private void StopRendering()
    {
        if (!_renderHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderHooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _epoch).TotalSeconds;

        // Spring-ish smoothing of the mic level for buttery bars.
        _displayLevel += (_audioLevel - _displayLevel) * 0.35;

        switch (_state)
        {
            case FlowBarState.Recording:
                UpdateLiveBars(t);
                var pulse = 0.5 + 0.5 * Math.Sin(t * 4.0);
                LiveDotHalo.Opacity = 0.35 * pulse;
                var elapsed = (int)(DateTime.UtcNow - _recordingStartedUtc).TotalSeconds;
                var label = $"{elapsed / 60}:{elapsed % 60:00}";
                if (TimerText.Text != label) TimerText.Text = label;
                SetGlow(Math.Min(1.0, _displayLevel * 1.4));
                break;
            case FlowBarState.Transcribing:
                UpdateProcessingBars(t);
                SetGlow(0.35);
                break;
            case FlowBarState.Starting:
                var active = (int)(t / 0.5) % 3;
                Dot0.Opacity = active == 0 ? 0.9 : 0.25;
                Dot1.Opacity = active == 1 ? 0.9 : 0.25;
                Dot2.Opacity = active == 2 ? 0.9 : 0.25;
                SetGlow(0);
                break;
            default:
                SetGlow(0);
                break;
        }
    }

    private void SetGlow(double intensity)
    {
        Glow.Opacity = intensity > 0 ? 0.15 + 0.6 * intensity : 0;
        GlowBlur.Radius = 10 + 6 * intensity;
        GlowScale.ScaleY = 0.75 + 0.25 * intensity;
    }

    private static double Envelope(int index)
    {
        var d = (index - Center) / Center;
        return Math.Exp(-d * d * 2.2);
    }

    private void UpdateLiveBars(double t)
    {
        var level = Math.Min(1.0, _displayLevel * 1.15);
        for (var i = 0; i < BarCount; i++)
        {
            var wobble = 0.55 + 0.25 * Math.Sin(t * 9.0 + i * 1.7) + 0.20 * Math.Sin(t * 5.3 - i * 0.9);
            var speaking = level * Envelope(i) * wobble;
            var idle = 0.05 + 0.04 * (0.5 + 0.5 * Math.Sin(t * 2.4 - i * 0.45));
            var amp = Math.Min(1.0, idle + speaking);
            _bars[i].Height = MinBarHeight + (MaxBarHeight - MinBarHeight) * amp;
            _bars[i].Opacity = 0.55 + 0.45 * Envelope(i);
        }
    }

    private void UpdateProcessingBars(double t)
    {
        const double cycle = 1.3;
        var position = (t % cycle) / cycle * (BarCount + 8) - 4;
        for (var i = 0; i < BarCount; i++)
        {
            var sweep = Math.Max(0, 1 - Math.Abs(i - position) / 4);
            _bars[i].Height = MinBarHeight + (MaxBarHeight - MinBarHeight) * (0.08 + 0.32 * sweep);
            _bars[i].Opacity = 0.3 + 0.7 * sweep;
        }
    }
}
