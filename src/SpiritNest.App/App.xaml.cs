using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using SpiritNest.Models;
using SpiritNest.Services;
using SpiritNest.Views;

namespace SpiritNest;

public partial class App : Application
{
    static App()
    {
        AssemblyLoadContext.Default.Resolving += ResolveLanguageAssembly;
    }

    /// <summary>
    /// Published satellites live in Languages\zh-Hans. Dev builds still use the default culture folder.
    /// </summary>
    private static Assembly? ResolveLanguageAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is not { } simple || !simple.EndsWith(".resources", StringComparison.Ordinal))
            return null;
        var culture = name.CultureName;
        if (string.IsNullOrEmpty(culture) || culture.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Path.Combine(AppContext.BaseDirectory, "Languages", culture, simple + ".dll");
        if (!File.Exists(path))
            return null;
        return context.LoadFromAssemblyPath(path);
    }

    private readonly SingleInstanceService _singleInstance = new();
    private SettingsStore? _settings;
    private LibraryStore? _library;
    private CollectService? _collect;
    private NotchHostWindow? _notch;
    private TaskbarIcon? _tray;
    private DispatcherTimer? _displayTimer;
    private FullscreenWatcher? _fullscreen;
    private BrowserMinimalWatcher? _browserMinimal;
    private int _lastMonitorCount;
    private Icon? _trayIconHandle;
    private SettingsWindow? _settingsWindow;
    private SessionEndingEventHandler? _systemSessionEnding;
    private MenuItem? _trayAutostart;
    private MenuItem? _trayMinimal;

    public bool IsExiting { get; private set; }

    /// <summary>Live settings for chrome/dialogs theming.</summary>
    internal AppSettings? CurrentSettings => _settings?.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (UninstallActions.TryHandle(e.Args) || AutostartService.TryHandle(e.Args))
        {
            Shutdown(Environment.ExitCode);
            return;
        }

        SessionEnding += (_, e) =>
        {
            e.Cancel = false;
            RequestExitForShutdown();
        };
        _systemSessionEnding = (_, _) =>
        {
            try { Dispatcher.BeginInvoke(RequestExitForShutdown); }
            catch { RequestExitForShutdown(); }
        };
        SystemEvents.SessionEnding += _systemSessionEnding;

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "灵栖未处理异常");
            args.Handled = true;
        };

        try
        {
            StartCore();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "启动失败：\n\n" + ex.Message + "\n\n详情已复制到剪贴板时可粘贴发给开发者。\n\n" + ex,
                "灵栖",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            try { Clipboard.SetText(ex.ToString()); } catch { /* ignore */ }
            Shutdown();
        }
    }

    private void StartCore()
    {
        if (!_singleInstance.TryAcquire())
        {
            Shutdown();
            return;
        }

        _settings = new SettingsStore();
        _settings.Load();
        var palette = ThemeService.Resolve(_settings.Current);
        ThemeService.ApplyResources(Resources, palette, _settings.Current);

        _library = new LibraryStore();
        _library.Load();
        ShellPlaces.Apply(_settings.Current, _library);
        _collect = new CollectService(_library);
        AreaCatalog.EnsureLocal();
        _ = AreaCatalog.TryUpdateAsync();

        AutostartService.Apply(_settings.Current.StartWithWindows);

        _tray = (TaskbarIcon)FindResource("TrayIcon");
        AssignTrayIconFromFile(_tray);
        if (_tray.ContextMenu is ContextMenu trayMenu)
        {
            MenuChrome.Attach(trayMenu);
            WireTrayMenu(trayMenu);
        }

        _notch = new NotchHostWindow(_settings, _library);
        _notch.RequestOpenSettings += ShowSettings;
        _notch.WeatherUpdated += () => _settingsWindow?.SyncFromStore();
        _notch.Show();

        _singleInstance.StartActivationServer(
            onActivate: () => { },
            onExit: () => Dispatcher.BeginInvoke(RequestExitForShutdown));

        _lastMonitorCount = DisplayPlacement.GetMonitors().Count;
        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _displayTimer.Tick += (_, _) =>
        {
            ShellPlaces.PollRecycleState();
            var count = DisplayPlacement.GetMonitors().Count;
            if (count == _lastMonitorCount) return;
            _lastMonitorCount = count;
            _notch?.ApplyAppearance();
        };
        _displayTimer.Start();

        Dispatcher.BeginInvoke(() =>
        {
            try { _collect?.RefreshAllBroken(); } catch { /* ignore */ }

            _fullscreen = new FullscreenWatcher(() => _settings!.Current.HideOnFullscreen);
            _fullscreen.EnteredFullscreen += () => Dispatcher.Invoke(() =>
            {
                _notch?.HideForFullscreen();
            });
            _fullscreen.LeftFullscreen += () => Dispatcher.Invoke(() => _notch?.ShowAfterFullscreen());
            _fullscreen.Start();

            _browserMinimal = new BrowserMinimalWatcher(
                () => _settings!.Current.BrowserMinimal,
                () => _notch is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(_notch).Handle);
            _browserMinimal.Entered += () => Dispatcher.Invoke(() => _notch?.SetBrowserMinimal(true));
            _browserMinimal.Left += () => Dispatcher.Invoke(() => _notch?.SetBrowserMinimal(false));
            _browserMinimal.Start();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void WireTrayMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => SyncTrayChecks();
    }

    private void SyncTrayChecks()
    {
        if (_settings is null) return;
        if (_trayAutostart is not null)
            _trayAutostart.IsChecked = _settings.Current.StartWithWindows;
        if (_trayMinimal is not null)
            _trayMinimal.IsChecked = _settings.Current.MinimalNotch;
    }

    private void AssignTrayIconFromFile(TaskbarIcon tray)
    {
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SpiritNest.ico");
        if (!File.Exists(icoPath))
            icoPath = Path.Combine(AppContext.BaseDirectory, "SpiritNest.ico");
        if (!File.Exists(icoPath)) return;

        _trayIconHandle?.Dispose();
        _trayIconHandle = new Icon(icoPath);
        tray.Icon = _trayIconHandle;
    }

    private void ShowSettings()
    {
        if (_settings is null || _library is null || _collect is null) return;

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        try
        {
            var dlg = new SettingsWindow(_settings, _library, _collect)
            {
                Topmost = false,
                ShowInTaskbar = true
            };
            _settingsWindow = dlg;
            dlg.LibraryChanged += () => _notch?.Reload();
            dlg.SettingsChanged += async () =>
            {
                _notch?.ApplyAppearance();
                SyncTrayChecks();
                var relocate = dlg.TakeRelocate();
                if (relocate) dlg.SetLocateSpin(true);
                try
                {
                    if (_notch is not null)
                        await _notch.RefreshWeatherAsync(relocate);
                }
                finally
                {
                    if (relocate) dlg.SetLocateSpin(false);
                }
                dlg.SyncFromStore();
            };
            dlg.RequestCollect += () =>
            {
                dlg.Close();
                ShowCollect();
            };
            dlg.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settingsWindow, dlg))
                    _settingsWindow = null;
                _notch?.ApplyAppearance();
                _notch?.Reload();
            };
            dlg.Show();
            dlg.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开设置失败：\n\n" + ex, "灵栖", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowCollect()
    {
        if (_collect is null || _library is null) return;
        var dlg = new CollectDialog(_collect, _library) { Owner = _notch };
        if (dlg.ShowDialog() == true)
            _notch?.Reload();
    }

    private void TrayMenu_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Name == "TrayAutostartItem" || item.Header is "开机启动" or "开启启动")
                _trayAutostart = item;
            else if (item.Name == "TrayMinimalItem" || item.Header is "极简模式")
                _trayMinimal = item;
        }
        SyncTrayChecks();
    }

    private void TrayAutostart_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var on = sender is MenuItem { IsChecked: true };
        _settings.Current.StartWithWindows = on;
        _settings.Save();
        AutostartService.Apply(on);
        SyncTrayChecks();
        _settingsWindow?.SyncFromStore();
    }

    private void TrayMinimal_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var on = sender is MenuItem { IsChecked: true };
        _settings.Current.MinimalNotch = on;
        _settings.Save();
        _notch?.SetMinimal(on);
        SyncTrayChecks();
        _settingsWindow?.SyncFromStore();
    }

    private void TraySettings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void TrayExit_Click(object sender, RoutedEventArgs e) => RequestExitForShutdown();

    public void RequestExitForShutdown()
    {
        if (IsExiting) return;
        IsExiting = true;

        try { _fullscreen?.Stop(); } catch { /* ignore */ }
        try { _displayTimer?.Stop(); } catch { /* ignore */ }

        try { _tray?.Dispose(); } catch { /* ignore */ }
        _tray = null;
        try { _trayIconHandle?.Dispose(); } catch { /* ignore */ }
        _trayIconHandle = null;

        try { _singleInstance.Dispose(); } catch { /* ignore */ }

        try { _settingsWindow?.Close(); } catch { /* ignore */ }
        try { _notch?.Close(); } catch { /* ignore */ }

        try { Shutdown(); } catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        if (_systemSessionEnding is not null)
        {
            try { SystemEvents.SessionEnding -= _systemSessionEnding; } catch { /* ignore */ }
            _systemSessionEnding = null;
        }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _trayIconHandle?.Dispose(); } catch { /* ignore */ }
        try { _singleInstance.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
