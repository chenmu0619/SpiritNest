using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SpiritNest.Models;
using SpiritNest.Services;

namespace SpiritNest.Views;

public partial class UrlEditorWindow : Window
{
    private SolidColorBrush _ink = ThemeService.Brush(Color.FromRgb(0x1A, 0x24, 0x20));
    private SolidColorBrush _line = ThemeService.Brush(Color.FromRgb(0xC5, 0xCD, 0xC8));
    private SolidColorBrush _fieldBg = ThemeService.Brush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private SolidColorBrush _rowBg = ThemeService.Brush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    private readonly IconService _icons = new();
    private readonly List<RowUi> _rows = [];
    private readonly bool _editMode;
    private readonly string? _editItemId;

    public IReadOnlyList<UrlEditResult> Results { get; private set; } = [];

    private void ApplyThemeChrome()
    {
        var live = (Application.Current as SpiritNest.App)?.CurrentSettings;
        var palette = ThemeService.Resolve(live ?? new AppSettings());
        GlassChrome.ApplyLocalBrushes(Resources, palette);
        GlassChrome.ApplyTheme(this, palette, live ?? new AppSettings());
        if (Content is Border shell)
            CornerClip.AttachBorder(shell);
        _ink = ThemeService.Brush(palette.Text);
        _line = ThemeService.Brush(palette.Border);
        _fieldBg = ThemeService.Brush(palette.IsDark
            ? Color.FromArgb(40, 255, 255, 255)
            : Color.FromArgb(220, 255, 255, 255));
        _rowBg = ThemeService.Brush(palette.Hover);
    }

    /// <summary>Settings page uses a solid gray shell, not the notch glass tint.</summary>
    private void ApplySettingsChrome()
    {
        if (Owner is not SettingsWindow) return;
        var page = Color.FromRgb(0xF2, 0xF2, 0xF7);
        var ink = Color.FromRgb(0x1A, 0x1A, 0x1A);
        var muted = Color.FromRgb(0x8A, 0x8A, 0x8A);
        var line = Color.FromArgb(0x1A, 0, 0, 0);
        var hover = Color.FromArgb(0x0A, 0, 0, 0);
        PaintBrush("Ink", ink);
        PaintBrush("Muted", muted);
        PaintBrush("Line", line);
        PaintBrush("Hover", hover);
        PaintBrush("Press", Color.FromArgb(0x14, 0, 0, 0));
        PaintBrush("PrimaryFill", Colors.White);
        if (Content is Border shell)
        {
            shell.Background = new SolidColorBrush(page);
            shell.BorderBrush = new SolidColorBrush(line);
        }
        _ink = new SolidColorBrush(ink);
        _line = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
        _fieldBg = new SolidColorBrush(Colors.White);
        _rowBg = new SolidColorBrush(Colors.White);
    }

    private void PaintBrush(string key, Color color)
    {
        if (Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
    }

    /// <summary>Batch add mode.</summary>
    public UrlEditorWindow(Window? owner)
    {
        Owner = owner;
        _editMode = false;
        InitializeComponent();
        ScreenDpi.Attach(this);
        GlassChrome.Attach(this);
        ApplyThemeChrome();
        ApplySettingsChrome();
        Title = "添加网址";
        TitleText.Text = "添加网址";
        Loaded += (_, _) => AlignToOwnerPanel();
        SourceInitialized += (_, _) => AlignToOwnerPanel();
        AddRow();
        AddRow();
    }

    /// <summary>Single-item edit mode.</summary>
    public UrlEditorWindow(Window? owner, ItemInfo existing)
    {
        Owner = owner;
        _editMode = true;
        _editItemId = existing.Id;
        InitializeComponent();
        ScreenDpi.Attach(this);
        GlassChrome.Attach(this);
        ApplyThemeChrome();
        ApplySettingsChrome();
        Title = "编辑网址";
        TitleText.Text = "编辑网址";
        Loaded += (_, _) => AlignToOwnerPanel();
        SourceInitialized += (_, _) => AlignToOwnerPanel();
        AddRowBtn.Visibility = Visibility.Collapsed;

        var row = AddRow();
        row.NameBox.Text = existing.DisplayName ?? "";
        row.UrlBox.Text = existing.TargetPath ?? "";
        row.NameTouched = !string.IsNullOrWhiteSpace(existing.DisplayName);
        row.IconTouched = !string.IsNullOrWhiteSpace(existing.IconCacheKey)
                          && File.Exists(IconService.UrlIconCachePath(existing.IconCacheKey!));
        row.CacheKey = existing.IconCacheKey ?? existing.Id;
        row.IconImage.Source = _icons.GetUrlIcon(row.CacheKey);
        if (row.IconTouched) row.PendingPng = File.ReadAllBytes(IconService.UrlIconCachePath(row.CacheKey));
    }

    /// <summary>
    /// Place beside the nest panel using free space on the current monitor.
    /// Prefer the outer side of the panel (panel on left → dialog right, etc.).
    /// </summary>
    private void AlignToOwnerPanel()
    {
        if (Owner is null) return;

        var w = ActualWidth > 1 ? ActualWidth : Width;
        var h = ActualHeight > 1 ? ActualHeight : Height;
        var ow = Owner.ActualWidth > 1 ? Owner.ActualWidth : Owner.Width;
        var oh = Owner.ActualHeight > 1 ? Owner.ActualHeight : Owner.Height;
        var ol = Owner.Left;
        var ot = Owner.Top;
        var work = DisplayPlacement.FromDip(new Point(ol + ow / 2, ot + oh / 2)).WorkArea;
        const double gap = 8;
        const double pad = 4;

        var spaceRight = work.Right - (ol + ow) - gap - pad;
        var spaceLeft = ol - work.Left - gap - pad;
        var spaceBelow = work.Bottom - (ot + oh) - gap - pad;
        var spaceAbove = ot - work.Top - gap - pad;

        // Panel on left half of screen → prefer opening to the right; otherwise left
        var preferRight = ol + ow / 2 < work.Left + work.Width / 2;
        var preferBelow = ot + oh / 2 < work.Top + work.Height / 2;

        double left;
        double top;

        var canRight = spaceRight >= w;
        var canLeft = spaceLeft >= w;
        var canBelow = spaceBelow >= h;
        var canAbove = spaceAbove >= h;

        if (canRight || canLeft)
        {
            var useRight = canRight && canLeft ? preferRight : canRight;
            left = useRight ? ol + ow + gap : ol - w - gap;
            top = ot; // top-align with panel when beside
        }
        else if (canBelow || canAbove)
        {
            var useBelow = canBelow && canAbove ? preferBelow : canBelow;
            left = ol; // left-align with panel when above/below
            top = useBelow ? ot + oh + gap : ot - h - gap;
        }
        else
        {
            // No full side free — start at panel origin, then clamp into work area
            left = ol;
            top = ot;
        }

        left = Math.Clamp(left, work.Left + pad, Math.Max(work.Left + pad, work.Right - w - pad));
        top = Math.Clamp(top, work.Top + pad, Math.Max(work.Top + pad, work.Bottom - h - pad));
        Left = left;
        Top = top;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        GlassChrome.DragFromChrome(this, e, e.OriginalSource as DependencyObject);

    private RowUi AddRow()
    {
        var row = new RowUi
        {
            CacheKey = Guid.NewGuid().ToString("N"),
            Root = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(8, 6, 6, 6),
                Background = _rowBg,
                CornerRadius = new CornerRadius(6)
            }
        };

        // Icon (spans two inputs) | URL / Name stacked | ×
        const double boxH = 30;
        const double boxGap = 6;
        var iconSize = boxH * 2 + boxGap; // match two input boxes + gap

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconSize) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(boxH) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(boxGap) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(boxH) });

        row.IconImage = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "点击上传图标，右键清除",
            Source = _icons.GetUrlPlaceholder()
        };
        // Placeholder is drawn sharp at 128px; Fant keeps edges clean when downscaling
        RenderOptions.SetBitmapScalingMode(row.IconImage, BitmapScalingMode.Fant);
        row.IconImage.MouseLeftButtonDown += (_, e) => e.Handled = true;
        row.IconImage.MouseLeftButtonUp += (_, e) => { PickIcon(row); e.Handled = true; };
        row.IconImage.MouseRightButtonUp += (_, e) =>
        {
            ClearIcon(row);
            e.Handled = true;
        };

        row.UrlBox = MakeBox("https://…");
        row.NameBox = MakeBox("可空");
        var urlField = WatermarkField(row.UrlBox, "地址");
        var nameField = WatermarkField(row.NameBox, "名称");
        row.NameBox.TextChanged += (_, _) =>
        {
            if (row.Suppress) return;
            row.NameTouched = row.NameBox.Text.Trim().Length > 0;
        };
        row.UrlBox.LostFocus += async (_, _) => await TryAutoFillAsync(row);
        row.UrlBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await TryAutoFillAsync(row);
            }
        };

        var remove = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 28,
            Height = 28,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Foreground = _ink,
            Visibility = _editMode ? Visibility.Collapsed : Visibility.Visible,
            ToolTip = "移除此行"
        };
        if (TryFindResource("FlatIconBtn") is Style iconStyle)
            remove.Style = iconStyle;
        remove.Click += (_, _) =>
        {
            if (_rows.Count <= 1) return;
            RowsHost.Children.Remove(row.Root);
            _rows.Remove(row);
        };

        Grid.SetColumn(row.IconImage, 0);
        Grid.SetRow(row.IconImage, 0);
        Grid.SetRowSpan(row.IconImage, 3);

        Grid.SetColumn(urlField, 1);
        Grid.SetRow(urlField, 0);

        Grid.SetColumn(nameField, 1);
        Grid.SetRow(nameField, 2);

        Grid.SetColumn(remove, 2);
        Grid.SetRow(remove, 0);
        Grid.SetRowSpan(remove, 3);

        grid.Children.Add(row.IconImage);
        grid.Children.Add(urlField);
        grid.Children.Add(nameField);
        grid.Children.Add(remove);
        row.Root.Child = grid;

        RowsHost.Children.Add(row.Root);
        _rows.Add(row);
        return row;
    }

    private Grid WatermarkField(TextBox box, string watermark)
    {
        var host = new Grid { Margin = new Thickness(10, 0, 0, 0) };
        var mark = new TextBlock
        {
            Text = watermark,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            FontSize = 13,
            Foreground = TryFindResource("Muted") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A))
        };
        void Sync() => mark.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        host.Children.Add(box);
        host.Children.Add(mark);
        return host;
    }

    private TextBox MakeBox(string tip) => new()
    {
        Height = 30,
        Padding = new Thickness(8, 4, 8, 4),
        BorderBrush = _line,
        BorderThickness = new Thickness(1),
        Background = _fieldBg,
        Foreground = _ink,
        CaretBrush = _ink,
        VerticalContentAlignment = VerticalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tip
    };

    private void AddRow_Click(object sender, RoutedEventArgs e) => AddRow();

    private void PickIcon(RowUi row)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.ico;*.bmp|所有文件|*.*",
            Title = "选择图标"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _icons.SaveUrlIconFromFile(row.CacheKey, dlg.FileName);
            row.PendingPng = File.ReadAllBytes(IconService.UrlIconCachePath(row.CacheKey));
            row.IconTouched = true;
            row.IconImage.Source = _icons.GetUrlIcon(row.CacheKey);
        }
        catch (Exception ex)
        {
            FlatDialog.Info(this, "无法读取图片：\n" + ex.Message);
        }
    }

    private void ClearIcon(RowUi row)
    {
        row.PendingPng = null;
        row.IconTouched = true; // user explicitly cleared — don't auto-fetch over
        row.IconCleared = true;
        _icons.ClearUrlIcon(row.CacheKey);
        row.IconImage.Source = _icons.GetUrlPlaceholder();
    }

    private async Task TryAutoFillAsync(RowUi row)
    {
        var url = UrlFetchService.NormalizeUrl(row.UrlBox.Text);
        if (url.Length == 0) return;
        if (row.Fetching) return;
        row.Fetching = true;
        row.UrlBox.IsEnabled = false;
        try
        {
            var result = await UrlFetchService.FetchAsync(url);
            if (!result.Success) return;

            row.Suppress = true;
            row.UrlBox.Text = result.Url;
            if (!row.NameTouched && !string.IsNullOrWhiteSpace(result.Title))
                row.NameBox.Text = result.Title!;
            row.Suppress = false;

            if (!row.IconTouched && !row.IconCleared && result.IconPng is { Length: > 0 })
            {
                _icons.SaveUrlIcon(row.CacheKey, result.IconPng);
                row.PendingPng = result.IconPng;
                row.IconImage.Source = _icons.GetUrlIcon(row.CacheKey);
            }
        }
        catch
        {
            /* silent — user can fill manually */
        }
        finally
        {
            row.Fetching = false;
            row.UrlBox.IsEnabled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var list = new List<UrlEditResult>();
        foreach (var row in _rows)
        {
            var url = UrlFetchService.NormalizeUrl(row.UrlBox.Text);
            if (url.Length == 0) continue;
            var name = row.NameBox.Text.Trim();
            if (name.Length == 0)
            {
                try { name = new Uri(url).Host; }
                catch { name = url; }
            }

            list.Add(new UrlEditResult(
                _editItemId,
                name,
                url,
                row.CacheKey,
                row.PendingPng,
                row.IconCleared));
        }

        if (list.Count == 0)
        {
            FlatDialog.Info(this, "请至少填写一个有效网址。");
            return;
        }

        Results = list;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class RowUi
    {
        public required Border Root { get; init; }
        public Image IconImage { get; set; } = null!;
        public TextBox NameBox { get; set; } = null!;
        public TextBox UrlBox { get; set; } = null!;
        public string CacheKey { get; set; } = "";
        public byte[]? PendingPng { get; set; }
        public bool NameTouched { get; set; }
        public bool IconTouched { get; set; }
        public bool IconCleared { get; set; }
        public bool Fetching { get; set; }
        public bool Suppress { get; set; }
    }
}

public readonly record struct UrlEditResult(
    string? ExistingId,
    string DisplayName,
    string Url,
    string IconCacheKey,
    byte[]? IconPng,
    bool IconCleared);
