using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpiritNest.Models;
using SpiritNest.Services;

namespace SpiritNest.Views;

public partial class CollectDialog : Window
{
    private readonly CollectService _collect;
    private readonly LibraryStore _library;
    private readonly ObservableCollection<Row> _rows = new();

    public CollectDialog(CollectService collect, LibraryStore library)
    {
        _collect = collect;
        _library = library;
        InitializeComponent();
        ScreenDpi.Attach(this);
        GlassChrome.Attach(this);
        ApplyAppearance();
        FileList.ItemsSource = _rows;

        foreach (var cat in _library.Current.Categories.OrderBy(c => c.SortOrder))
            CategoryBox.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Id });
        if (CategoryBox.Items.Count > 0) CategoryBox.SelectedIndex = 0;

        foreach (var fi in _collect.ScanUserDesktop())
        {
            var ext = fi.Extension.ToLowerInvariant();
            _rows.Add(new Row
            {
                Path = fi.FullName,
                Display = $"{fi.Name}  ({ext})",
                IsChecked = ext is ".lnk" or ".url"
            });
        }
    }

    private void ApplyAppearance()
    {
        var live = (Application.Current as SpiritNest.App)?.CurrentSettings ?? new AppSettings();
        var palette = ThemeService.Resolve(live);
        ThemeService.ApplyResources(Resources, palette, live);
        GlassChrome.ApplyLocalBrushes(Resources, palette);
        GlassChrome.ApplyTheme(this, palette, live, radiusDip: 12);
        if (ShellRoot is not null)
        {
            var rgb = palette.PanelRgb;
            var a = ThemeService.SurfaceFillAlpha(live);
            ShellRoot.Background = new SolidColorBrush(Color.FromArgb(a, rgb.R, rgb.G, rgb.B));
            ShellRoot.BorderBrush = ThemeService.Brush(palette.Border);
            ShellRoot.CornerRadius = new CornerRadius(12);
            CornerClip.Attach(ShellRoot, 12);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsChecked = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsChecked = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryBox.SelectedItem is not ComboBoxItem { Tag: string catId })
        {
            FlatDialog.Info(this, "请选择分类。");
            return;
        }

        var selected = _rows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0)
        {
            FlatDialog.Info(this, "未勾选任何项，无变更。");
            DialogResult = false;
            return;
        }

        var ok = 0;
        var fail = 0;
        var errors = new List<string>();
        foreach (var row in selected)
        {
            var r = _collect.CollectFile(row.Path, catId);
            if (r.Success) ok++;
            else
            {
                fail++;
                if (r.Error != null) errors.Add($"{System.IO.Path.GetFileName(row.Path)}: {r.Error}");
            }
        }

        FlatDialog.Info(this,
            $"成功 {ok}，失败 {fail}" +
            (errors.Count > 0 ? "\n\n" + string.Join("\n", errors.Take(8)) : ""),
            "一键收纳完成");
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        GlassChrome.DragFromChrome(this, e, e.OriginalSource as DependencyObject);

    private sealed class Row : INotifyPropertyChanged
    {
        private bool _isChecked;
        public string Path { get; set; } = "";
        public string Display { get; set; } = "";
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
