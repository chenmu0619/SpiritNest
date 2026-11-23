using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SpiritNest.Models;
using SpiritNest.Services;

namespace SpiritNest.Views;

public readonly record struct EngineEditResult(
    string? ExistingId,
    string Name,
    string UrlTemplate,
    string CacheKey,
    byte[]? IconPng,
    bool IconCleared);

public partial class EngineEditorWindow : Window
{
    private SolidColorBrush _ink = new(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private SolidColorBrush _line = new(Color.FromRgb(0xE5, 0xE5, 0xEA));
    private SolidColorBrush _fieldBg = new(Colors.White);
    private SolidColorBrush _rowBg = new(Colors.White);
    private readonly IconService _icons = new();
    private readonly List<RowUi> _rows = [];
    private readonly bool _editMode;
    private readonly string? _editId;

    public IReadOnlyList<EngineEditResult> Results { get; private set; } = [];

    public EngineEditorWindow(Window? owner)
    {
        Owner = owner;
        _editMode = false;
        InitializeComponent();
        ScreenDpi.Attach(this);
        GlassChrome.Attach(this);
        ApplySettingsChrome();
        Title = "添加搜索引擎";
        TitleText.Text = "添加搜索引擎";
        Loaded += (_, _) => AlignToOwner();
        AddRow();
    }

    public EngineEditorWindow(Window? owner, SearchEngineInfo existing)
    {
        Owner = owner;
        _editMode = true;
        _editId = existing.Id;
        InitializeComponent();
        ScreenDpi.Attach(this);
        GlassChrome.Attach(this);
        ApplySettingsChrome();
        Title = "编辑搜索引擎";
        TitleText.Text = "编辑搜索引擎";
        AddRowBtn.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => AlignToOwner();

        var row = AddRow();
        row.NameBox.Text = existing.Name;
        row.UrlBox.Text = existing.UrlTemplate;
        row.CacheKey = SearchEngineIcons.CustomKey(existing.Id);
        var custom = IconService.UrlIconCachePath(row.CacheKey);
        if (File.Exists(custom))
        {
            row.IconTouched = true;
            row.PendingPng = File.ReadAllBytes(custom);
            row.IconImage.Source = _icons.GetUrlIcon(row.CacheKey);
        }
        else
        {
            row.IconImage.Source = SearchEngineIcons.Get(existing.Id) ?? _icons.GetUrlPlaceholder();
        }
    }

    private void ApplySettingsChrome()
    {
        if (Owner is not SettingsWindow) return;
        var page = Color.FromRgb(0xF2, 0xF2, 0xF7);
        var ink = Color.FromRgb(0x1A, 0x1A, 0x1A);
        var line = Color.FromArgb(0x1A, 0, 0, 0);
        PaintBrush("Ink", ink);
        PaintBrush("Muted", Color.FromRgb(0x8A, 0x8A, 0x8A));
        PaintBrush("Line", line);
        PaintBrush("Hover", Color.FromArgb(0x0A, 0, 0, 0));
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

    private void AlignToOwner()
    {
        if (Owner is null) return;
        var w = ActualWidth > 1 ? ActualWidth : Width;
        var h = ActualHeight > 1 ? ActualHeight : Height;
        var ow = Owner.ActualWidth > 1 ? Owner.ActualWidth : Owner.Width;
        var oh = Owner.ActualHeight > 1 ? Owner.ActualHeight : Owner.Height;
        var work = DisplayPlacement.FromDip(new Point(Owner.Left + ow / 2, Owner.Top + oh / 2)).WorkArea;
        const double gap = 8;
        var left = Owner.Left + ow + gap;
        if (left + w > work.Right) left = Owner.Left - w - gap;
        left = Math.Clamp(left, work.Left + 4, Math.Max(work.Left + 4, work.Right - w - 4));
        var top = Math.Clamp(Owner.Top, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - h - 4));
        Left = left;
        Top = top;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        GlassChrome.DragFromChrome(this, e, e.OriginalSource as DependencyObject);

    private RowUi AddRow()
    {
        var row = new RowUi
        {
            CacheKey = SearchEngineIcons.CustomKey(Guid.NewGuid().ToString("N")),
            Root = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(8, 6, 6, 6),
                Background = _rowBg,
                CornerRadius = new CornerRadius(6)
            }
        };

        const double boxH = 30;
        const double boxGap = 6;
        var iconSize = boxH * 2 + boxGap;
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
        RenderOptions.SetBitmapScalingMode(row.IconImage, BitmapScalingMode.Fant);
        row.IconImage.MouseLeftButtonDown += (_, e) => e.Handled = true;
        row.IconImage.MouseLeftButtonUp += (_, e) => { PickIcon(row); e.Handled = true; };
        row.IconImage.MouseRightButtonUp += (_, e) => { ClearIcon(row); e.Handled = true; };

        row.UrlBox = MakeBox("https://cn.bing.com/search?q={word}");
        row.NameBox = MakeBox("可空");
        row.NameBox.TextChanged += (_, _) =>
        {
            if (row.Suppress) return;
            row.NameTouched = row.NameBox.Text.Trim().Length > 0;
        };
        row.UrlBox.LostFocus += async (_, _) => await TryFetchAsync(row);
        row.UrlBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await TryFetchAsync(row);
        };
        var urlField = WatermarkField(row.UrlBox, "地址");
        var nameField = WatermarkField(row.NameBox, "名称");

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
        ToolTip = tip
    };

    private void AddRow_Click(object sender, RoutedEventArgs e) => AddRow();

    private async Task TryFetchAsync(RowUi row)
    {
        var needIcon = !row.IconTouched && !row.IconCleared;
        var needName = !row.NameTouched;
        if (row.Fetching || (!needIcon && !needName)) return;
        var probe = SiteOrigin(row.UrlBox.Text);
        if (probe.Length == 0) return;
        row.Fetching = true;
        try
        {
            var result = await UrlFetchService.FetchAsync(probe);
            if (!result.Success) return;
            if (needName && !row.NameTouched)
            {
                var title = CleanSiteName(result.Title);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    row.Suppress = true;
                    row.NameBox.Text = title;
                    row.Suppress = false;
                    row.NameTouched = true;
                }
            }
            if (needIcon && !row.IconTouched && !row.IconCleared && result.IconPng is { Length: > 0 })
            {
                _icons.SaveUrlIcon(row.CacheKey, result.IconPng);
                row.PendingPng = result.IconPng;
                row.IconImage.Source = _icons.GetUrlIcon(row.CacheKey);
            }
        }
        catch
        {
            /* keep the current name and icon when the site cannot be read */
        }
        finally
        {
            row.Fetching = false;
        }
    }

    private static string SiteOrigin(string template)
    {
        var s = template.Trim();
        if (s.Length == 0) return "";
        var q = s.IndexOf('?');
        if (q >= 0) s = s[..q];
        s = s.Replace("{word}", "", StringComparison.OrdinalIgnoreCase)
            .Replace("{q}", "", StringComparison.OrdinalIgnoreCase);
        s = UrlFetchService.NormalizeUrl(s);
        if (s.Length == 0 || !Uri.TryCreate(s, UriKind.Absolute, out var uri)) return "";
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static string? CleanSiteName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title.Trim();
        foreach (var sep in new[] { " - ", " | ", " _ ", " – ", " — ", " · " })
        {
            var i = t.IndexOf(sep, StringComparison.Ordinal);
            if (i is > 0 and <= 20)
            {
                t = t[..i].Trim();
                break;
            }
        }
        if (t.Length > 32) t = t[..32].Trim();
        return t.Length == 0 ? null : t;
    }

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
            row.IconCleared = false;
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
        row.IconTouched = true;
        row.IconCleared = true;
        _icons.ClearUrlIcon(row.CacheKey);
        row.IconImage.Source = _icons.GetUrlPlaceholder();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var list = new List<EngineEditResult>();
        foreach (var row in _rows)
        {
            var url = row.UrlBox.Text.Trim();
            var name = row.NameBox.Text.Trim();
            if (url.Length == 0 && name.Length == 0) continue;
            if (url.Length == 0)
            {
                FlatDialog.Info(this, "请填写搜索地址。");
                return;
            }
            if (!url.Contains("{word}", StringComparison.OrdinalIgnoreCase)
                && !url.Contains("{q}", StringComparison.OrdinalIgnoreCase))
            {
                FlatDialog.Info(this, "地址需要包含 {word}，例如 https://cn.bing.com/search?q={word}");
                return;
            }
            if (name.Length == 0)
            {
                try { name = new Uri(url.Replace("{word}", "a", StringComparison.OrdinalIgnoreCase).Replace("{q}", "a", StringComparison.OrdinalIgnoreCase)).Host; }
                catch { name = "搜索"; }
            }
            list.Add(new EngineEditResult(_editId, name, url, row.CacheKey, row.PendingPng, row.IconCleared));
        }

        if (list.Count == 0)
        {
            FlatDialog.Info(this, "请至少填写一个搜索引擎。");
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
        public bool IconTouched { get; set; }
        public bool IconCleared { get; set; }
        public bool NameTouched { get; set; }
        public bool Fetching { get; set; }
        public bool Suppress { get; set; }
    }
}
