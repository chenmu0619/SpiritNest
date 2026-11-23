using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SpiritNest.Models;
using SpiritNest.Services;
using SpiritNest.ViewModels;

namespace SpiritNest.Views;

public enum NotchUiState
{
    MinimalLine,
    Notch,
    Panel,
    PanelSearch,
    PanelWeather
}

public partial class NotchHostWindow : Window
{
    private const double NotchW = 136;
    private const double NotchH = 28;
    /// <summary>Home-indicator style bar (not a mini notch).</summary>
    private const double LineW = 128;
    private const double LineH = 5;
    /// <summary>Gap between screen edge and home bar (also part of hover hit area).</summary>
    private const double HomeEdgeGap = LineH;
    /// <summary>Minimal hit box = gap + bar so hovering the gap also expands.</summary>
    private const double HomeHitH = HomeEdgeGap + LineH;
    private const double PanelHIcons = 192;
    /// <summary>One icon row. Two rows fill the icon area; a single row must not stretch into the empty row.</summary>
    private const double IconRowH = 72;
    private const double PanelHSearch = 88;
    private const double PanelHWeather = 176;
    private const int MaxIconColumns = 6;
    /// <summary>Slot width so at most 6 icons fill the icon area.</summary>
    private const double IconSlotW = 64;
    /// <summary>Panel L/R margins (8+8) + circular page buttons (28+28).</summary>
    private const double PanelSideChrome = 72;
    /// <summary>Live panel width = icon area + side chrome.</summary>
    private double _panelW = PanelSideChrome + MaxIconColumns * IconSlotW;
    private double _panelH = PanelHIcons;
    private const double EyeRadius = 9;
    private const double DigitTravel = 3.2;
    /// <summary>Normal notch: bottom radius, capped by the 28px height.</summary>
    private const double NotchCornerRadius = 14;
    /// <summary>Expanded panel bottom radius.</summary>
    private const double PanelCornerRadius = 18;
    private double _cornerRadius = NotchCornerRadius;
    private bool _capsuleClip;
    private static readonly TimeSpan MouseIdleThreshold = TimeSpan.FromMilliseconds(600);
    private static readonly Color HomeBarFill = ThemeService.IslandBlack;

    private readonly SettingsStore _settings;
    private readonly LibraryStore _library;
    private readonly IconService _icons = new();
    private readonly ShellLaunchService _launch = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _expandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _gazePollTimer;
    private readonly DispatcherTimer _saccadeTimer;
    private readonly DispatcherTimer _homeGapPoll;
    private readonly Random _rng = new();

    private NotchUiState _state = NotchUiState.Notch;
    private WeatherSnapshot? _weather;
    private bool _weatherBusy;
    private List<CategoryInfo> _visiblePages = [];
    private List<ItemVm> _pageIcons = [];
    private int _pageIndex;
    private string? _activePageName;
    private bool _pointerInside;
    private bool _hiddenForFullscreen;
    private bool _browserMinimal;
    /// <summary>1 when WPF already scales with the monitor; otherwise monitor DPI / 96.</summary>
    private double _layoutScale = 1;
    private double Host(double design) => design * _layoutScale;

    private void RefreshLayoutScale()
    {
        var extra = ScreenDpi.ExtraScale(this);
        if (Math.Abs(extra - _layoutScale) < 0.01) return;
        _layoutScale = extra;
        Shell.LayoutTransform = extra > 1.01
            ? new ScaleTransform(extra, extra)
            : Transform.Identity;
    }
    private Popup? _enginePopup;
    private string? _selectedEngineId;
    private DateTime _lastMouseMoveUtc = DateTime.MinValue;
    private bool _trackingMouse;
    private POINT _lastCursor;
    private bool _morphing;
    private bool _morphExpanding;
    private bool _morphCollapsedMinimal;
    private double _morphFromW, _morphFromH, _morphToW, _morphToH;
    private double _morphFromR, _morphToR;
    private double _morphCenterX, _morphWorkTop;
    private DateTime _morphAwayUtc;
    private DateTime _morphStartUtc;
    private EventHandler? _morphRenderHandler;
    private TimeSpan MorphDuration => TimeSpan.FromMilliseconds(SnapAnimationMs(_settings.Current.AnimationDurationMs));

    private bool _viewTransitioning;
    private FrameworkElement? _vtFrom;
    private FrameworkElement? _vtTo;
    private double _vtFromW, _vtFromH, _vtToW, _vtToH;
    private DateTime _vtStartUtc;
    private EventHandler? _vtRenderHandler;
    private TimeSpan ViewTransitionDuration => MorphDuration;

    private static int SnapAnimationMs(int ms)
    {
        if (ms < 100) ms = 360;
        var snapped = (int)(Math.Round(ms / 50.0) * 50);
        return (int)Math.Clamp(snapped, 100, 2000);
    }

    public event Action? RequestOpenSettings;
    public event Action? WeatherUpdated;

    public NotchHostWindow(SettingsStore settings, LibraryStore library)
    {
        _settings = settings;
        _library = library;
        InitializeComponent();
        ShellPlaces.RecycleStateChanged += RefreshRecycleIcons;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClock();

        _expandTimer = new DispatcherTimer();
        _expandTimer.Tick += (_, _) =>
        {
            _expandTimer.Stop();
            if (_morphing) return;
            if (_pointerInside && _state is NotchUiState.Notch or NotchUiState.MinimalLine)
                EnterPanel();
        };

        _collapseTimer = new DispatcherTimer();
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_morphing || HoldPanelForPopup || HoldForSearchInput) return;
            if (!_pointerInside && _state is NotchUiState.Panel or NotchUiState.PanelSearch or NotchUiState.PanelWeather)
                CollapseToIdle();
        };

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SubviewLeaveSeconds) };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (_pointerInside || IsMouseOver || HoldPanelForPopup)
                return;
            if (_state is not (NotchUiState.PanelSearch or NotchUiState.PanelWeather))
                return;
            ShowIconsView();
            if (!IsMouseOver && !HoldPanelForPopup && !HoldForSearchInput
                && _state is NotchUiState.Panel or NotchUiState.PanelSearch or NotchUiState.PanelWeather)
            {
                _pointerInside = false;
                _collapseTimer.Stop();
                _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.Current.CollapseDelayMs);
                _collapseTimer.Start();
            }
        };

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkTimer.Interval = TimeSpan.FromSeconds(2.8 + _rng.NextDouble() * 3.5);
            if (_state is NotchUiState.Notch) BlinkOnce();
        };

        // Smooth look-at while cursor moves; idle saccades use a separate slow timer.
        _gazePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _gazePollTimer.Tick += (_, _) => PollCursorGaze();

        // Gap above home bar is real desktop (right-click works); poll cursor for hover there
        _homeGapPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _homeGapPoll.Tick += (_, _) => PollHomeGapHover();

        _saccadeTimer = new DispatcherTimer();
        _saccadeTimer.Tick += (_, _) =>
        {
            ScheduleNextSaccade();
            if (_state is NotchUiState.Notch && !_trackingMouse)
                PerformIdleSaccade();
        };

        Loaded += async (_, _) =>
        {
            ApplyAppearance();
            PlaceOnMonitor();
            CollapseToIdle(animate: false);
            _clockTimer.Start();
            _blinkTimer.Start();
            _gazePollTimer.Start();
            _homeGapPoll.Start();
            ScheduleNextSaccade();
            _saccadeTimer.Start();
            var auto = !string.Equals(_settings.Current.WeatherCitySource, "manual", StringComparison.OrdinalIgnoreCase);
            await RefreshWeatherAsync(relocate: auto || !_settings.Current.HasWeatherPlace).ConfigureAwait(true);
        };

        SearchBox.TextChanged += (_, _) => ResetIdleTimer();
        SearchBox.GotKeyboardFocus += (_, _) => _collapseTimer.Stop();
        SearchBox.LostKeyboardFocus += (_, _) =>
        {
            if (HoldPanelForPopup || IsMouseOver) return;
            if (_state is not (NotchUiState.PanelSearch or NotchUiState.PanelWeather))
                return;
            _pointerInside = false;
            ArmSubviewLeaveTimer();
        };

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) PlaceOnMonitor();
        };
    }

    public void ApplyAppearance()
    {
        var p = ThemeService.Island(_settings.Current);
        ApplyShellChromeForState();

        NotchTemp.Foreground = ThemeService.Brush(p.Text);
        NotchWeather.Foreground = ThemeService.Brush(p.Text);
        HourText.Foreground = Brushes.Black;
        MinuteText.Foreground = Brushes.Black;
        LeftEye.Background = ThemeService.Brush(ThemeService.MilkWhite);
        RightEye.Background = ThemeService.Brush(ThemeService.MilkWhite);
        LeftLid.Background = new SolidColorBrush(ThemeService.IslandBlack);
        RightLid.Background = new SolidColorBrush(ThemeService.IslandBlack);

        PageTitle.Foreground = ThemeService.Brush(p.Text);
        HeaderDateTime.Foreground = ThemeService.Brush(p.TextMuted);
        HeaderWeatherIcon.Foreground = ThemeService.Brush(p.Text);
        HeaderWeatherText.Foreground = ThemeService.Brush(p.Text);
        SearchBox.Foreground = ThemeService.Brush(p.Text);
        SearchBox.CaretBrush = ThemeService.Brush(p.Text);
        WeatherCity.Foreground = ThemeService.Brush(p.TextMuted);
        WeatherRefreshed.Foreground = ThemeService.Brush(p.TextMuted);
        WeatherRefreshIcon.Foreground = ThemeService.Brush(p.Text);
        WeatherNowIcon.Foreground = ThemeService.Brush(p.Text);
        WeatherNow.Foreground = ThemeService.Brush(p.Text);
        WeatherRange.Foreground = ThemeService.Brush(p.TextMuted);
        ApplyFeatureChrome();

        if (_state is NotchUiState.MinimalLine or NotchUiState.Notch)
            CollapseToIdle(animate: false);
    }

    public void Reload()
    {
        BuildIconList();
        RenderPage();
        UpdatePageTitle();
    }

    /// <summary>Idle chrome follows the saved minimal mode, or a maximized browser while that switch is on.</summary>
    private bool IdleIsMinimal => _settings.Current.MinimalNotch || _browserMinimal;

    public void SetBrowserMinimal(bool on)
    {
        if (_browserMinimal == on) return;
        _browserMinimal = on;
        if (_hiddenForFullscreen) return;
        if (on)
        {
            if (_state == NotchUiState.MinimalLine) return;
            CollapseToIdle(animate: true);
            return;
        }
        if (_state == NotchUiState.MinimalLine)
            CollapseToIdle(animate: true);
    }

    public void SetMinimal(bool minimal)
    {
        if (_settings.Current.MinimalNotch != minimal)
        {
            _settings.Current.MinimalNotch = minimal;
            _settings.Save();
        }
        if (_state is NotchUiState.MinimalLine or NotchUiState.Notch)
            CollapseToIdle(animate: false);
    }

    public void HideForFullscreen()
    {
        _hiddenForFullscreen = true;
        Hide();
    }

    public void ShowAfterFullscreen()
    {
        if (!_hiddenForFullscreen) return;
        _hiddenForFullscreen = false;
        Show();
        PlaceOnMonitor();
        CollapseToIdle(animate: false);
    }

    private Rect CurrentWorkArea()
    {
        var cx = Width > 1 ? Left + Width / 2 : (ActualWidth > 1 ? Left + ActualWidth / 2 : double.NaN);
        if (double.IsNaN(cx) || Width < 1 && ActualWidth < 1)
        {
            GetCursorPos(out var c);
            return DisplayPlacement.FromDevicePoint(new Point(c.X, c.Y)).WorkArea;
        }
        return DisplayPlacement.FromDip(new Point(cx, Top + 2)).WorkArea;
    }

    private void PlaceOnMonitor()
    {
        if (_morphing) return; // never fight the morph tick
        var work = CurrentWorkArea();
        var w = Width > 1 ? Width : LineW;
        Left = Math.Round(work.Left + (work.Width - w) / 2.0);
        // Bar sits below the gap; gap itself is not in our HWND (desktop right-click works)
        Top = work.Top + (_state == NotchUiState.MinimalLine ? Host(HomeEdgeGap) : 0);
    }

    private void ApplyShellChromeForState()
    {
        if (_state == NotchUiState.MinimalLine)
        {
            // Window is only the visible capsule — no overlay on the gap (desktop receives clicks)
            MinimalLine.Visibility = Visibility.Collapsed;
            Shell.Background = new SolidColorBrush(HomeBarFill);
            Shell.BorderBrush = Brushes.Transparent;
            Shell.Width = LineW;
            Shell.Height = LineH;
            Width = Host(LineW);
            Height = Host(LineH);
            ApplyShellRoundClip();
        }
        else
        {
            MinimalLine.Visibility = Visibility.Collapsed;
            Shell.Background = new SolidColorBrush(ThemeService.IslandBlack);
            Shell.BorderBrush = ThemeService.Brush(ThemeService.Island(_settings.Current).Border);
            ApplyShellRoundClip();
        }
    }

    /// <summary>
    /// Gap between screen edge and home bar is not covered by our window (so desktop
    /// context menu works). Poll cursor to treat that gap as hover for expand.
    /// </summary>
    private void PollHomeGapHover()
    {
        if (_morphing || _viewTransitioning) return;
        if (_state != NotchUiState.MinimalLine) return;

        var inside = IsCursorInHomeHitZone();
        if (inside)
        {
            if (_pointerInside) return;
            _pointerInside = true;
            _collapseTimer.Stop();
            _expandTimer.Stop();
            _expandTimer.Interval = TimeSpan.FromMilliseconds(_settings.Current.ExpandDelayMs);
            _expandTimer.Start();
        }
        else if (_pointerInside && !IsMouseOver)
        {
            _pointerInside = false;
            _expandTimer.Stop();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private void Host_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var pref = DwmwcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmaWindowCornerPreference, ref pref, sizeof(int));
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(HostWndProc);
            RefreshLayoutScale();
            DpiChanged += (_, _) =>
            {
                RefreshLayoutScale();
                if (_state is NotchUiState.MinimalLine or NotchUiState.Notch)
                    CollapseToIdle(animate: false);
                else
                    SyncPanelSize(applyHost: PanelHostSettled);
            };
        }
        catch { /* ignore */ }
        ApplyWindowShape(redraw: true);
    }

    private int _paintLock;

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_paintLock > 0) return;
        ApplyWindowShape(redraw: !_morphing && !_viewTransitioning);
    }

    /// <summary>
    /// Batch a resize. Do not toggle WM_SETREDRAW: on a layered window that
    /// clears the frame to white for a tick (the transition flash).
    /// </summary>
    private void WithPaintLocked(Action body)
    {
        _paintLock++;
        try
        {
            body();
            UpdateLayout();
        }
        finally
        {
            _paintLock--;
        }
    }

    /// <summary>
    /// Drop the GDI region. CreateRoundRectRgn is a 1-bit mask and stair-steps the
    /// corners. The shell opacity mask is anti-aliased; pixels outside it stay clear.
    /// </summary>
    private void ApplyWindowShape(bool redraw)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowRgn(hwnd, IntPtr.Zero, redraw);
    }

    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private IntPtr HostWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest) return IntPtr.Zero;
        var lp = lParam.ToInt32();
        var px = (short)(lp & 0xFFFF);
        var py = (short)((lp >> 16) & 0xFFFF);
        if (PresentationSource.FromVisual(this) is not HwndSource source) return IntPtr.Zero;
        if (!GetWindowRect(hwnd, out var wr)) return IntPtr.Zero;
        var local = source.CompositionTarget.TransformFromDevice.Transform(
            new Point(px - wr.Left, py - wr.Top));
        if (HitTestShell(local)) return IntPtr.Zero;
        handled = true;
        return new IntPtr(HtTransparent);
    }

    private bool HitTestShell(Point localInWindow)
    {
        Point origin;
        try { origin = Shell.TranslatePoint(new Point(0, 0), this); }
        catch { return true; }
        var x = localInWindow.X - origin.X;
        var y = localInWindow.Y - origin.Y;
        var w = (Shell.ActualWidth > 1 ? Shell.ActualWidth : Shell.Width) * _layoutScale;
        var h = (Shell.ActualHeight > 1 ? Shell.ActualHeight : Shell.Height) * _layoutScale;
        if (w < 1 || h < 1) return true;
        if (x < 0 || y < 0 || x > w || y > h) return false;
        var r = Math.Min(_cornerRadius, Math.Min(w, h) / 2);
        if (r <= 0) return true;
        if (_capsuleClip)
        {
            var cy = h / 2;
            if (x < r && (x - r) * (x - r) + (y - cy) * (y - cy) > r * r) return false;
            if (x > w - r && (x - (w - r)) * (x - (w - r)) + (y - cy) * (y - cy) > r * r) return false;
            return true;
        }
        if (y <= h - r) return true;
        if (x < r && (x - r) * (x - r) + (y - (h - r)) * (y - (h - r)) > r * r) return false;
        if (x > w - r && (x - (w - r)) * (x - (w - r)) + (y - (h - r)) * (y - (h - r)) > r * r) return false;
        return true;
    }

    private const int DwmaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    private void Host_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_morphing) return;
        _pointerInside = true;
        _collapseTimer.Stop();
        _idleTimer.Stop();
        if (_state is NotchUiState.Notch or NotchUiState.MinimalLine)
        {
            _expandTimer.Stop();
            _expandTimer.Interval = TimeSpan.FromMilliseconds(_settings.Current.ExpandDelayMs);
            _expandTimer.Start();
        }
    }

    private void Host_MouseLeave(object sender, MouseEventArgs e)
    {
        // Size/position changes during morph synthesize leave/enter — ignore until settled
        if (_morphing || _viewTransitioning) return;

        _pointerInside = false;
        _trackingMouse = false;
        _expandTimer.Stop();
        if (HoldPanelForPopup)
        {
            _collapseTimer.Stop();
            return;
        }
        if (_state is NotchUiState.PanelSearch or NotchUiState.PanelWeather)
        {
            _collapseTimer.Stop();
            ArmSubviewLeaveTimer();
            return;
        }
        if (_state is NotchUiState.Panel)
        {
            _collapseTimer.Stop();
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.Current.CollapseDelayMs);
            _collapseTimer.Start();
        }
    }

    private bool HoldPanelForPopup => _enginePopup?.IsOpen == true;

    /// <summary>Typing in search must not collapse when the pointer slips off the short panel.</summary>
    private bool HoldForSearchInput =>
        _state == NotchUiState.PanelSearch && SearchBox.IsKeyboardFocusWithin;

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        // Global cursor polling also drives look-at; local move keeps tracking fresh.
        if (_state != NotchUiState.Notch) return;
        _lastMouseMoveUtc = DateTime.UtcNow;
        _trackingMouse = true;
        TrackDigitsToPointer(e.GetPosition(this), animate: false);
    }

    private void CollapseToIdle(bool animate = true)
    {
        if (_morphing && animate) return;
        _idleTimer.Stop();
        _expandTimer.Stop();
        StopViewTransition(finish: false);
        var minimal = IdleIsMinimal;
        _state = minimal ? NotchUiState.MinimalLine : NotchUiState.Notch;

        IconsView.Visibility = Visibility.Visible;
        IconsView.Opacity = 1;
        SearchView.Visibility = Visibility.Collapsed;
        SearchView.Opacity = 1;
        WeatherView.Visibility = Visibility.Collapsed;
        WeatherView.Opacity = 1;

        // Morph to visual idle size (minimal = bar only; gap hover via PollHomeGapHover)
        var tw = minimal ? LineW : NotchW;
        var th = minimal ? LineH : NotchH;
        animate = animate && _settings.Current.EnablePanelAnimation;

        if (!animate)
        {
            StopMorphRender();
            var work = CurrentWorkArea();
            WithPaintLocked(() =>
            {
                SnapCollapsedChrome(minimal);
                Shell.Width = tw;
                Shell.Height = th;
                Width = Host(tw);
                Height = Host(th);
                Left = Math.Round(work.Left + (work.Width - Host(tw)) / 2.0);
                Top = work.Top + (minimal ? Host(HomeEdgeGap) : 0);
                ApplyShellRoundClip();
            });
            TickClock();
            return;
        }

        MinimalLine.Visibility = Visibility.Collapsed;
        NotchView.Visibility = Visibility.Collapsed;
        PanelView.Visibility = Visibility.Visible;
        PanelView.Opacity = 1;
        StartBoundsMorph(toW: tw, toH: th, expanding: false, collapsedMinimal: minimal);
        TickClock();
    }

    private void EnterPanel()
    {
        if (_morphing) return;
        _state = NotchUiState.Panel;
        ShowIconsView();
        Reload();
        TickClock();

        SyncPanelSize(applyHost: false);

        if (!_settings.Current.EnablePanelAnimation)
        {
            StopMorphRender();
            var work = CurrentWorkArea();
            WithPaintLocked(() =>
            {
                MinimalLine.Visibility = Visibility.Collapsed;
                NotchView.Visibility = Visibility.Collapsed;
                Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
                Shell.VerticalAlignment = VerticalAlignment.Stretch;
                Shell.Width = _panelW;
                Shell.Height = _panelH;
                Width = Host(_panelW);
                Height = Host(_panelH);
                Left = Math.Round(work.Left + (work.Width - Host(_panelW)) / 2.0);
                Top = work.Top;
                ApplyShellChromeForState();
                PanelView.Visibility = Visibility.Visible;
                PanelView.Opacity = 1;
                UpdateHeaderChrome();
            });
            _pointerInside = true;
            _collapseTimer.Stop();
            return;
        }

        // PanelView shown after morph frame 0 clips — avoids full-width content flash
        PanelView.Visibility = Visibility.Collapsed;
        StartBoundsMorph(toW: _panelW, toH: _panelH, expanding: true, collapsedMinimal: false);
    }

    private void SnapCollapsedChrome(bool minimal)
    {
        PanelView.Visibility = Visibility.Collapsed;
        PanelView.Opacity = 1;
        NotchView.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        NotchView.Opacity = 1;
        ApplyShellChromeForState();
    }

    private void ShowIconsView()
    {
        _idleTimer.Stop();
        TransitionToView(NotchUiState.Panel, onReady: UpdatePageTitle);
    }

    private void ShowSearchView()
    {
        if (!SearchAvailable) return;
        TransitionToView(NotchUiState.PanelSearch, onReady: () =>
        {
            PageTitle.Text = "搜索";
            RefreshEngineUi();
            SearchBox.Focus();
            _idleTimer.Stop();
        });
    }

    private void ShowWeatherView()
    {
        if (!WeatherAvailable) return;
        TransitionToView(NotchUiState.PanelWeather, onReady: () =>
        {
            PageTitle.Text = "天气";
            BindWeatherDetail();
            _idleTimer.Stop();
        });
    }

    private FrameworkElement ContentView(NotchUiState state) => state switch
    {
        NotchUiState.PanelSearch => SearchView,
        NotchUiState.PanelWeather => WeatherView,
        _ => IconsView
    };

    /// <summary>Switch icons ↔ search ↔ weather with size + crossfade.</summary>
    private void TransitionToView(NotchUiState next, Action? onReady = null)
    {
        if (next is not (NotchUiState.Panel or NotchUiState.PanelSearch or NotchUiState.PanelWeather))
            next = NotchUiState.Panel;

        var prev = _state is (NotchUiState.Panel or NotchUiState.PanelSearch or NotchUiState.PanelWeather)
            ? _state
            : NotchUiState.Panel;

        if (prev == next && ContentView(next).IsVisible && ContentView(next).Opacity > 0.9)
        {
            onReady?.Invoke();
            return;
        }

        // During expand morph, snap instantly
        if (!PanelHostSettled || _morphing)
        {
            StopViewTransition(finish: false);
            ApplyViewInstant(next, onReady);
            return;
        }

        if (_viewTransitioning)
            StopViewTransition(finish: false);

        var fromEl = ContentView(prev);
        var toEl = ContentView(next);
        _state = next;
        UpdateHeaderChrome();
        onReady?.Invoke();

        var toW = ResolvePanelWidth(_pageIcons.Count);
        var toH = ResolvePanelHeight();
        var fromW = Shell.Width > 1 ? Shell.Width : _panelW;
        var fromH = Shell.Height > 1 ? Shell.Height : _panelH;

        // Prepare both layers for crossfade; collapse the unused third view
        foreach (var v in new FrameworkElement[] { IconsView, SearchView, WeatherView })
        {
            if (ReferenceEquals(v, fromEl) || ReferenceEquals(v, toEl))
            {
                v.Visibility = Visibility.Visible;
            }
            else
            {
                v.Visibility = Visibility.Collapsed;
                v.Opacity = 1;
            }
        }
        fromEl.Opacity = 1;
        toEl.Opacity = 0;

        PanelRoot.Width = Math.Max(fromW, toW);
        PanelRoot.Height = Math.Max(fromH, toH);
        var hostW = Math.Max(fromW, toW);
        var hostH = Math.Max(fromH, toH);
        // Lock the window once. Shell shrinks inside; the region follows it.
        // Recentering Left every frame is the drift.
        Shell.HorizontalAlignment = HorizontalAlignment.Center;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        Shell.Width = fromW;
        Shell.Height = fromH;
        Width = Host(hostW);
        Height = Host(hostH);

        var work = CurrentWorkArea();
        Left = Math.Round(work.Left + (work.Width - Host(hostW)) / 2);
        Top = work.Top;

        _viewTransitioning = true;
        _vtFrom = fromEl;
        _vtTo = toEl;
        _vtFromW = fromW;
        _vtFromH = fromH;
        _vtToW = toW;
        _vtToH = toH;
        _vtStartUtc = DateTime.UtcNow;
        _vtRenderHandler = (_, _) => ViewTransitionTick();
        CompositionTarget.Rendering += _vtRenderHandler;
    }

    private void ApplyViewInstant(NotchUiState next, Action? onReady = null)
    {
        _state = next;
        IconsView.Visibility = next == NotchUiState.Panel ? Visibility.Visible : Visibility.Collapsed;
        SearchView.Visibility = next == NotchUiState.PanelSearch ? Visibility.Visible : Visibility.Collapsed;
        WeatherView.Visibility = next == NotchUiState.PanelWeather ? Visibility.Visible : Visibility.Collapsed;
        IconsView.Opacity = 1;
        SearchView.Opacity = 1;
        WeatherView.Opacity = 1;
        UpdateHeaderChrome();
        onReady?.Invoke();
        SyncPanelSize(applyHost: PanelHostSettled);
    }

    private void ViewTransitionTick()
    {
        var raw = (DateTime.UtcNow - _vtStartUtc).TotalSeconds / ViewTransitionDuration.TotalSeconds;
        var done = raw >= 1;
        var t = CruiseEase(done ? 1.0 : raw);

        Shell.Width = _vtFromW + (_vtToW - _vtFromW) * t;
        Shell.Height = _vtFromH + (_vtToH - _vtFromH) * t;
        if (_vtFrom is not null) _vtFrom.Opacity = 1 - t;
        if (_vtTo is not null) _vtTo.Opacity = t;
        ApplyShellRoundClip();

        if (!done) return;
        FinishViewTransition();
    }

    private void FinishViewTransition()
    {
        StopViewTransition(finish: true);
    }

    private void StopViewTransition(bool finish = true)
    {
        if (_vtRenderHandler is not null)
        {
            CompositionTarget.Rendering -= _vtRenderHandler;
            _vtRenderHandler = null;
        }

        if (finish && _viewTransitioning)
        {
            if (_vtFrom is not null)
            {
                _vtFrom.Visibility = Visibility.Collapsed;
                _vtFrom.Opacity = 1;
            }
            if (_vtTo is not null)
            {
                _vtTo.Visibility = Visibility.Visible;
                _vtTo.Opacity = 1;
            }

            _panelW = _vtToW;
            _panelH = _vtToH;
            PanelRoot.Width = _panelW;
            PanelRoot.Height = _panelH;
            Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
            Shell.VerticalAlignment = VerticalAlignment.Stretch;
            Shell.Width = _panelW;
            Shell.Height = _panelH;
            Width = Host(_panelW);
            Height = Host(_panelH);
            PlaceOnMonitor();
            ApplyShellRoundClip();
        }
        else
        {
            Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
            Shell.VerticalAlignment = VerticalAlignment.Stretch;
        }

        _viewTransitioning = false;
        _vtFrom = null;
        _vtTo = null;
    }

    private bool SearchAvailable =>
        _settings.Current.SearchEngines.Any(e => e.Enabled);

    private bool WeatherAvailable => _settings.Current.ShowWeather;

    private void ApplyFeatureChrome()
    {
        var weather = WeatherAvailable ? Visibility.Visible : Visibility.Collapsed;
        NotchTemp.Visibility = weather;
        NotchWeather.Visibility = weather;
        if (!SearchAvailable && _state == NotchUiState.PanelSearch)
            ShowIconsView();
        else if (!WeatherAvailable && _state == NotchUiState.PanelWeather)
            ShowIconsView();
        else
            UpdateHeaderChrome();
        if (_state == NotchUiState.PanelSearch)
            RefreshEngineUi();
    }

    private void UpdateHeaderChrome()
    {
        var sub = _state is NotchUiState.PanelSearch or NotchUiState.PanelWeather;
        HeaderBackBtn.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
        HeaderSearchBtn.Visibility = _state == NotchUiState.PanelSearch || !SearchAvailable
            ? Visibility.Collapsed
            : Visibility.Visible;
        HeaderWeatherBtn.Visibility = _state == NotchUiState.PanelWeather || !WeatherAvailable
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private const int SubviewLeaveSeconds = 10;

    /// <summary>Activity while the pointer is outside restarts the 10s return. Inside, the panel stays.</summary>
    private void ResetIdleTimer()
    {
        if (_pointerInside || HoldPanelForPopup)
        {
            _idleTimer.Stop();
            return;
        }
        ArmSubviewLeaveTimer();
    }

    /// <summary>Pointer left the search or weather panel. Return after 10s with no further action.</summary>
    private void ArmSubviewLeaveTimer()
    {
        if (_state is not (NotchUiState.PanelSearch or NotchUiState.PanelWeather)) return;
        if (_pointerInside || HoldPanelForPopup) return;
        _idleTimer.Stop();
        _idleTimer.Interval = TimeSpan.FromSeconds(SubviewLeaveSeconds);
        _idleTimer.Start();
    }

    private void ResizeHost(double w, double h, bool animate)
    {
        StopMorphRender();
        _morphing = false;
        Shell.Width = w;
        Shell.Height = h;
        Width = Host(w);
        Height = Host(h);
        var work = CurrentWorkArea();
        Left = Math.Round(work.Left + (work.Width - Host(w)) / 2.0);
        Top = work.Top + (_state == NotchUiState.MinimalLine ? Host(HomeEdgeGap) : 0);
        ApplyShellRoundClip();
    }

    private void Shell_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyShellRoundClip();

    /// <summary>
    /// Flat-top + rounded-bottom mask for notch/panel. Geometry clips are aliased;
    /// a Border opacity mask keeps the curve smooth. During morph always flat-top
    /// (the capsule path let full panel content flash unclipped).
    /// </summary>
    private void ApplyShellRoundClip()
    {
        var w = Shell.Width > 1 ? Shell.Width : Shell.ActualWidth;
        var h = Shell.Height > 1 ? Shell.Height : Shell.ActualHeight;
        if (w < 1 || h < 1)
        {
            Shell.Clip = null;
            Shell.OpacityMask = null;
            return;
        }

        double br;
        // Capsule only for the thin home bar. Using it on the full panel lets
        // content draw outside the pill. The bar's own ends must be semicircles.
        var homeBar = h <= LineH + 0.75
            && (_state == NotchUiState.MinimalLine
                || _morphCollapsedMinimal
                || (_morphing && _morphFromH <= LineH + 0.5));
        if (homeBar)
        {
            _capsuleClip = true;
            br = Math.Min(w, h) / 2;
        }
        else if (_morphing)
        {
            _capsuleClip = false;
            br = Math.Min(_cornerRadius, Math.Min(w, h) / 2);
        }
        else
        {
            _capsuleClip = false;
            var want = _state == NotchUiState.Notch ? NotchCornerRadius : PanelCornerRadius;
            br = Math.Min(want, Math.Min(w, h) / 2);
        }
        _cornerRadius = br;

        // Paint the silhouette as a geometry, not a clip or region. Those are 1-bit
        // and stair-step; a filled geometry is coverage-antialiased against the desktop.
        Shell.Clip = null;
        Shell.ClipToBounds = false;
        Shell.CornerRadius = new CornerRadius(0);
        Shell.OpacityMask = null;
        var geo = BuildShellGeometry(w, h, br, _capsuleClip);
        geo.Freeze();
        var drawing = new GeometryDrawing(new SolidColorBrush(ThemeService.IslandBlack), null, geo);
        drawing.Freeze();
        Shell.Background = new DrawingBrush(drawing)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Viewbox = new Rect(0, 0, w, h),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, w, h),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Geometry BuildShellGeometry(double w, double h, double br, bool capsule)
    {
        if (capsule)
        {
            // ArcTo between the two ends of a diameter collapses to a straight
            // line, which made the home bar a rectangle. Union two circles
            // with the middle instead.
            var d = Math.Min(h, w);
            Geometry geo = w <= h + 0.01
                ? new EllipseGeometry(new Point(w / 2, h / 2), w / 2, h / 2)
                : new CombinedGeometry(
                    GeometryCombineMode.Union,
                    new CombinedGeometry(
                        GeometryCombineMode.Union,
                        new RectangleGeometry(new Rect(d / 2, 0, Math.Max(0, w - d), h)),
                        new EllipseGeometry(new Rect(0, 0, d, h))),
                    new EllipseGeometry(new Rect(w - d, 0, d, h)));
            geo.Freeze();
            return geo;
        }

        var geoStream = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geoStream.Open())
        {
            var rx = Math.Min(br, w / 2);
            var ry = Math.Min(br, h / 2);
            ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(w, 0), true, true);
            ctx.LineTo(new Point(w, Math.Max(0, h - ry)), true, true);
            ctx.ArcTo(new Point(w - rx, h), new Size(rx, ry), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(rx, h), true, true);
            ctx.ArcTo(new Point(0, Math.Max(0, h - ry)), new Size(rx, ry), 0, false, SweepDirection.Clockwise, true, true);
        }

        geoStream.Freeze();
        return geoStream;
    }

    private void StopMorphRender()
    {
        if (_morphRenderHandler is null) return;
        CompositionTarget.Rendering -= _morphRenderHandler;
        _morphRenderHandler = null;
    }

    /// <summary>
    /// Fixed host (final bounds, centered once). Only Shell W/H animate inside —
    /// window never moves during the transition (eliminates drift / leave-enter loops).
    /// </summary>
    private void StartBoundsMorph(double toW, double toH, bool expanding, bool collapsedMinimal)
    {
        StopMorphRender();
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Shell.BeginAnimation(WidthProperty, null);
        Shell.BeginAnimation(HeightProperty, null);
        _collapseTimer.Stop();
        _expandTimer.Stop();

        var work = CurrentWorkArea();
        _morphWorkTop = work.Top;
        _morphCenterX = work.Left + work.Width / 2.0;

        double fromW, fromH;
        if (expanding)
        {
            fromW = IdleIsMinimal ? LineW : NotchW;
            fromH = IdleIsMinimal ? LineH : NotchH;
        }
        else
        {
            // Collapse from the size on screen. Max(shell, panel) jumped a
            // half-open island to full size before it could shrink.
            fromW = Shell.Width > 1 ? Shell.Width : _panelW;
            fromH = Shell.Height > 1 ? Shell.Height : _panelH;
            toW = collapsedMinimal ? LineW : NotchW;
            toH = collapsedMinimal ? LineH : NotchH;
        }

        _morphing = true;
        _morphExpanding = expanding;
        _morphCollapsedMinimal = collapsedMinimal;
        _morphFromW = fromW;
        _morphFromH = fromH;
        _morphToW = toW;
        _morphToH = toH;
        _morphFromR = expanding
            ? (IdleIsMinimal ? LineH / 2 : NotchCornerRadius)
            : _cornerRadius;
        _morphToR = expanding
            ? PanelCornerRadius
            : (collapsedMinimal ? LineH / 2 : NotchCornerRadius);
        _cornerRadius = _morphFromR;
        _morphAwayUtc = DateTime.MinValue;
        var needW = Math.Max(fromW, toW);
        var needH = Math.Max(fromH, toH);
        var curW = Width > 1 ? Width / _layoutScale : needW;
        var curH = Height > 1 ? Height / _layoutScale : needH;
        // Never shrink the layered HWND mid-gesture. A half-open collapse used
        // to resize the window down to the shell and flashed white.
        var hostW = Host(Math.Max(needW, curW));
        var hostH = Host(Math.Max(needH, curH));
        var left = Math.Round(_morphCenterX - hostW / 2.0);
        var top = _morphWorkTop;
        var resizeHost = Math.Abs((Width > 1 ? Width : hostW) - hostW) > 0.5
            || Math.Abs((Height > 1 ? Height : hostH) - hostH) > 0.5
            || Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5;
        WithPaintLocked(() =>
        {
            MinimalLine.Visibility = Visibility.Collapsed;
            NotchView.Visibility = Visibility.Collapsed;
            Shell.HorizontalAlignment = HorizontalAlignment.Center;
            Shell.VerticalAlignment = VerticalAlignment.Top;
            Shell.Width = fromW;
            Shell.Height = fromH;
            if (resizeHost)
            {
                Width = hostW;
                Height = hostH;
                Left = left;
                Top = top;
            }
            ApplyShellRoundClip();
            if (PanelView.Visibility != Visibility.Visible)
            {
                PanelView.Visibility = Visibility.Visible;
                PanelView.Opacity = 1;
            }
        });

        _morphStartUtc = DateTime.UtcNow;
        _morphRenderHandler = (_, _) => MorphRenderTick();
        CompositionTarget.Rendering += _morphRenderHandler;
    }

    private void ApplyMorphFrame(double t)
    {
        var w = _morphFromW + (_morphToW - _morphFromW) * t;
        var h = _morphFromH + (_morphToH - _morphFromH) * t;
        _cornerRadius = _morphFromR + (_morphToR - _morphFromR) * t;
        Shell.Width = w;
        Shell.Height = h;
        ApplyShellRoundClip();
    }

    /// <summary>
    /// Speed 0 → cruise → 0. Accelerate for the first 30%, hold, then brake for the last 30%.
    /// </summary>
    private static double CruiseEase(double t)
    {
        const double accel = 0.3;
        t = Math.Clamp(t, 0, 1);
        var v = 1.0 / (1.0 - accel);
        if (t < accel)
            return v * t * t / (2 * accel);
        if (t <= 1 - accel)
            return v * accel / 2 + v * (t - accel);
        var u = t - (1 - accel);
        var beforeBrake = v * accel / 2 + v * (1 - 2 * accel);
        return beforeBrake + v * (u - u * u / (2 * accel));
    }

    private bool CursorOverShell()
    {
        if (!GetCursorPos(out var p)) return false;
        var src = PresentationSource.FromVisual(this);
        var pt = src?.CompositionTarget is null
            ? new Point(p.X, p.Y)
            : src.CompositionTarget.TransformFromDevice.Transform(new Point(p.X, p.Y));

        var shellW = Shell.Width > 1 ? Shell.Width : Shell.ActualWidth;
        var shellH = Shell.Height > 1 ? Shell.Height : Shell.ActualHeight;
        var hostW = ActualWidth > 1 ? ActualWidth : Width;
        var hostH = ActualHeight > 1 ? ActualHeight : Height;
        double x, y, w, h;
        if (Shell.HorizontalAlignment == HorizontalAlignment.Stretch)
        {
            x = Left;
            y = Top;
            w = hostW;
            h = hostH;
        }
        else
        {
            w = Host(shellW);
            h = Host(shellH);
            x = Left + Math.Max(0, (hostW - w) / 2);
            y = Top;
        }
        return pt.X >= x && pt.X < x + w && pt.Y >= y && pt.Y < y + h;
    }

    private void MorphRenderTick()
    {
        if (_morphExpanding && !CursorOverShell())
        {
            if (_morphAwayUtc == DateTime.MinValue)
                _morphAwayUtc = DateTime.UtcNow;
            else if ((DateTime.UtcNow - _morphAwayUtc).TotalMilliseconds
                     >= Math.Max(0, _settings.Current.CollapseDelayMs))
            {
                StopMorphRender();
                _morphing = false;
                _morphAwayUtc = DateTime.MinValue;
                _pointerInside = false;
                CollapseToIdle(animate: true);
                return;
            }
        }
        else
        {
            _morphAwayUtc = DateTime.MinValue;
        }

        var raw = (DateTime.UtcNow - _morphStartUtc).TotalSeconds / MorphDuration.TotalSeconds;
        var done = raw >= 1;
        var t = CruiseEase(done ? 1.0 : raw);
        ApplyMorphFrame(t);
        if (!done) return;

        StopMorphRender();
        ApplyMorphFrame(1);

        if (_morphExpanding)
        {
            _state = NotchUiState.Panel;
            WithPaintLocked(() =>
            {
                Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
                Shell.VerticalAlignment = VerticalAlignment.Stretch;
                Shell.Width = _panelW;
                Shell.Height = _panelH;
                Width = Host(_panelW);
                Height = Host(_panelH);
                Left = Math.Round(_morphCenterX - Host(_panelW) / 2.0);
                Top = _morphWorkTop;
                ApplyShellChromeForState();
                PanelView.Visibility = Visibility.Visible;
                NotchView.Visibility = Visibility.Collapsed;
                MinimalLine.Visibility = Visibility.Collapsed;
                UpdateHeaderChrome();
                _morphing = false;
            });
            // The host is larger than the shell while it grows, so IsMouseOver
            // lies. Use the shell rectangle. Left during the expand → collapse.
            if (!CursorOverShell() && !HoldPanelForPopup && !HoldForSearchInput)
            {
                _pointerInside = false;
                CollapseToIdle(animate: true);
                return;
            }
            _pointerInside = true;
            _collapseTimer.Stop();
            return;
        }

        var endW = _morphToW;
        var endH = _morphToH;
        WithPaintLocked(() =>
        {
            if (_morphCollapsedMinimal)
            {
                Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
                Shell.VerticalAlignment = VerticalAlignment.Stretch;
                Width = Host(endW);
                Height = Host(endH);
                Left = Math.Round(_morphCenterX - Host(endW) / 2.0);
                Top = _morphWorkTop + Host(HomeEdgeGap);
            }
            else
            {
                // Leave the panel-sized HWND in place. Shrinking it flashes.
                Shell.HorizontalAlignment = HorizontalAlignment.Center;
                Shell.VerticalAlignment = VerticalAlignment.Top;
            }
            Shell.Width = endW;
            Shell.Height = endH;
            _morphing = false;
            SnapCollapsedChrome(_morphCollapsedMinimal);
        });
        // Re-sync with bar / gap (gap is polled; bar uses IsMouseOver)
        _pointerInside = IsMouseOver || IsCursorInHomeHitZone();
        if (_pointerInside && _state is NotchUiState.MinimalLine or NotchUiState.Notch)
        {
            _expandTimer.Stop();
            _expandTimer.Interval = TimeSpan.FromMilliseconds(_settings.Current.ExpandDelayMs);
            _expandTimer.Start();
        }
    }

    private bool IsCursorInHomeHitZone()
    {
        if (_state != NotchUiState.MinimalLine) return false;
        GetCursorPos(out var c);
        var mon = DisplayPlacement.FromDevicePoint(new Point(c.X, c.Y));
        var cursor = ScreenDpi.DeviceToDip(new Point(c.X, c.Y), mon.DpiScale);
        var hit = new Rect(Left, mon.WorkArea.Top, Math.Max(Width, Host(LineW)), Host(HomeHitH));
        return cursor.X >= hit.Left && cursor.X < hit.Right && cursor.Y >= hit.Top && cursor.Y < hit.Bottom;
    }

    private void TickClock()
    {
        var now = DateTime.Now;
        HourText.Text = now.ToString("HH");
        MinuteText.Text = now.ToString("mm");
        var week = "日一二三四五六"[(int)now.DayOfWeek];
        HeaderDateTime.Text = $"周{week} {now:yyyy-MM-dd HH:mm}";
    }

    private void PollCursorGaze()
    {
        if (_state != NotchUiState.Notch) return;

        GetCursorPos(out var cur);
        var moved = Math.Abs(cur.X - _lastCursor.X) + Math.Abs(cur.Y - _lastCursor.Y) >= 2;
        if (moved)
        {
            _lastCursor = cur;
            _lastMouseMoveUtc = DateTime.UtcNow;
            _trackingMouse = true;
            try
            {
                var local = PointFromScreen(new Point(cur.X, cur.Y));
                TrackDigitsToPointer(local, animate: false);
            }
            catch { /* ignore */ }
            return;
        }

        if (_trackingMouse && DateTime.UtcNow - _lastMouseMoveUtc >= MouseIdleThreshold)
            _trackingMouse = false;
    }

    /// <summary>Irregular pause between idle eye saccades (~1.6–5.5s), never rapid.</summary>
    private void ScheduleNextSaccade()
    {
        // Bias toward slower looks; occasional quicker glance still ≥1.6s
        var sec = 1.6 + _rng.NextDouble() * 2.4;
        if (_rng.NextDouble() < 0.35)
            sec += 1.0 + _rng.NextDouble() * 1.5;
        _saccadeTimer.Interval = TimeSpan.FromSeconds(sec);
    }

    private void PerformIdleSaccade()
    {
        // Small gaze shift + soft ease — like a quiet glance, not jitter
        var angle = _rng.NextDouble() * Math.PI * 2;
        var dist = DigitTravel * (0.35 + _rng.NextDouble() * 0.65);
        var dx = Math.Cos(angle) * dist;
        var dy = Math.Sin(angle) * dist * 0.85;
        // Slight vergence difference between eyes
        AnimateDigitOffset(HourTx, dx, dy);
        AnimateDigitOffset(MinuteTx, dx * 0.92 + (_rng.NextDouble() - 0.5) * 0.4, dy * 0.92);
    }

    private void TrackDigitsToPointer(Point windowPos, bool animate)
    {
        ApplyLookAt(LeftEye, HourTx, windowPos, animate);
        ApplyLookAt(RightEye, MinuteTx, windowPos, animate);
    }

    private void ApplyLookAt(FrameworkElement eye, TranslateTransform tx, Point windowPos, bool animate)
    {
        var center = eye.TranslatePoint(new Point(eye.ActualWidth / 2, eye.ActualHeight / 2), this);
        var vx = windowPos.X - center.X;
        var vy = windowPos.Y - center.Y;
        var len = Math.Sqrt(vx * vx + vy * vy);
        double dx = 0, dy = 0;
        if (len >= 0.01)
        {
            var scale = Math.Min(DigitTravel, len * 0.12) / len;
            dx = vx * scale;
            dy = vy * scale;
        }
        ClampOffset(ref dx, ref dy);
        if (animate) AnimateDigitOffset(tx, dx, dy);
        else
        {
            tx.BeginAnimation(TranslateTransform.XProperty, null);
            tx.BeginAnimation(TranslateTransform.YProperty, null);
            tx.X = dx;
            tx.Y = dy;
        }
    }

    private void AnimateDigitOffset(TranslateTransform tx, double dx, double dy)
    {
        ClampOffset(ref dx, ref dy);
        var ms = 180 + _rng.Next(0, 140); // soft saccade, not snap
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var ax = new DoubleAnimation(tx.X, dx, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        var ay = new DoubleAnimation(tx.Y, dy, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        ax.Completed += (_, _) => tx.X = dx;
        ay.Completed += (_, _) => tx.Y = dy;
        tx.BeginAnimation(TranslateTransform.XProperty, ax);
        tx.BeginAnimation(TranslateTransform.YProperty, ay);
    }

    private static void ClampOffset(ref double dx, ref double dy)
    {
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len > DigitTravel && len > 0.01)
        {
            dx = dx / len * DigitTravel;
            dy = dy / len * DigitTravel;
        }
    }

    private void BlinkOnce()
    {
        var anim = new DoubleAnimation(0, EyeRadius * 2, TimeSpan.FromMilliseconds(70))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase()
        };
        LeftLid.BeginAnimation(HeightProperty, anim);
        RightLid.BeginAnimation(HeightProperty, anim);
    }

    private async void WeatherRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_weatherBusy) return;
        SetIconSpin(WeatherRefreshIcon, true);
        try
        {
            var auto = !string.Equals(_settings.Current.WeatherCitySource, "manual", StringComparison.OrdinalIgnoreCase);
            await RefreshWeatherAsync(relocate: auto, forceForecast: true).ConfigureAwait(true);
        }
        finally
        {
            SetIconSpin(WeatherRefreshIcon, false);
        }
    }

    private static void SetIconSpin(TextBlock icon, bool spinning)
    {
        if (icon.RenderTransform is not RotateTransform spin) return;
        if (!spinning)
        {
            spin.BeginAnimation(RotateTransform.AngleProperty, null);
            spin.Angle = 0;
            return;
        }

        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    public async Task RefreshWeatherAsync(bool relocate = false, bool forceForecast = false)
    {
        if (_weatherBusy) return;
        _weatherBusy = true;
        try
        {
            var auto = !string.Equals(_settings.Current.WeatherCitySource, "manual", StringComparison.OrdinalIgnoreCase);
            WeatherPlace place;
            if (!auto)
            {
                place = ResolveManualPlace();
                if (place.Latitude == 0 && place.Longitude == 0)
                {
                    ShowNamedPlace();
                    return;
                }
            }
            else
            {
                place = await WeatherService.LocateAsync(_settings.Current.AmapKey, force: relocate).ConfigureAwait(true);
                RememberPlace(place);
                _settings.Save();
            }

            _weather = await WeatherService.ForecastAsync(place, force: relocate || forceForecast).ConfigureAwait(true);
            ApplyWeatherUi();
            WeatherUpdated?.Invoke();
        }
        catch
        {
            NotchTemp.Text = "--°";
            NotchWeather.Text = WeatherService.FallbackGlyph;
            HeaderWeatherIcon.Text = WeatherService.FallbackGlyph;
            HeaderWeatherText.Text = " --°";
        }
        finally
        {
            _weatherBusy = false;
        }
    }

    private void RememberPlace(WeatherPlace place)
    {
        _settings.Current.WeatherProvince = place.Province;
        _settings.Current.WeatherCity = place.City;
        _settings.Current.WeatherDistrict = place.District;
        _settings.Current.WeatherLatitude = place.Latitude;
        _settings.Current.WeatherLongitude = place.Longitude;
    }

    private void ShowNamedPlace()
    {
        _weather = null;
        var leaf = !string.IsNullOrWhiteSpace(_settings.Current.WeatherDistrict)
            ? _settings.Current.WeatherDistrict
            : !string.IsNullOrWhiteSpace(_settings.Current.WeatherCity)
                ? _settings.Current.WeatherCity
                : _settings.Current.WeatherProvince;
        NotchTemp.Text = "--°";
        NotchWeather.Text = WeatherService.FallbackGlyph;
        HeaderWeatherIcon.Text = WeatherService.FallbackGlyph;
        HeaderWeatherText.Text = " --°";
        WeatherCity.Text = leaf;
        WeatherRefreshed.Text = "";
        WeatherNowIcon.Text = WeatherService.FallbackGlyph;
        WeatherNow.Text = "--°";
        WeatherRange.Text = "";
        ForecastList.ItemsSource = null;
    }

    private WeatherPlace ResolveManualPlace()
    {
        var place = SavedPlace();
        if (place.Latitude != 0 || place.Longitude != 0) return place;
        if (!AreaCatalog.TryGetCoordinates(place.Province, place.City, place.District, out var latitude, out var longitude))
            return place;
        place = new WeatherPlace
        {
            Province = place.Province,
            City = place.City,
            District = place.District,
            Latitude = latitude,
            Longitude = longitude
        };
        RememberPlace(place);
        _settings.Save();
        return place;
    }

    private WeatherPlace SavedPlace() => new()
    {
        Province = _settings.Current.WeatherProvince,
        City = _settings.Current.WeatherCity,
        District = _settings.Current.WeatherDistrict,
        Latitude = _settings.Current.WeatherLatitude,
        Longitude = _settings.Current.WeatherLongitude
    };

    private void ApplyWeatherUi()
    {
        if (_weather is null) return;
        NotchTemp.Text = $"{_weather.Temperature:0}°";
        NotchWeather.Text = _weather.WeatherGlyph;
        HeaderWeatherIcon.Text = _weather.WeatherGlyph;
        HeaderWeatherText.Text = $" {_weather.Temperature:0}°";
        BindWeatherDetail();
    }

    private void BindWeatherDetail()
    {
        if (_weather is null)
        {
            WeatherCity.Text = "";
            WeatherRefreshed.Text = "";
            WeatherNowIcon.Text = "";
            WeatherNow.Text = "暂无天气数据";
            WeatherRange.Text = "";
            ForecastList.ItemsSource = null;
            return;
        }
        WeatherNowIcon.Text = _weather.WeatherGlyph;
        WeatherNow.Text = $"{_weather.Temperature:0}° {_weather.WeatherText}";
        WeatherRange.Text = $"{_weather.TempMin:0}° / {_weather.TempMax:0}°";
        WeatherCity.Text = _weather.City;
        WeatherRefreshed.Text = FormatRefreshTime(WeatherService.ForecastUpdatedLocal);
        ForecastList.ItemsSource = _weather.Days.Skip(1).Take(6).ToList();
    }

    private static string FormatRefreshTime(DateTime? updated)
    {
        if (updated is null) return "";
        var local = updated.Value;
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm")
            : local.ToString("M月d日 HH:mm");
    }

    private void BuildIconList()
    {
        _visiblePages = _library.Current.Categories
            .Where(c => c.Visible)
            .OrderBy(c => c.SortOrder)
            .ToList();
        if (_pageIndex >= _visiblePages.Count)
            _pageIndex = 0;
        _activePageName = _visiblePages.ElementAtOrDefault(_pageIndex)?.Name ?? "灵栖";
    }

    private void RenderPage()
    {
        if (_pageIndex >= _visiblePages.Count)
            _pageIndex = Math.Max(0, _visiblePages.Count - 1);

        _pageIcons = [];
        if (_pageIndex >= 0 && _pageIndex < _visiblePages.Count)
        {
            var cat = _visiblePages[_pageIndex];
            _pageIcons = _library.Current.Items
                .Where(i => i.CategoryId == cat.Id
                            && !string.Equals(i.ItemKind, "Widget", StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.SortOrder)
                .Select(i =>
                {
                    var vm = new ItemVm(i);
                    vm.LoadIcon(_icons);
                    return vm;
                })
                .ToList();
            _activePageName = cat.Name;
        }

        IconGrid.ItemsSource = _pageIcons;
        ApplyIconGridLayout(_pageIcons.Count);
        SyncPanelSize(applyHost: PanelHostSettled);
        IconScroll.ScrollToVerticalOffset(0);
        RenderDots();
        UpdatePageTitle();
    }

    private void RefreshRecycleIcons()
    {
        foreach (var vm in _pageIcons)
        {
            if (!ShellPlaces.IsRecycleBin(vm.Model)) continue;
            vm.LoadIcon(_icons);
        }
    }

    /// <summary>
    /// &lt;6: one row; 6–12: two rows evenly (ceil(n/2) cols); &gt;12: 6 per row.
    /// </summary>
    private static int ResolveIconColumns(int count)
    {
        if (count <= 0) return MaxIconColumns;
        if (count < 6) return count;
        if (count <= 12) return (int)Math.Ceiling(count / 2.0);
        return MaxIconColumns;
    }

    private double ResolvePanelWidth(int iconCount) =>
        // Fixed to 6-icon width for icons / search / weather
        PanelSideChrome + MaxIconColumns * IconSlotW;

    private double ResolvePanelHeight() => _state switch
    {
        NotchUiState.PanelSearch => PanelHSearch,
        NotchUiState.PanelWeather => PanelHWeather,
        _ => PanelHIcons
    };

    /// <summary>True when panel is open and morph finished — safe to resize host.</summary>
    private bool PanelHostSettled =>
        !_morphing
        && _state is (NotchUiState.Panel or NotchUiState.PanelSearch or NotchUiState.PanelWeather)
        && Shell.Height >= Math.Min(_panelH, PanelHSearch) - 0.5;

    private void SyncPanelSize(bool applyHost)
    {
        _panelW = ResolvePanelWidth(_pageIcons.Count);
        _panelH = ResolvePanelHeight();
        PanelRoot.Width = _panelW;
        PanelRoot.Height = _panelH;
        if (!applyHost) return;
        Width = Host(_panelW);
        Height = Host(_panelH);
        Shell.Width = _panelW;
        Shell.Height = _panelH;
        PlaceOnMonitor();
        ApplyShellRoundClip();
    }

    private void ApplyIconGridLayout(int count)
    {
        // Panel width is fixed at 6 slots; grid columns still follow count rules and stay centered
        var cols = ResolveIconColumns(count);
        IconGrid.HorizontalAlignment = HorizontalAlignment.Center;
        IconGrid.Width = cols * IconSlotW;

        var rows = count <= 0
            ? 0
            : count < 6
                ? 1
                : count <= 12
                    ? 2
                    : (int)Math.Ceiling(count / (double)MaxIconColumns);
        IconGrid.VerticalAlignment = VerticalAlignment.Top;
        IconGrid.Height = rows * IconRowH;

        void Apply()
        {
            var ug = FindVisualChild<UniformGrid>(IconGrid);
            if (ug is null) return;
            ug.Columns = cols;
            ug.Rows = Math.Max(rows, 1);
            ug.VerticalAlignment = VerticalAlignment.Top;
            ug.Height = rows * IconRowH;
        }

        Apply();
        if (FindVisualChild<UniformGrid>(IconGrid) is null)
            Dispatcher.BeginInvoke(Apply, DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T hit) return hit;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void RenderDots()
    {
        PageDots.Children.Clear();
        for (var i = 0; i < _visiblePages.Count; i++)
        {
            var idx = i;
            var isCurrent = i == _pageIndex;
            var cat = _visiblePages[i];
            var dot = new Border
            {
                Width = isCurrent ? 14 : 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(3, 0, 3, 0),
                Background = isCurrent
                    ? ThemeService.Brush(ThemeService.Island(_settings.Current).Accent)
                    : ThemeService.Brush(ThemeService.Island(_settings.Current).TextMuted),
                Cursor = Cursors.Hand,
                ToolTip = cat.Name,
                Tag = idx
            };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                _pageIndex = idx;
                RenderPage();
            };
            PageDots.Children.Add(dot);
        }
    }

    private void UpdatePageTitle()
    {
        if (_state is NotchUiState.PanelSearch)
        {
            PageTitle.Text = "搜索";
            return;
        }
        if (_state is NotchUiState.PanelWeather)
        {
            PageTitle.Text = "天气";
            return;
        }

        PageTitle.Text = _visiblePages.ElementAtOrDefault(_pageIndex)?.Name
                         ?? _activePageName
                         ?? "灵栖";
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex <= 0) return;
        _pageIndex--;
        RenderPage();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex >= _visiblePages.Count - 1) return;
        _pageIndex++;
        RenderPage();
    }

    private async void Icon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ItemVm vm }) return;
        if (!vm.CanInteract) return;
        if (_launch.TryActivateExisting(vm.Model)) return;
        vm.IsLaunching = true;
        // Paint the spinner before Process.Start blocks this thread.
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        try
        {
            if (!_launch.TryLaunch(vm.Model, out _))
                return;
            await _launch.WaitUntilRunningAsync(vm.Model, TimeSpan.FromSeconds(4));
        }
        finally
        {
            vm.IsLaunching = false;
        }
    }

    private void SearchBtn_Click(object sender, RoutedEventArgs e) => ShowSearchView();
    private void WeatherBtn_Click(object sender, RoutedEventArgs e) => ShowWeatherView();
    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => RequestOpenSettings?.Invoke();
    private void BackFromSub_Click(object sender, RoutedEventArgs e) => ShowIconsView();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        ResetIdleTimer();
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        RunSearch();
    }

    private void RefreshEngineUi()
    {
        var eng = CurrentEngine();
        _selectedEngineId = eng?.Id;
        EngineIcon.Source = SearchEngineIcons.Get(eng?.Id);
        EngineBtn.ToolTip = eng?.Name ?? "选择搜索引擎";
    }

    private SearchEngineInfo? CurrentEngine()
    {
        var engines = _settings.Current.SearchEngines;
        if (!string.IsNullOrEmpty(_selectedEngineId))
        {
            var picked = engines.FirstOrDefault(e => e.Id == _selectedEngineId && e.Enabled);
            if (picked is not null) return picked;
        }
        return engines.FirstOrDefault(e => e.Enabled);
    }

    private void EngineBtn_Click(object sender, RoutedEventArgs e)
    {
        ResetIdleTimer();
        _collapseTimer.Stop();
        if (_enginePopup?.IsOpen == true)
        {
            _enginePopup.IsOpen = false;
            return;
        }

        var p = ThemeService.Island(_settings.Current);
        var currentId = CurrentEngine()?.Id;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4)
        };
        foreach (var eng in _settings.Current.SearchEngines.Where(x => x.Enabled))
        {
            var id = eng.Id;
            var selected = string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase);
            var cell = new Border
            {
                Width = 44,
                Margin = new Thickness(1, 0, 1, 0),
                CornerRadius = new CornerRadius(6),
                Background = selected ? ThemeService.Brush(p.Hover) : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Padding = new Thickness(2, 4, 2, 4),
                ToolTip = eng.Name,
                Tag = id
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new Image
            {
                Source = SearchEngineIcons.Get(id),
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3)
            });
            stack.Children.Add(new TextBlock
            {
                Text = eng.Name,
                FontSize = 9,
                Foreground = ThemeService.Brush(p.Text),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 40
            });
            cell.Child = stack;
            cell.MouseEnter += (_, _) =>
            {
                if (!selected) cell.Background = ThemeService.Brush(p.Hover);
            };
            cell.MouseLeave += (_, _) =>
            {
                cell.Background = selected ? ThemeService.Brush(p.Hover) : Brushes.Transparent;
            };
            cell.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                _selectedEngineId = id;
                RefreshEngineUi();
                ResetIdleTimer();
                if (_enginePopup is not null) _enginePopup.IsOpen = false;
            };
            row.Children.Add(cell);
        }

        _enginePopup = new Popup
        {
            PlacementTarget = EngineBtn,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, p.PanelRgb.R, p.PanelRgb.G, p.PanelRgb.B)),
                BorderBrush = ThemeService.Brush(p.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = row,
                Padding = new Thickness(4)
            }
        };
        _enginePopup.Closed += EnginePopup_Closed;
        _enginePopup.IsOpen = true;
    }

    private void EnginePopup_Closed(object? sender, EventArgs e)
    {
        if (sender is Popup popup)
            popup.Closed -= EnginePopup_Closed;

        _idleTimer.Stop();
        _collapseTimer.Stop();

        // Popup is outside the host; restore pointer state from actual hover
        if (IsMouseOver)
        {
            _pointerInside = true;
            return;
        }

        _pointerInside = false;
        if (_state is NotchUiState.PanelSearch or NotchUiState.PanelWeather)
        {
            ArmSubviewLeaveTimer();
            return;
        }
        if (_state is NotchUiState.Panel)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.Current.CollapseDelayMs);
            _collapseTimer.Start();
        }
    }

    private void RunSearch()
    {
        var q = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return;
        var eng = CurrentEngine();
        if (eng is null) return;
        var url = eng.BuildSearchUrl(q);
        try
        {
            var browser = _settings.Current.PreferredBrowserPath;
            if (!string.IsNullOrWhiteSpace(browser) && File.Exists(browser))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browser,
                    Arguments = url,
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
        }
        catch { /* ignore */ }
    }
}
