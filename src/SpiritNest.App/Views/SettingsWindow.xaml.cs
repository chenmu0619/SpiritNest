using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SpiritNest.Models;
using SpiritNest.Services;

namespace SpiritNest.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly LibraryStore _library;
    private readonly CollectService _collect;
    private bool _ready;
    private bool _buildingEngines;
    private bool _enginePressing;
    private bool _engineDragging;
    private Point _enginePress;
    private string? _engineDragId;
    private int _engineInsert = -1;
    private Border? _engineInsertTail;
    private readonly List<EngineRowUi> _engineRows = new();

    private bool _placePicking;
    private bool _placeEditing;
    private bool _placeFilling;
    private int _placeSearchGen;
    private bool _relocateOnRefresh;

    public bool TakeRelocate()
    {
        var relocate = _relocateOnRefresh;
        _relocateOnRefresh = false;
        return relocate;
    }

    public void SetLocateSpin(bool spinning)
    {
        if (PlaceLocateIcon.RenderTransform is not RotateTransform spin) return;
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

    private sealed class EngineRowUi
    {
        public required string Id { get; init; }
        public required Border Root { get; init; }
        public required Border InsertBefore { get; init; }
    }

    public event Action? LibraryChanged;
    public event Action? SettingsChanged;
#pragma warning disable CS0067
    public event Action? RequestCollect;
#pragma warning restore CS0067

    public SettingsWindow(SettingsStore settings, LibraryStore library, CollectService collect)
    {
        _settings = settings;
        _library = library;
        _collect = collect;
        InitializeComponent();
        ScreenDpi.Attach(this);

        // Load values without firing persist handlers
        _ready = false;
        LoadToggleState();
        AnimDurationSlider.Value = SnapAnimationMs(_settings.Current.AnimationDurationMs);
        BrowserPathBox.Text = _settings.Current.PreferredBrowserPath ?? "";
        var amapKey = _settings.Current.AmapKey ?? "";
        AmapKeyPassword.Password = amapKey;
        AmapKeyBox.Text = amapKey;

        RefreshAnimDurationLabel();
        BindSearchEngines();
        BindCategories();
        VersionText.Text = "版本 " + AppVersionLabel();
        InstallPathText.Text = InstallDirectory();
        DataPathText.Text = AppPaths.Root;
        ApplyAppearance();
        ShowPage("general");
        NavGeneral.IsChecked = true;
        _ready = true;
        ShellPlaces.RecycleStateChanged += RefreshRecycleIcons;
        Closed += (_, _) => ShellPlaces.RecycleStateChanged -= RefreshRecycleIcons;
    }

    private static string AppVersionLabel()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    /// <summary>Refresh toggles from store (e.g. after tray menu changes).</summary>
    public void SyncFromStore()
    {
        var wasReady = _ready;
        _ready = false;
        LoadToggleState();
        _ready = wasReady;
    }

    private void LoadToggleState()
    {
        ChkAutostart.IsChecked = _settings.Current.StartWithWindows;
        ChkMinimal.IsChecked = _settings.Current.MinimalNotch;
        ChkBrowserMinimal.IsChecked = _settings.Current.BrowserMinimal;
        ChkHideFullscreen.IsChecked = _settings.Current.HideOnFullscreen;
        ChkThisPc.IsChecked = _settings.Current.ShowThisPc;
        ChkRecycle.IsChecked = _settings.Current.ShowRecycleBin;
        ChkWeather.IsChecked = _settings.Current.ShowWeather;
        ApplyPlaceUi();
        ApplyLocationSource();
    }

    public void ApplyAppearance()
    {
        var palette = ThemeService.Chrome(_settings.Current);
        ThemeService.ApplyResources(Resources, palette, _settings.Current);
        try { ThemeService.ApplyResources(Application.Current.Resources, palette, _settings.Current); } catch { /* ignore */ }
        GlassChrome.ApplyLocalBrushes(Resources, palette);

        ShellRoot.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7));
        ShellRoot.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0));
        ShellRoot.CornerRadius = new CornerRadius(16);
        CornerClip.Attach(ShellRoot, 16);

        BrowserPathBox.Foreground = ThemeService.Brush(palette.Text);
        BrowserPathBox.CaretBrush = ThemeService.Brush(palette.Text);
        BrowserPathBox.BorderBrush = ThemeService.Brush(palette.Border);
        AmapKeyBox.Foreground = ThemeService.Brush(palette.Text);
        AmapKeyBox.CaretBrush = ThemeService.Brush(palette.Text);
        AmapKeyBox.BorderBrush = ThemeService.Brush(palette.Border);
        AmapKeyPassword.Foreground = ThemeService.Brush(palette.Text);
        AmapKeyPassword.CaretBrush = ThemeService.Brush(palette.Text);
        AmapKeyPassword.BorderBrush = ThemeService.Brush(palette.Border);
        AmapKeyToggleIcon.Foreground = ThemeService.Brush(palette.Text);
        PlaceSearch.Foreground = ThemeService.Brush(palette.Text);
        PlaceSearch.CaretBrush = ThemeService.Brush(palette.Text);
        UpdateLocateButton();
        if (Resources["FieldBg"] is SolidColorBrush field)
        {
            BrowserPathBox.Background = field;
            AmapKeyBox.Background = field;
            AmapKeyPassword.Background = field;
            PlaceSearch.Background = field;
        }
    }

    private void ShowPage(string id)
    {
        if (PageGeneral is not null)
            PageGeneral.Visibility = id == "general" ? Visibility.Visible : Visibility.Collapsed;
        if (PagePages is not null)
            PagePages.Visibility = id == "pages" ? Visibility.Visible : Visibility.Collapsed;
        if (PageSearch is not null)
            PageSearch.Visibility = id == "search" ? Visibility.Visible : Visibility.Collapsed;
        if (PageAbout is not null)
            PageAbout.Visibility = id == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (NavGeneral.IsChecked == true) ShowPage("general");
        else if (NavPages.IsChecked == true) { BindCategories(); ShowPage("pages"); }
        else if (NavSearch.IsChecked == true) ShowPage("search");
        else if (NavAbout.IsChecked == true) ShowPage("about");
    }

    private void BindCategories() => RebuildPageList();

    private void BindSearchEngines()
    {
        if (EngineListHost is null) return;
        _buildingEngines = true;
        EngineListHost.Children.Clear();
        _engineRows.Clear();
        foreach (var eng in _settings.Current.SearchEngines)
            EngineListHost.Children.Add(BuildEngineRow(eng).Root);
        _engineInsertTail = new Border
        {
            Height = 2,
            Margin = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x9D, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        EngineListHost.Children.Add(_engineInsertTail);
        _buildingEngines = false;
    }

    private EngineRowUi BuildEngineRow(SearchEngineInfo eng)
    {
        var insertBefore = new Border
        {
            Height = 2,
            Margin = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x9D, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var handle = BuildDragHandle();
        var id = eng.Id;
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _enginePressing = true;
            _engineDragging = false;
            _engineDragId = id;
            _engineInsert = -1;
            _enginePress = e.GetPosition(EngineListHost);
            EngineListHost.CaptureMouse();
            e.Handled = true;
        };
        var name = new TextBlock
        {
            Text = eng.Name,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Ink"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var icon = new Image
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 8, 0),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Source = SearchEngineIcons.Get(eng.Id)
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.Fant);
        name.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            EditEngine(id);
            e.Handled = true;
        };
        icon.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            EditEngine(id);
            e.Handled = true;
        };
        var enabled = new CheckBox
        {
            Style = (Style)FindResource("InlineSwitch"),
            IsChecked = eng.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            ToolTip = "启用"
        };
        enabled.Checked += (_, _) => SetEngineEnabled(id, true);
        enabled.Unchecked += (_, _) => SetEngineEnabled(id, false);

        var deleteBtn = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 10, 0),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "删除",
            Child = new Border
            {
                Width = 10,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        deleteBtn.MouseLeftButtonDown += (_, e) =>
        {
            DeleteEngine(id);
            e.Handled = true;
        };

        var header = new Grid { Height = 44 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(handle, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(name, 2);
        Grid.SetColumn(enabled, 3);
        Grid.SetColumn(deleteBtn, 4);
        header.Children.Add(handle);
        header.Children.Add(icon);
        header.Children.Add(name);
        header.Children.Add(enabled);
        header.Children.Add(deleteBtn);

        var body = new StackPanel();
        body.Children.Add(insertBefore);
        body.Children.Add(header);
        body.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(12, 0, 12, 0),
            Background = (Brush)FindResource("Line")
        });
        var row = new EngineRowUi
        {
            Id = id,
            Root = new Border { Background = Brushes.Transparent, Child = body },
            InsertBefore = insertBefore
        };
        _engineRows.Add(row);
        return row;
    }

    private void EngineAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EngineEditorWindow(this);
        if (dlg.ShowDialog() != true) return;
        var icons = new IconService();
        foreach (var r in dlg.Results)
        {
            var id = Guid.NewGuid().ToString("N");
            _settings.Current.SearchEngines.Add(new SearchEngineInfo
            {
                Id = id,
                Name = r.Name,
                UrlTemplate = r.UrlTemplate,
                Enabled = true
            });
            ApplyEngineIcon(icons, id, r);
        }
        PersistSettings();
        BindSearchEngines();
    }

    private void EditEngine(string id)
    {
        var eng = _settings.Current.SearchEngines.FirstOrDefault(e => e.Id == id);
        if (eng is null) return;
        var dlg = new EngineEditorWindow(this, eng);
        if (dlg.ShowDialog() != true) return;
        var r = dlg.Results.FirstOrDefault();
        if (r.Name is null) return;
        eng.Name = r.Name;
        eng.UrlTemplate = r.UrlTemplate;
        ApplyEngineIcon(new IconService(), eng.Id, r);
        PersistSettings();
        BindSearchEngines();
    }

    private void DeleteEngine(string id)
    {
        var eng = _settings.Current.SearchEngines.FirstOrDefault(e => e.Id == id);
        if (eng is null) return;
        _settings.Current.SearchEngines.Remove(eng);
        new IconService().ClearUrlIcon(SearchEngineIcons.CustomKey(id));
        SearchEngineIcons.Invalidate(id);
        PersistSettings();
        BindSearchEngines();
    }

    private static void ApplyEngineIcon(IconService icons, string id, EngineEditResult result)
    {
        var key = SearchEngineIcons.CustomKey(id);
        if (result.IconCleared)
            icons.ClearUrlIcon(key);
        else if (result.IconPng is { Length: > 0 })
        {
            icons.SaveUrlIcon(key, result.IconPng);
            if (!string.Equals(result.CacheKey, key, StringComparison.Ordinal))
                icons.ClearUrlIcon(result.CacheKey);
        }
        else if (!string.IsNullOrEmpty(result.CacheKey) && result.CacheKey != key)
        {
            var staged = IconService.UrlIconCachePath(result.CacheKey);
            if (File.Exists(staged))
            {
                icons.SaveUrlIcon(key, File.ReadAllBytes(staged));
                icons.ClearUrlIcon(result.CacheKey);
            }
        }
        SearchEngineIcons.Invalidate(id);
    }

    private void SetEngineEnabled(string id, bool enabled)
    {
        if (!_ready || _buildingEngines) return;
        var eng = _settings.Current.SearchEngines.FirstOrDefault(e => e.Id == id);
        if (eng is null || eng.Enabled == enabled) return;
        eng.Enabled = enabled;
        PersistSettings();
    }

    private void EngineList_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_enginePressing && !_engineDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishEngineDrag(commit: false);
            return;
        }

        var p = e.GetPosition(EngineListHost);
        if (_enginePressing && (p - _enginePress).Length < 6) return;
        if (_enginePressing)
        {
            _enginePressing = false;
            _engineDragging = true;
            var src = _engineRows.FirstOrDefault(r => r.Id == _engineDragId);
            if (src is not null) src.Root.Opacity = 0.45;
        }
        UpdateEngineInsert(p);
    }

    private void EngineList_MouseUp(object sender, MouseButtonEventArgs e) => FinishEngineDrag(commit: true);

    private void EngineList_LostCapture(object sender, MouseEventArgs e)
    {
        if (_enginePressing || _engineDragging)
            FinishEngineDrag(commit: false);
    }

    private void UpdateEngineInsert(Point p)
    {
        ClearEngineInserts();
        if (_engineRows.Count == 0) return;
        for (var i = 0; i < _engineRows.Count; i++)
        {
            var row = _engineRows[i];
            var top = row.Root.TranslatePoint(new Point(0, 0), EngineListHost).Y;
            var h = Math.Max(1, row.Root.ActualHeight);
            if (p.Y < top || p.Y >= top + h) continue;
            var before = p.Y < top + h / 2;
            _engineInsert = before ? i : i + 1;
            if (before) row.InsertBefore.Visibility = Visibility.Visible;
            else if (i + 1 < _engineRows.Count) _engineRows[i + 1].InsertBefore.Visibility = Visibility.Visible;
            else if (_engineInsertTail is not null) _engineInsertTail.Visibility = Visibility.Visible;
            return;
        }
        _engineInsert = _engineRows.Count;
        if (_engineInsertTail is not null) _engineInsertTail.Visibility = Visibility.Visible;
    }

    private void ClearEngineInserts()
    {
        foreach (var row in _engineRows)
            row.InsertBefore.Visibility = Visibility.Collapsed;
        if (_engineInsertTail is not null)
            _engineInsertTail.Visibility = Visibility.Collapsed;
    }

    private void FinishEngineDrag(bool commit)
    {
        var id = _engineDragId;
        var index = _engineInsert;
        var dragging = _engineDragging;
        _enginePressing = false;
        _engineDragging = false;
        _engineDragId = null;
        _engineInsert = -1;
        ClearEngineInserts();
        foreach (var row in _engineRows)
            row.Root.Opacity = 1;
        if (EngineListHost.IsMouseCaptured) EngineListHost.ReleaseMouseCapture();
        if (!commit || !dragging || id is null || index < 0) return;

        var list = _settings.Current.SearchEngines;
        var from = list.FindIndex(e => e.Id == id);
        if (from < 0 || index == from || index == from + 1) return;
        var item = list[from];
        list.RemoveAt(from);
        if (index > from) index--;
        list.Insert(Math.Clamp(index, 0, list.Count), item);
        PersistSettings();
        BindSearchEngines();
    }

    private void RefreshAnimDurationLabel()
    {
        if (AnimDurationLabel is null || AnimDurationSlider is null) return;
        AnimDurationLabel.Text = $"{AnimDurationSlider.Value:0} ms";
    }

    private void AnimDuration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        RefreshAnimDurationLabel();
        PersistSettings();
    }

    private void AnimDurationSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsSliderThumb(e.OriginalSource as DependencyObject)) return;
        var slider = AnimDurationSlider;
        if (slider.ActualWidth < 1) return;
        var x = Math.Clamp(e.GetPosition(slider).X, 0, slider.ActualWidth);
        var raw = slider.Minimum + x / slider.ActualWidth * (slider.Maximum - slider.Minimum);
        slider.Value = SnapAnimationMs((int)Math.Round(raw));
        e.Handled = true;
    }

    private static bool IsSliderThumb(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Thumb) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static int SnapAnimationMs(int ms)
    {
        if (ms < 100) ms = 360;
        var snapped = (int)(Math.Round(ms / 50.0) * 50);
        return (int)Math.Clamp(snapped, 100, 2000);
    }

    private void PlaceLocate_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ClosePlaceEditor();
        _settings.Current.WeatherCitySource = "auto";
        ApplyPlaceUi();
        _relocateOnRefresh = true;
        PersistSettings();
    }

    private void PlaceSearch_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_placeEditing) return;
        _placeEditing = true;
        _placeFilling = true;
        PlaceSearch.TextAlignment = TextAlignment.Left;
        PlaceSearch.Text = "";
        _placeFilling = false;
        PlaceList.ItemsSource = null;
        PlaceList.Visibility = Visibility.Collapsed;
    }

    private void PlaceSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_placeFilling || !_placeEditing) return;
        var text = PlaceSearch.Text.Trim();
        if (text.Length == 0)
        {
            PlaceList.ItemsSource = null;
            PlaceList.Visibility = Visibility.Collapsed;
            return;
        }

        var hits = AreaCatalog.Search(text);
        PlaceList.ItemsSource = hits;
        PlaceList.DisplayMemberPath = nameof(WeatherPlace.Label);
        PlaceList.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlaceList_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _placePicking = true;

    private void PlaceList_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(PlaceList, src) is not ListBoxItem item) return;
        if (item.DataContext is not WeatherPlace place) return;
        _placePicking = true;
        CommitPlace(place);
    }

    private void PlaceSearch_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_placeEditing) return;
            if (_placePicking)
            {
                if (PlaceList.IsMouseOver || PlaceList.IsKeyboardFocusWithin) return;
                _placePicking = false;
            }
            if (PlaceSearch.IsKeyboardFocusWithin || PlaceList.IsKeyboardFocusWithin) return;
            ClosePlaceEditor();
        }), DispatcherPriority.Input);
    }

    private void PlaceList_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_placeEditing) return;
            if (_placePicking && (PlaceList.IsMouseOver || PlaceList.IsKeyboardFocusWithin)) return;
            _placePicking = false;
            if (PlaceSearch.IsKeyboardFocusWithin || PlaceList.IsKeyboardFocusWithin) return;
            ClosePlaceEditor();
        }), DispatcherPriority.Input);
    }

    private void CommitPlace(WeatherPlace place)
    {
        _placeSearchGen++;
        _settings.Current.WeatherProvince = place.Province;
        _settings.Current.WeatherCity = place.City;
        _settings.Current.WeatherDistrict = place.District;
        _settings.Current.WeatherLatitude = place.Latitude;
        _settings.Current.WeatherLongitude = place.Longitude;
        _settings.Current.WeatherCitySource = "manual";
        _relocateOnRefresh = false;
        ClosePlaceEditor();
        ApplyPlaceUi();
        _settings.Save();
        SettingsChanged?.Invoke();
        _placePicking = false;
    }

    private void ApplyPlaceUi()
    {
        PlaceSearch.IsReadOnly = false;
        PlaceSearch.Focusable = true;
        PlaceSearch.Cursor = Cursors.IBeam;
        PlaceSearch.Opacity = 1;
        UpdateLocateButton();
        if (!_placeEditing)
            ClosePlaceEditor();
    }

    private void UpdateLocateButton()
    {
        var auto = !string.Equals(_settings.Current.WeatherCitySource, "manual", StringComparison.OrdinalIgnoreCase);
        if (TryFindResource("Accent") is Brush accent && TryFindResource("Ink") is Brush ink)
            PlaceLocateIcon.Foreground = auto ? accent : ink;
        PlaceLocateBtn.ToolTip = auto ? "重新定位" : "定位";
    }

    private void ClosePlaceEditor()
    {
        _placeEditing = false;
        _placeSearchGen++;
        PlaceList.Visibility = Visibility.Collapsed;
        PlaceList.ItemsSource = null;
        ShowPlaceText();
    }

    private void ShowPlaceText()
    {
        var label = _settings.Current.WeatherPlaceLabel;
        _placeFilling = true;
        PlaceSearch.TextAlignment = TextAlignment.Right;
        PlaceSearch.Text = string.IsNullOrWhiteSpace(label) ? "尚未获取" : label;
        _placeFilling = false;
    }

    private void ApplyLocationSource()
    {
        var source = WeatherService.LocationSource;
        if (string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(_settings.Current.AmapKey))
            source = "高德";
        if (string.IsNullOrWhiteSpace(source))
        {
            LocationSourceText.Visibility = Visibility.Collapsed;
            return;
        }
        LocationSourceText.Visibility = Visibility.Visible;
        LocationSourceText.Text = "定位数据来自" + source;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e) => PersistSettings();

    private void Browser_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PersistSettings();
            e.Handled = true;
        }
    }

    private bool _amapKeyRevealed;

    private void AmapKey_LostFocus(object sender, RoutedEventArgs e) => PersistSettings();

    private void AmapKey_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PersistSettings();
        e.Handled = true;
    }

    private void AmapKeyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_amapKeyRevealed)
            AmapKeyPassword.Password = AmapKeyBox.Text;
        else
            AmapKeyBox.Text = AmapKeyPassword.Password;
        _amapKeyRevealed = !_amapKeyRevealed;
        AmapKeyPassword.Visibility = _amapKeyRevealed ? Visibility.Collapsed : Visibility.Visible;
        AmapKeyBox.Visibility = _amapKeyRevealed ? Visibility.Visible : Visibility.Collapsed;
        AmapKeyToggleIcon.Text = _amapKeyRevealed ? "\uED1A" : "\uE890";
        AmapKeyToggleBtn.ToolTip = _amapKeyRevealed ? "隐藏" : "显示";
        if (_amapKeyRevealed) AmapKeyBox.Focus();
        else AmapKeyPassword.Focus();
    }

    private string CurrentAmapKey() =>
        (_amapKeyRevealed ? AmapKeyBox.Text : AmapKeyPassword.Password).Trim();

    private void PersistSettings()
    {
        if (!_ready) return;

        _settings.Current.StartWithWindows = ChkAutostart.IsChecked == true;
        _settings.Current.MinimalNotch = ChkMinimal.IsChecked == true;
        _settings.Current.BrowserMinimal = ChkBrowserMinimal.IsChecked == true;
        _settings.Current.HideOnFullscreen = ChkHideFullscreen.IsChecked == true;
        _settings.Current.ShowThisPc = ChkThisPc.IsChecked == true;
        _settings.Current.ShowRecycleBin = ChkRecycle.IsChecked == true;
        _settings.Current.EnablePanelAnimation = true;
        _settings.Current.AnimationDurationMs = SnapAnimationMs((int)AnimDurationSlider.Value);
        _settings.Current.PreferredBrowserPath = string.IsNullOrWhiteSpace(BrowserPathBox.Text)
            ? null
            : BrowserPathBox.Text.Trim();
        var amapKey = CurrentAmapKey();
        var amapKeyChanged = !string.Equals(amapKey, _settings.Current.AmapKey ?? "", StringComparison.Ordinal);
        _settings.Current.AmapKey = amapKey;
        if (amapKeyChanged
            && !string.Equals(_settings.Current.WeatherCitySource, "manual", StringComparison.OrdinalIgnoreCase))
            _relocateOnRefresh = true;
        _settings.Current.ShowWeather = ChkWeather.IsChecked == true;
        _settings.Current.DefaultSearchEngineId =
            _settings.Current.SearchEngines.FirstOrDefault(e => e.Enabled)?.Id ?? "";

        _settings.Save();
        AutostartService.Apply(_settings.Current.StartWithWindows);
        if (ShellPlaces.Apply(_settings.Current, _library))
        {
            LibraryChanged?.Invoke();
            if (PagePages.Visibility == Visibility.Visible)
                RebuildPageList();
        }
        ApplyAppearance();
        SettingsChanged?.Invoke();
    }

    private void CatAdd_Click(object sender, RoutedEventArgs e) => AddPageInline();

    private void BrowseBrowser_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "可执行文件|*.exe|所有文件|*.*",
            Title = "选择默认浏览器"
        };
        if (dlg.ShowDialog(this) == true)
        {
            BrowserPathBox.Text = dlg.FileName;
            PersistSettings();
        }
    }

    private static string InstallDirectory()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void OpenInstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = InstallDirectory();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FlatDialog.Info(this, "无法打开安装目录：\n" + ex.Message);
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = AppPaths.Root,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FlatDialog.Info(this, "无法打开数据文件夹：\n" + ex.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject src
            && (IsDescendantOf(src, PagePages) || IsDescendantOf(src, EngineListHost)))
            return;
        DragMove();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
