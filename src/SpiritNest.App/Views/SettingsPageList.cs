using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpiritNest.Models;
using SpiritNest.Services;
using SpiritNest.ViewModels;

namespace SpiritNest.Views;

public partial class SettingsWindow
{
    private enum PageDragKind { None, Page, Icon }

    private sealed class IconCell
    {
        public required string ItemId { get; init; }
        public required ItemInfo Item { get; init; }
        public required FrameworkElement Root { get; init; }
        public required Border Chrome { get; init; }
        public required Border Marker { get; init; }
        public required CheckBox Pick { get; init; }
        public ItemVm? Vm { get; init; }
        public Image? ShellImage { get; init; }
    }

    private sealed class PageRowUi
    {
        public required string Id { get; init; }
        public required Border Root { get; init; }
        public required Border InsertBefore { get; init; }
        public required Grid Header { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBox NameEdit { get; init; }
        public required TextBlock Chevron { get; init; }
        public required UniformGrid Icons { get; init; }
        public required Border InsertEnd { get; init; }
        public required List<IconCell> Cells { get; init; }
        public required CheckBox Pick { get; init; }
        public required Border IconGutter { get; init; }
    }

    private readonly IconService _pageIcons = new();
    private readonly List<PageRowUi> _pageRows = new();
    private readonly HashSet<string> _expandedPages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedItemIds = new(StringComparer.Ordinal);
    private const string DragIdsFormat = "SpiritNestItemIds";
    private bool _pressCtrl;
    private bool _marqueeDown;
    private bool _marqueeActive;
    private bool _marqueeCtrl;
    private Point _marqueeOrigin;
    private string? _marqueePageId;
    private Grid? _marqueeHost;
    private Border? _marquee;
    private HashSet<string>? _marqueeBase;
    private bool _dropHandledInside;
    private readonly List<string> _tempDragFiles = new();
    private string? _oleDropPage;
    private int _oleDropIndex = -1;
    private DispatcherTimer? _edgeScrollTimer;
    private bool _buildingPages;
    private bool _pressing;
    private PageDragKind _drag = PageDragKind.None;
    private Point _pressPoint;
    private string? _pressPageId;
    private string? _pressItemId;
    private string? _inlineNewId;
    private int _iconInsert = -1;
    private string? _iconInsertPageId;
    private string? _pageInsertTargetId;
    private bool _pageInsertBefore;
    private Border? _pageInsertTail;
    private bool _deletePick;
    private bool _syncingPicks;
    private readonly HashSet<string> _deletePageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleteItemIds = new(StringComparer.Ordinal);
    private HashSet<string>? _expandBeforeDelete;

    private void RebuildPageList(bool scrollToEnd = false, string? editId = null)
    {
        if (PageListHost is null) return;
        _buildingPages = true;
        CancelDragVisuals();
        PageListHost.Children.Clear();
        _pageRows.Clear();

        foreach (var cat in _library.Current.Categories.OrderBy(c => c.SortOrder))
            PageListHost.Children.Add(BuildPageRow(cat).Root);

        _pageInsertTail = new Border
        {
            Height = 2,
            Margin = new Thickness(12, 0, 12, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x9D, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        PageListHost.Children.Add(_pageInsertTail);

        _deletePageIds.RemoveWhere(id => _library.FindCategory(id) is null);
        _deleteItemIds.RemoveWhere(id => _library.Current.Items.All(i => i.Id != id));
        _buildingPages = false;
        ApplyExpandedVisuals();
        UpdateDeleteBar();

        if (editId is not null)
        {
            var row = _pageRows.FirstOrDefault(r => r.Id == editId);
            if (row is not null) OpenNameEdit(row, selectAll: true);
        }

        if (scrollToEnd || editId is not null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                PageScroll.UpdateLayout();
                if (scrollToEnd)
                    PageScroll.ScrollToVerticalOffset(PageScroll.ScrollableHeight);
                if (editId is not null)
                {
                    var row = _pageRows.FirstOrDefault(r => r.Id == editId);
                    row?.NameEdit.Focus();
                    row?.NameEdit.SelectAll();
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private PageRowUi BuildPageRow(CategoryInfo cat)
    {
        var insertBefore = new Border
        {
            Height = 2,
            Margin = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x9D, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        var chevron = new TextBlock
        {
            Text = "+",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)FindResource("Ink"),
            Margin = new Thickness(0, -2, 0, 0)
        };
        var chevronHit = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 8, 0),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA)),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = chevron,
            ToolTip = "展开"
        };

        var name = new TextBlock
        {
            Text = string.IsNullOrEmpty(cat.Name) ? "未命名" : cat.Name,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Ink"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var edit = new TextBox
        {
            Text = cat.Name,
            FontSize = 14,
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("Ink"),
            CaretBrush = (Brush)FindResource("Ink"),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };

        var nameHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        nameHost.Children.Add(name);
        nameHost.Children.Add(edit);

        var visible = new CheckBox
        {
            Style = (Style)FindResource("InlineSwitch"),
            IsChecked = cat.Visible,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "在面板中显示"
        };
        var catId = cat.Id;
        visible.Checked += (_, _) => SetPageVisible(catId, true);
        visible.Unchecked += (_, _) => SetPageVisible(catId, false);

        var pagePick = new CheckBox
        {
            Style = (Style)FindResource("PickCheck"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
            Visibility = _deletePick ? Visibility.Visible : Visibility.Collapsed,
            IsChecked = _deletePageIds.Contains(catId)
        };
        pagePick.Checked += (_, _) =>
        {
            if (_buildingPages || _syncingPicks || !_deletePick) return;
            _deletePageIds.Add(catId);
            SetPageIconsPicked(catId, picked: true);
            UpdateDeleteBar();
        };
        pagePick.Unchecked += (_, _) =>
        {
            if (_buildingPages || _syncingPicks || !_deletePick) return;
            _deletePageIds.Remove(catId);
            SetPageIconsPicked(catId, picked: false);
            UpdateDeleteBar();
        };

        var handle = BuildDragHandle();
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            ArmPress(PageDragKind.Page, catId, null, e);
        };

        var header = new Grid();
        header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(handle, 0);
        Grid.SetColumn(chevronHit, 1);
        Grid.SetColumn(nameHost, 2);
        Grid.SetColumn(visible, 3);
        Grid.SetColumn(pagePick, 4);
        header.Children.Add(handle);
        header.Children.Add(chevronHit);
        header.Children.Add(nameHost);
        header.Children.Add(visible);
        header.Children.Add(pagePick);

        var icons = new UniformGrid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var cells = new List<IconCell>();
        foreach (var item in _library.Current.Items.Where(i => i.CategoryId == cat.Id).OrderBy(i => i.SortOrder))
        {
            var cell = BuildIconCell(item);
            cells.Add(cell);
            icons.Children.Add(cell.Root);
        }
        var iconGutter = new Border
        {
            Background = Brushes.Transparent,
            MinHeight = 44
        };
        Grid.SetRow(iconGutter, 1);
        Grid.SetColumn(iconGutter, 0);
        Grid.SetColumnSpan(iconGutter, 5);
        iconGutter.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            ShowAddUrlMenu(cat.Id);
            e.Handled = true;
        };
        header.Children.Add(iconGutter);

        var iconHost = new Grid { MinHeight = 44, Margin = new Thickness(0, 0, 0, 6) };
        iconHost.Children.Add(icons);
        Grid.SetRow(iconHost, 1);
        Grid.SetColumn(iconHost, 2);
        Grid.SetColumnSpan(iconHost, 2);
        header.Children.Add(iconHost);
        var count = cells.Count;
        iconHost.SizeChanged += (_, e) => FitIconGrid(icons, e.NewSize.Width, count);

        var insertEnd = new Border
        {
            Width = 3,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x9D, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        iconHost.Children.Add(insertEnd);

        var row = new PageRowUi
        {
            Id = cat.Id,
            Root = new Border { Background = Brushes.Transparent },
            InsertBefore = insertBefore,
            Header = header,
            NameText = name,
            NameEdit = edit,
            Chevron = chevron,
            Icons = icons,
            InsertEnd = insertEnd,
            Cells = cells,
            Pick = pagePick,
            IconGutter = iconGutter
        };

        var body = new StackPanel();
        body.Children.Add(insertBefore);
        body.Children.Add(header);
        body.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(12, 0, 12, 0),
            Background = (Brush)FindResource("Line")
        });
        row.Root.Child = body;

        chevronHit.MouseLeftButtonDown += (_, e) =>
        {
            if (_drag != PageDragKind.None) return;
            if (_expandedPages.Contains(cat.Id)) _expandedPages.Remove(cat.Id);
            else _expandedPages.Add(cat.Id);
            ApplyExpandedVisuals();
            e.Handled = true;
        };
        name.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                OpenNameEdit(row, selectAll: true);
                e.Handled = true;
            }
        };
        edit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitNameEdit(row, cancel: false);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CommitNameEdit(row, cancel: true);
                e.Handled = true;
            }
        };
        edit.LostFocus += (_, _) => CommitNameEdit(row, cancel: false);

        foreach (var cell in cells)
        {
            cell.Root.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject src && IsDescendantOf(src, cell.Pick))
                    return;
                if (e.ClickCount >= 2)
                {
                    _pressing = false;
                    _drag = PageDragKind.None;
                    if (PageScroll.IsMouseCaptured) PageScroll.ReleaseMouseCapture();
                    EditItem(cell.Item);
                    e.Handled = true;
                    return;
                }
                ArmPress(PageDragKind.Icon, cat.Id, cell.ItemId, e);
            };
        }

        icons.Visibility = Visibility.Collapsed;
        iconHost.Visibility = Visibility.Collapsed;
        iconHost.Background = Brushes.Transparent;
        iconHost.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (CellFromSource(e.OriginalSource as DependencyObject) is not null) return;
            BeginMarquee(iconHost, cat.Id, e);
        };
        iconHost.PreviewMouseMove += (_, e) => UpdateMarquee(iconHost, e);
        iconHost.PreviewMouseLeftButtonUp += (_, e) => EndMarquee(iconHost, e);
        iconHost.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            ShowAddUrlMenu(cat.Id);
            e.Handled = true;
        };
        row.Root.AllowDrop = true;
        row.Root.DragOver += Row_DragOver;
        row.Root.Drop += Row_Drop;
        _pageRows.Add(row);
        PaintSelection();
        return row;
    }

    private static Border BuildDragHandle()
    {
        var grid = new Grid { Width = 14, Height = 18, HorizontalAlignment = HorizontalAlignment.Center };
        for (var c = 0; c < 2; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < 3; r++)
            grid.RowDefinitions.Add(new RowDefinition());
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 2; c++)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 3.5,
                    Height = 3.5,
                    Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(dot, r);
                Grid.SetColumn(dot, c);
                grid.Children.Add(dot);
            }
        }

        return new Border
        {
            Width = 28,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = grid,
            ToolTip = "拖动排序"
        };
    }

    private IconCell BuildIconCell(ItemInfo item)
    {
        var marker = new Border
        {
            Width = 3,
            Margin = new Thickness(0, 4, 2, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x9D, 0xFF)),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var vm = new ItemVm(item);
        vm.LoadIcon(_pageIcons);
        Image? shellImage = null;
        FrameworkElement glyph;
        if (ShellPlaces.IsShell(item))
        {
            shellImage = new Image
            {
                Width = 32,
                Height = 32,
                Stretch = Stretch.Uniform,
                Source = vm.Icon
            };
            glyph = shellImage;
        }
        else
        {
            glyph = vm.Icon is BitmapImage img
                ? new Image { Source = img, Width = 32, Height = 32, Stretch = Stretch.Uniform }
                : new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA)),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(item.DisplayName) ? "·" : item.DisplayName[..1],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12
                }
            };
        }
        var label = new TextBlock
        {
            Text = string.IsNullOrEmpty(item.DisplayName) ? "·" : item.DisplayName,
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 64,
            Margin = new Thickness(0, 1, 0, 0),
            Foreground = (Brush)FindResource("Ink")
        };
        var iconBox = new Grid { Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center };
        iconBox.Children.Add(glyph);
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(iconBox);
        stack.Children.Add(label);
        var chrome = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(2),
            Padding = new Thickness(2),
            Cursor = Cursors.SizeAll,
            ToolTip = label.Text,
            Child = stack
        };
        marker.HorizontalAlignment = HorizontalAlignment.Left;
        marker.Width = 3;
        marker.Margin = new Thickness(0);
        var grid = new Grid { ToolTip = label.Text };
        var pick = new CheckBox
        {
            Style = (Style)FindResource("PickCheck"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 0),
            Visibility = _deletePick ? Visibility.Visible : Visibility.Collapsed,
            IsChecked = _deleteItemIds.Contains(item.Id)
        };
        Panel.SetZIndex(pick, 2);
        var itemId = item.Id;
        pick.Checked += (_, _) =>
        {
            if (_buildingPages || _syncingPicks || !_deletePick) return;
            _deleteItemIds.Add(itemId);
            SyncPagePickFromIcons(item.CategoryId);
            UpdateDeleteBar();
        };
        pick.Unchecked += (_, _) =>
        {
            if (_buildingPages || _syncingPicks || !_deletePick) return;
            _deleteItemIds.Remove(itemId);
            SyncPagePickFromIcons(item.CategoryId);
            UpdateDeleteBar();
        };
        grid.Children.Add(chrome);
        grid.Children.Add(marker);
        grid.Children.Add(pick);
        chrome.MouseEnter += (_, _) =>
        {
            if (!_selectedItemIds.Contains(item.Id))
                chrome.Background = new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0));
        };
        chrome.MouseLeave += (_, _) => PaintSelection();
        chrome.MouseRightButtonUp += (_, e) =>
        {
            ShowItemMenu(item);
            e.Handled = true;
        };
        return new IconCell { ItemId = item.Id, Item = item, Root = grid, Chrome = chrome, Marker = marker, Pick = pick, Vm = vm, ShellImage = shellImage };
    }

    private void RefreshRecycleIcons()
    {
        foreach (var row in _pageRows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.ShellImage is null || cell.Vm is null || !ShellPlaces.IsRecycleBin(cell.Item))
                    continue;
                cell.Vm.LoadIcon(_pageIcons);
                cell.ShellImage.Source = cell.Vm.Icon;
            }
        }
    }

    private void SetPageVisible(string id, bool visible)
    {
        if (!_ready || _buildingPages) return;
        var cat = _library.FindCategory(id);
        if (cat is null || cat.Visible == visible) return;
        cat.Visible = visible;
        _library.Save();
        LibraryChanged?.Invoke();
    }

    private void AddPageInline()
    {
        if (_inlineNewId is not null && _library.FindCategory(_inlineNewId) is not null)
        {
            RebuildPageList(scrollToEnd: true, editId: _inlineNewId);
            return;
        }

        var max = _library.Current.Categories.Count == 0
            ? 0
            : _library.Current.Categories.Max(c => c.SortOrder) + 1;
        var cat = new CategoryInfo { Name = "", SortOrder = max, Visible = true };
        _library.Current.Categories.Add(cat);
        _library.Save();
        _inlineNewId = cat.Id;
        LibraryChanged?.Invoke();
        RebuildPageList(scrollToEnd: true, editId: cat.Id);
    }

    private void OpenNameEdit(PageRowUi row, bool selectAll)
    {
        row.NameText.Visibility = Visibility.Collapsed;
        row.NameEdit.Text = _library.FindCategory(row.Id)?.Name ?? "";
        row.NameEdit.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            row.NameEdit.Focus();
            if (selectAll) row.NameEdit.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CommitNameEdit(PageRowUi row, bool cancel)
    {
        if (row.NameEdit.Visibility != Visibility.Visible) return;
        var cat = _library.FindCategory(row.Id);
        if (cat is null) return;
        var typed = row.NameEdit.Text.Trim();
        var isNew = row.Id == _inlineNewId;

        if (cancel || string.IsNullOrWhiteSpace(typed))
        {
            if (isNew)
            {
                _library.Current.Categories.Remove(cat);
                _library.Save();
                _inlineNewId = null;
                _expandedPages.Remove(row.Id);
                LibraryChanged?.Invoke();
                RebuildPageList();
                return;
            }

            row.NameEdit.Visibility = Visibility.Collapsed;
            row.NameText.Visibility = Visibility.Visible;
            row.NameText.Text = cat.Name;
            return;
        }

        if (!string.Equals(cat.Name, typed, StringComparison.Ordinal))
        {
            if (!_library.RenameCategory(row.Id, typed))
            {
                row.NameEdit.Focus();
                return;
            }
            LibraryChanged?.Invoke();
        }
        else if (isNew)
        {
            cat.Name = typed;
            _library.Save();
        }

        if (isNew) _inlineNewId = null;
        row.NameText.Text = typed;
        row.NameEdit.Visibility = Visibility.Collapsed;
        row.NameText.Visibility = Visibility.Visible;
    }

    private void ApplyExpandedVisuals()
    {
        foreach (var row in _pageRows)
        {
            var open = _expandedPages.Contains(row.Id);
            var shown = open ? Visibility.Visible : Visibility.Collapsed;
            row.Icons.Visibility = shown;
            if (row.Icons.Parent is UIElement host)
                host.Visibility = shown;
            row.IconGutter.Visibility = shown;
            row.Chevron.Text = open ? "−" : "+";
            if (row.Chevron.Parent is FrameworkElement toggle)
                toggle.ToolTip = open ? "收起" : "展开";
        }
    }

    private void ArmPress(PageDragKind kind, string pageId, string? itemId, MouseButtonEventArgs e)
    {
        if (_drag != PageDragKind.None) return;
        _pressing = true;
        _pressCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        _pressPoint = e.GetPosition(PageScroll);
        _pressPageId = pageId;
        _pressItemId = itemId;
        _drag = kind;
        PageScroll.CaptureMouse();
        e.Handled = true;
    }

    private void PageScroll_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressing && _drag == PageDragKind.None) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishDrag(commit: false);
            return;
        }

        var p = e.GetPosition(PageScroll);
        if (_pressing && (p - _pressPoint).Length < 6) return;
        if (_pressing)
        {
            _pressing = false;
            if (_drag == PageDragKind.Page)
            {
                foreach (var row in _pageRows)
                {
                    row.Icons.Visibility = Visibility.Collapsed;
                    if (row.Icons.Parent is UIElement host)
                        host.Visibility = Visibility.Collapsed;
                    row.IconGutter.Visibility = Visibility.Collapsed;
                    row.Chevron.Text = "+";
                    if (row.Chevron.Parent is FrameworkElement toggle)
                        toggle.ToolTip = "展开";
                }
                var src = _pageRows.FirstOrDefault(r => r.Id == _pressPageId);
                if (src is not null) src.Root.Opacity = 0.45;
            }
            else if (_drag == PageDragKind.Icon && _pressPageId is not null && _pressItemId is not null)
            {
                BeginOleIconDrag(_pressPageId, _pressItemId);
                return;
            }
            EnsureEdgeTimer();
        }

        if (_drag == PageDragKind.Page) UpdatePageDrag(p);
        else if (_drag == PageDragKind.Icon) UpdateIconDrag(p);
    }

    private void PageScroll_MouseUp(object sender, MouseButtonEventArgs e) => FinishDrag(commit: true);

    private void PageScroll_LostCapture(object sender, MouseEventArgs e)
    {
        if (_pressing || _drag != PageDragKind.None)
            FinishDrag(commit: false);
    }

    private void FinishDrag(bool commit)
    {
        var kind = _drag;
        var pageId = _pressPageId;
        var itemId = _pressItemId;
        var ctrl = _pressCtrl;
        var targetPage = _pageInsertTargetId;
        var before = _pageInsertBefore;
        var iconPage = _iconInsertPageId;
        var iconAt = _iconInsert;
        _pressing = false;
        _drag = PageDragKind.None;
        _pressPageId = null;
        _pressItemId = null;
        _edgeScrollTimer?.Stop();
        ClearInserts();
        foreach (var row in _pageRows)
        {
            row.Root.Opacity = 1;
            foreach (var cell in row.Cells)
                cell.Root.Opacity = 1;
        }
        if (PageScroll.IsMouseCaptured) PageScroll.ReleaseMouseCapture();
        if (!commit || kind == PageDragKind.None || pageId is null) 
        {
            ApplyExpandedVisuals();
            return;
        }

        if (kind == PageDragKind.Page && targetPage is not null)
            DropPage(pageId, targetPage, before);
        else if (kind == PageDragKind.Icon && itemId is not null && iconAt < 0)
        {
            if (ctrl)
            {
                if (!_selectedItemIds.Add(itemId))
                    _selectedItemIds.Remove(itemId);
            }
            else
            {
                _selectedItemIds.Clear();
                _selectedItemIds.Add(itemId);
            }
            PaintSelection();
        }
        else
            ApplyExpandedVisuals();
    }

    private void UpdatePageDrag(Point viewportPoint)
    {
        ClearInserts();
        var hit = HitPage(viewportPoint);
        if (hit is null)
        {
            _pageInsertTargetId = _pageRows.Count > 0 ? _pageRows[^1].Id : null;
            _pageInsertBefore = false;
            if (_pageRows.Count > 0)
                _pageRows[^1].InsertBefore.Visibility = Visibility.Collapsed;
            return;
        }

        var top = hit.Root.TranslatePoint(new Point(0, 0), PageScroll).Y;
        var before = viewportPoint.Y < top + hit.Root.ActualHeight / 2;
        _pageInsertTargetId = hit.Id;
        _pageInsertBefore = before;
        if (before)
            hit.InsertBefore.Visibility = Visibility.Visible;
        else
        {
            var idx = _pageRows.FindIndex(r => r.Id == hit.Id);
            if (idx >= 0 && idx < _pageRows.Count - 1)
                _pageRows[idx + 1].InsertBefore.Visibility = Visibility.Visible;
            else if (_pageInsertTail is not null)
                _pageInsertTail.Visibility = Visibility.Visible;
        }
    }

    private void UpdateIconDrag(Point viewportPoint)
    {
        var hit = HitPage(viewportPoint);
        if (hit is null) return;
        if (!_expandedPages.Contains(hit.Id) || _expandedPages.Count != 1)
        {
            _expandedPages.Clear();
            _expandedPages.Add(hit.Id);
            ApplyExpandedVisuals();
            PageScroll.UpdateLayout();
        }

        ClearInserts();
        var local = Mouse.GetPosition(hit.Icons);
        var at = hit.Cells.Count;
        if (hit.Cells.Count > 0)
        {
            var best = 0;
            var bestD = double.MaxValue;
            double maxBottom = 0;
            for (var i = 0; i < hit.Cells.Count; i++)
            {
                var cell = hit.Cells[i].Root;
                var origin = cell.TranslatePoint(new Point(0, 0), hit.Icons);
                var center = new Point(origin.X + cell.ActualWidth / 2, origin.Y + cell.ActualHeight / 2);
                var d = (local - center).LengthSquared;
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
                maxBottom = Math.Max(maxBottom, origin.Y + cell.ActualHeight);
            }
            if (local.Y > maxBottom + 10)
                at = hit.Cells.Count;
            else
            {
                var chosen = hit.Cells[best].Root;
                var origin = chosen.TranslatePoint(new Point(0, 0), hit.Icons);
                at = local.X >= origin.X + chosen.ActualWidth / 2 ? best + 1 : best;
            }
        }

        _iconInsertPageId = hit.Id;
        _iconInsert = at;
        if (at >= 0 && at < hit.Cells.Count)
            hit.Cells[at].Marker.Visibility = Visibility.Visible;
        else
            ShowEndMarker(hit);
    }

    private PageRowUi? HitPage(Point viewportPoint)
    {
        PageRowUi? found = null;
        foreach (var row in _pageRows)
        {
            var top = row.Root.TranslatePoint(new Point(0, 0), PageScroll).Y;
            if (viewportPoint.Y >= top && viewportPoint.Y < top + row.Root.ActualHeight)
                found = row;
        }
        return found;
    }

    private void DropPage(string movingId, string targetId, bool before)
    {
        var ordered = _library.Current.Categories.OrderBy(c => c.SortOrder).ToList();
        var from = ordered.FindIndex(c => c.Id == movingId);
        var to = ordered.FindIndex(c => c.Id == targetId);
        if (from < 0 || to < 0) return;
        if (!before) to++;
        if (from < to) to--;
        if (from == to)
        {
            ApplyExpandedVisuals();
            return;
        }

        var moving = ordered[from];
        ordered.RemoveAt(from);
        ordered.Insert(Math.Clamp(to, 0, ordered.Count), moving);
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;
        _library.Save();
        LibraryChanged?.Invoke();
        RebuildPageList();
    }

    private void DropIcon(string itemId, string fromPageId, string toPageId, int visualIndex)
    {
        var fromItems = _library.Current.Items
            .Where(i => i.CategoryId == fromPageId)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.Id)
            .ToList();
        var from = fromItems.IndexOf(itemId);
        var at = visualIndex;
        if (fromPageId == toPageId)
        {
            if (from >= 0 && (visualIndex == from || visualIndex == from + 1))
            {
                ApplyExpandedVisuals();
                return;
            }
            if (from >= 0 && from < visualIndex) at = visualIndex - 1;
        }

        _expandedPages.Clear();
        _expandedPages.Add(toPageId);
        _library.MoveItemsAt([itemId], toPageId, at);
        LibraryChanged?.Invoke();
        RebuildPageList();
    }

    private IconCell? FindCell(string? pageId, string? itemId)
    {
        var row = _pageRows.FirstOrDefault(r => r.Id == pageId);
        return row?.Cells.FirstOrDefault(c => c.ItemId == itemId);
    }

    private void ClearInserts()
    {
        if (_pageInsertTail is not null)
            _pageInsertTail.Visibility = Visibility.Collapsed;
        foreach (var row in _pageRows)
        {
            row.InsertBefore.Visibility = Visibility.Collapsed;
            row.InsertEnd.Visibility = Visibility.Collapsed;
            foreach (var cell in row.Cells)
                cell.Marker.Visibility = Visibility.Collapsed;
        }
        _pageInsertTargetId = null;
        _iconInsertPageId = null;
        _iconInsert = -1;
    }

    private void CancelDragVisuals()
    {
        _pressing = false;
        _drag = PageDragKind.None;
        _edgeScrollTimer?.Stop();
    }

    private void EnsureEdgeTimer()
    {
        _edgeScrollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _edgeScrollTimer.Tick -= EdgeScrollTick;
        _edgeScrollTimer.Tick += EdgeScrollTick;
        _edgeScrollTimer.Start();
    }

    private void EdgeScrollTick(object? sender, EventArgs e)
    {
        if (_drag == PageDragKind.None)
        {
            _edgeScrollTimer?.Stop();
            return;
        }

        var p = Mouse.GetPosition(PageScroll);
        const double edge = 36;
        var offset = PageScroll.VerticalOffset;
        if (p.Y < edge)
            PageScroll.ScrollToVerticalOffset(Math.Max(0, offset - 14));
        else if (p.Y > PageScroll.ViewportHeight - edge)
            PageScroll.ScrollToVerticalOffset(offset + 14);

        if (_drag == PageDragKind.Page) UpdatePageDrag(p);
        else if (_drag == PageDragKind.Icon) UpdateIconDrag(p);
    }

    /// <summary>Same column rule as the notch panel: &lt;6 one row, 6–12 two even rows, else 6 per row.</summary>
    private static int ResolveIconColumns(int count)
    {
        if (count <= 0) return 6;
        if (count < 6) return count;
        if (count <= 12) return (int)Math.Ceiling(count / 2.0);
        return 6;
    }

    private static void FitIconGrid(UniformGrid icons, double avail, int count)
    {
        if (avail < 8) return;
        var cols = ResolveIconColumns(count);
        icons.Columns = Math.Max(1, cols);
        icons.Rows = count is >= 6 and <= 12 ? 2 : 0;
        icons.HorizontalAlignment = HorizontalAlignment.Left;
        icons.Width = count == 0 ? avail : Math.Min(avail, cols * 64);
        icons.MinHeight = count == 0 ? 44 : 0;
    }

    private static bool IsShortcutOrUrl(ItemInfo item) =>
        !string.Equals(item.ItemKind, "Widget", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(item.StorageMode, "Widget", StringComparison.OrdinalIgnoreCase);

    private void PaintSelection()
    {
        var on = new SolidColorBrush(Color.FromArgb(0x28, 0x4F, 0x9D, 0xFF));
        on.Freeze();
        foreach (var row in _pageRows)
        {
            foreach (var cell in row.Cells)
                cell.Chrome.Background = _selectedItemIds.Contains(cell.ItemId) ? on : Brushes.Transparent;
        }
    }

    private void PageDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!_deletePick)
        {
            _deletePick = true;
            _expandBeforeDelete = new HashSet<string>(_expandedPages, StringComparer.Ordinal);
            foreach (var row in _pageRows)
                _expandedPages.Add(row.Id);
            ApplyExpandedVisuals();
            ApplyDeletePickVisuals();
            UpdateDeleteBar();
            return;
        }

        if (_deletePageIds.Count + _deleteItemIds.Count >= 1)
            CommitDeletePick();
    }

    private void PageDeleteCancel_Click(object sender, RoutedEventArgs e) => ExitDeletePick();

    private void ApplyDeletePickVisuals()
    {
        var vis = _deletePick ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in _pageRows)
        {
            row.Pick.Visibility = vis;
            if (!_deletePick && row.Pick.IsChecked == true)
                row.Pick.IsChecked = false;
            foreach (var cell in row.Cells)
            {
                cell.Pick.Visibility = vis;
                if (!_deletePick && cell.Pick.IsChecked == true)
                    cell.Pick.IsChecked = false;
            }
        }
    }

    private void UpdateDeleteBar()
    {
        if (PageDeleteBtn is null || PageDeleteCancelBtn is null) return;
        var picked = _deletePageIds.Count + _deleteItemIds.Count > 0;
        PageDeleteBtn.Content = picked ? "确认删除" : "删除";
        PageDeleteBtn.Style = (Style)FindResource(picked ? "WarnBtn" : "FlatBtn");
        PageDeleteCancelBtn.Visibility = _deletePick ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExitDeletePick()
    {
        _deletePick = false;
        _deletePageIds.Clear();
        _deleteItemIds.Clear();
        if (_expandBeforeDelete is not null)
        {
            _expandedPages.Clear();
            foreach (var id in _expandBeforeDelete)
            {
                if (_library.FindCategory(id) is not null)
                    _expandedPages.Add(id);
            }
            _expandBeforeDelete = null;
        }
        ApplyDeletePickVisuals();
        ApplyExpandedVisuals();
        UpdateDeleteBar();
    }

    private void CommitDeletePick()
    {
        var pages = _deletePageIds.Where(id => _library.FindCategory(id) is not null).ToList();
        if (pages.Count > 0 && _library.Current.Categories.Count - pages.Count < 1)
        {
            FlatDialog.Info(this, "至少保留一个页面。");
            return;
        }

        var items = ItemsPendingDelete(pages);
        var shortcuts = items.Where(IsShortcut).ToList();
        var urls = items.Where(IsUrlItem).ToList();
        if (shortcuts.Count + urls.Count > 0
            && !FlatDialog.Confirm(this, BuildDeleteAsk(shortcuts.Count, urls.Count), "删除", "删除", "取消", warn: true))
            return;

        var restore = false;
        if (shortcuts.Count > 0)
        {
            switch (AskShortcutFate("是否将快捷图标还原到原来的位置？"))
            {
                case ShortcutFate.Cancel:
                    return;
                case ShortcutFate.Restore:
                    restore = true;
                    break;
            }
        }

        foreach (var item in items)
        {
            if (restore && IsShortcut(item))
                RestoreOrRemove(item);
            else
                RemoveItem(item);
        }
        foreach (var id in pages)
            RemovePage(id);

        ExitDeletePick();
        LibraryChanged?.Invoke();
        RebuildPageList();
    }

    private void SetPageIconsPicked(string pageId, bool picked)
    {
        var row = _pageRows.FirstOrDefault(r => r.Id == pageId);
        if (row is null) return;
        _syncingPicks = true;
        foreach (var cell in row.Cells)
        {
            if (picked) _deleteItemIds.Add(cell.ItemId);
            else _deleteItemIds.Remove(cell.ItemId);
            if (cell.Pick.IsChecked != picked)
                cell.Pick.IsChecked = picked;
        }
        _syncingPicks = false;
    }

    private void SyncPagePickFromIcons(string pageId)
    {
        var row = _pageRows.FirstOrDefault(r => r.Id == pageId);
        if (row is null || row.Cells.Count == 0) return;
        var all = row.Cells.All(c => _deleteItemIds.Contains(c.ItemId));
        if (all) _deletePageIds.Add(pageId);
        else _deletePageIds.Remove(pageId);
        _syncingPicks = true;
        if (row.Pick.IsChecked != all)
            row.Pick.IsChecked = all;
        _syncingPicks = false;
    }

    private List<ItemInfo> ItemsPendingDelete(IReadOnlyCollection<string> pageIds)
    {
        var pages = pageIds.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ItemInfo>();
        foreach (var item in _library.Current.Items)
        {
            if (!pages.Contains(item.CategoryId) && !_deleteItemIds.Contains(item.Id)) continue;
            if (!seen.Add(item.Id)) continue;
            list.Add(item);
        }
        return list;
    }

    private static string BuildDeleteAsk(int shortcuts, int urls)
    {
        if (shortcuts > 0 && urls > 0)
            return $"将删除 {shortcuts} 个快捷图标和 {urls} 个网址，是否继续？";
        if (shortcuts > 0)
            return $"将删除 {shortcuts} 个快捷图标，是否继续？";
        return $"将删除 {urls} 个网址，是否继续？";
    }

    private enum ShortcutFate { Cancel, Discard, Restore }

    private ShortcutFate AskShortcutFate(string message)
    {
        var choice = FlatDialog.Ask(this, "还原快捷图标", message,
            new FlatDialog.Choice("取消"),
            new FlatDialog.Choice("不还原"),
            new FlatDialog.Choice("还原", Primary: true));
        return choice switch
        {
            1 => ShortcutFate.Discard,
            2 => ShortcutFate.Restore,
            _ => ShortcutFate.Cancel
        };
    }

    private static bool IsShortcut(ItemInfo item) =>
        IsShortcutOrUrl(item) && !IsUrlItem(item) && !ShellPlaces.IsShell(item);

    private void RestoreOrRemove(ItemInfo item)
    {
        _selectedItemIds.Remove(item.Id);
        _deleteItemIds.Remove(item.Id);
        var restored = _collect.RestoreToOriginal(item);
        if (!restored.Success)
            _collect.RemoveFromNest(item, deleteStoredFile: true);
    }

    private void RemovePage(string id)
    {
        foreach (var item in _library.Current.Items.Where(i => i.CategoryId == id).ToList())
            RemoveItem(item);
        if (!_library.DeleteCategory(id, null, out var error))
        {
            FlatDialog.Info(this, string.IsNullOrWhiteSpace(error) ? "无法删除该页面。" : error);
            return;
        }
        _expandedPages.Remove(id);
        _expandBeforeDelete?.Remove(id);
        _deletePageIds.Remove(id);
        if (_inlineNewId == id) _inlineNewId = null;
    }

    private void RemoveItem(ItemInfo item)
    {
        if (IsUrlItem(item))
            _pageIcons.ClearUrlIcon(item.IconCacheKey ?? item.Id);
        _selectedItemIds.Remove(item.Id);
        _deleteItemIds.Remove(item.Id);
        _collect.RemoveFromNest(item, deleteStoredFile: true);
        ForgetShellToggle(item);
    }

    private void ForgetShellToggle(ItemInfo item)
    {
        if (ShellPlaces.IsThisPc(item)) _settings.Current.ShowThisPc = false;
        else if (ShellPlaces.IsRecycleBin(item)) _settings.Current.ShowRecycleBin = false;
        else return;
        _settings.Save();
        var wasReady = _ready;
        _ready = false;
        ChkThisPc.IsChecked = _settings.Current.ShowThisPc;
        ChkRecycle.IsChecked = _settings.Current.ShowRecycleBin;
        _ready = wasReady;
    }

    private void BeginOleIconDrag(string pageId, string itemId)
    {
        _pressing = false;
        _drag = PageDragKind.None;
        _pressPageId = null;
        _pressItemId = null;
        _edgeScrollTimer?.Stop();
        if (PageScroll.IsMouseCaptured) PageScroll.ReleaseMouseCapture();

        if (!_selectedItemIds.Contains(itemId))
        {
            _selectedItemIds.Clear();
            _selectedItemIds.Add(itemId);
            PaintSelection();
        }

        var ids = _selectedItemIds.ToArray();
        var data = new DataObject();
        data.SetData(DragIdsFormat, ids);
        data.SetData("SpiritNestItemId", itemId);
        var files = BuildShellDropFiles(ids);
        if (files.Length > 0)
            data.SetData(DataFormats.FileDrop, files);

        var cell = FindCell(pageId, itemId);
        if (cell is null) return;
        _dropHandledInside = false;
        DragDropEffects effects;
        try
        {
            effects = DragDrop.DoDragDrop(cell.Root, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            ClearInserts();
            CleanupTempDragFiles();
        }

        if (_dropHandledInside) return;

        if (effects == DragDropEffects.Move)
        {
            foreach (var id in ids)
            {
                var item = _library.Current.Items.FirstOrDefault(i => i.Id == id);
                if (item is null || ShellPlaces.IsShell(item)) continue;
                _collect.RemoveFromNest(item, deleteStoredFile: false);
                _selectedItemIds.Remove(id);
            }
            LibraryChanged?.Invoke();
            RebuildPageList();
            return;
        }

        if (effects == DragDropEffects.None)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (DesktopHitTest.IsDesktopUnderCursor(hwnd))
            {
                foreach (var id in ids)
                {
                    var item = _library.Current.Items.FirstOrDefault(i => i.Id == id);
                    if (item is null || ShellPlaces.IsShell(item)) continue;
                    _collect.RestoreToOriginal(item);
                    _selectedItemIds.Remove(id);
                }
                LibraryChanged?.Invoke();
                RebuildPageList();
            }
        }
    }

    private void IconDrag_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (DesktopHitTest.IsDesktopUnderCursor(hwnd))
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(SelectionIsOnlyShell() ? Cursors.No : Cursors.Arrow);
            e.Handled = true;
            return;
        }
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private bool SelectionIsOnlyShell()
    {
        if (_selectedItemIds.Count == 0) return false;
        foreach (var id in _selectedItemIds)
        {
            var item = _library.Current.Items.FirstOrDefault(i => i.Id == id);
            if (item is null || !ShellPlaces.IsShell(item)) return false;
        }
        return true;
    }

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        var row = _pageRows.FirstOrDefault(r => ReferenceEquals(r.Root, sender));
        if (row is null) return;
        var internalDrag = e.Data.GetDataPresent(DragIdsFormat);
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        if (!internalDrag && !files)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = files && !internalDrag ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;

        if (!_expandedPages.Contains(row.Id) || _expandedPages.Count != 1)
        {
            _expandedPages.Clear();
            _expandedPages.Add(row.Id);
            ApplyExpandedVisuals();
            PageScroll.UpdateLayout();
        }

        var view = e.GetPosition(PageScroll);
        const double edge = 36;
        if (view.Y < edge)
            PageScroll.ScrollToVerticalOffset(Math.Max(0, PageScroll.VerticalOffset - 14));
        else if (view.Y > PageScroll.ViewportHeight - edge)
            PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset + 14);

        var local = e.GetPosition(row.Icons);
        var at = local.Y < 0 ? 0 : ComputeIconInsert(row, local);
        _oleDropPage = row.Id;
        _oleDropIndex = at;
        ClearInserts();
        _oleDropPage = row.Id;
        _oleDropIndex = at;
        if (at >= 0 && at < row.Cells.Count)
            row.Cells[at].Marker.Visibility = Visibility.Visible;
        else
            ShowEndMarker(row);
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        _dropHandledInside = true;
        var row = _pageRows.FirstOrDefault(r => ReferenceEquals(r.Root, sender));
        var pageId = row?.Id ?? _oleDropPage;
        var at = _oleDropIndex;
        ClearInserts();
        if (pageId is null) return;

        if (e.Data.GetData(DragIdsFormat) is string[] ids && ids.Length > 0)
        {
            var visual = row?.Cells.Select(c => c.ItemId).ToList() ?? [];
            var drag = ids.ToHashSet(StringComparer.Ordinal);
            var index = at < 0 ? visual.Count : at;
            var stored = visual.Take(Math.Clamp(index, 0, visual.Count)).Count(id => !drag.Contains(id));
            _library.MoveItemsAt(ids, pageId, stored);
            _expandedPages.Clear();
            _expandedPages.Add(pageId);
            _selectedItemIds.Clear();
            foreach (var id in ids) _selectedItemIds.Add(id);
            LibraryChanged?.Invoke();
            RebuildPageList();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var newIds = new List<string>();
            foreach (var file in files)
            {
                var result = _collect.CollectFile(file, pageId);
                if (result.Success && result.Item is not null)
                    newIds.Add(result.Item.Id);
            }
            if (newIds.Count > 0)
                _library.MoveItemsAt(newIds, pageId, at < 0 ? int.MaxValue : at);
            _expandedPages.Clear();
            _expandedPages.Add(pageId);
            _selectedItemIds.Clear();
            foreach (var id in newIds) _selectedItemIds.Add(id);
            LibraryChanged?.Invoke();
            RebuildPageList();
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private static void ShowEndMarker(PageRowUi row)
    {
        var bar = row.InsertEnd;
        bar.Width = 3;
        bar.HorizontalAlignment = HorizontalAlignment.Left;
        bar.VerticalAlignment = VerticalAlignment.Top;
        if (row.Cells.Count == 0 || row.Icons.Parent is not UIElement host)
        {
            bar.Height = 36;
            bar.Margin = new Thickness(2, 6, 0, 0);
            bar.Visibility = Visibility.Visible;
            return;
        }

        var last = row.Cells[^1].Root;
        var origin = last.TranslatePoint(new Point(0, 0), host);
        var h = last.ActualHeight > 4 ? last.ActualHeight : 36;
        bar.Height = h;
        bar.Margin = new Thickness(origin.X + last.ActualWidth - 2, origin.Y, 0, 0);
        bar.Visibility = Visibility.Visible;
    }

    private void BeginMarquee(Grid host, string pageId, MouseButtonEventArgs e)
    {
        if (_drag != PageDragKind.None) return;
        _marqueeDown = true;
        _marqueeActive = false;
        _marqueeCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        _marqueeOrigin = e.GetPosition(host);
        _marqueePageId = pageId;
        _marqueeHost = host;
        _marqueeBase = _marqueeCtrl
            ? new HashSet<string>(_selectedItemIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        host.CaptureMouse();
        e.Handled = true;
    }

    private void UpdateMarquee(Grid host, MouseEventArgs e)
    {
        if (!_marqueeDown || !ReferenceEquals(_marqueeHost, host)) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndMarquee(host, null);
            return;
        }

        var p = e.GetPosition(host);
        var dx = p.X - _marqueeOrigin.X;
        var dy = p.Y - _marqueeOrigin.Y;
        if (!_marqueeActive && dx * dx + dy * dy < 16) return;
        _marqueeActive = true;

        var x = Math.Min(_marqueeOrigin.X, p.X);
        var y = Math.Min(_marqueeOrigin.Y, p.Y);
        var w = Math.Abs(dx);
        var h = Math.Abs(dy);
        var band = EnsureMarquee(host);
        band.Margin = new Thickness(x, y, 0, 0);
        band.Width = Math.Max(1, w);
        band.Height = Math.Max(1, h);
        band.Visibility = Visibility.Visible;

        var rect = new Rect(x, y, w, h);
        var next = new HashSet<string>(_marqueeBase ?? [], StringComparer.Ordinal);
        var row = _pageRows.FirstOrDefault(r => r.Id == _marqueePageId);
        if (row is not null)
        {
            foreach (var cell in row.Cells)
            {
                var origin = cell.Root.TranslatePoint(new Point(0, 0), host);
                var bounds = new Rect(origin, new Size(cell.Root.ActualWidth, cell.Root.ActualHeight));
                if (rect.IntersectsWith(bounds))
                    next.Add(cell.ItemId);
            }
        }
        _selectedItemIds.Clear();
        foreach (var id in next) _selectedItemIds.Add(id);
        PaintSelection();
        e.Handled = true;
    }

    private void EndMarquee(Grid host, MouseButtonEventArgs? e)
    {
        if (!_marqueeDown || !ReferenceEquals(_marqueeHost, host)) return;
        var clickedEmpty = !_marqueeActive && !_marqueeCtrl;
        _marqueeDown = false;
        _marqueeActive = false;
        _marqueeHost = null;
        _marqueePageId = null;
        _marqueeBase = null;
        if (_marquee is not null) _marquee.Visibility = Visibility.Collapsed;
        if (host.IsMouseCaptured) host.ReleaseMouseCapture();
        if (clickedEmpty)
        {
            _selectedItemIds.Clear();
            PaintSelection();
        }
        if (e is not null) e.Handled = true;
    }

    private Border EnsureMarquee(Grid host)
    {
        _marquee ??= new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0x9D, 0xFF)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0x4F, 0x9D, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        if (!ReferenceEquals(_marquee.Parent, host))
        {
            if (_marquee.Parent is Panel old) old.Children.Remove(_marquee);
            host.Children.Add(_marquee);
        }
        return _marquee;
    }

    private IconCell? CellFromSource(DependencyObject? src)
    {
        while (src is not null)
        {
            foreach (var row in _pageRows)
            {
                foreach (var cell in row.Cells)
                {
                    if (ReferenceEquals(cell.Root, src) || ReferenceEquals(cell.Chrome, src))
                        return cell;
                }
            }
            src = VisualTreeHelper.GetParent(src);
        }
        return null;
    }

    private string[] BuildShellDropFiles(IReadOnlyList<string> ids)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SpiritNestDrag");
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        foreach (var id in ids)
        {
            var item = _library.Current.Items.FirstOrDefault(i => i.Id == id);
            if (item is null || ShellPlaces.IsShell(item)) continue;
            if (!string.IsNullOrWhiteSpace(item.StoredPath) && File.Exists(item.StoredPath))
            {
                paths.Add(item.StoredPath);
                continue;
            }

            var safe = string.Join("_", (string.IsNullOrWhiteSpace(item.DisplayName) ? "链接" : item.DisplayName)
                .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(safe)) safe = "链接";
            if (IsUrlItem(item) && !string.IsNullOrWhiteSpace(item.TargetPath))
            {
                var path = UniqueTemp(dir, safe + ".url");
                File.WriteAllText(path, "[InternetShortcut]\r\nURL=" + item.TargetPath + "\r\n");
                _tempDragFiles.Add(path);
                paths.Add(path);
            }
            else if (!string.IsNullOrWhiteSpace(item.TargetPath))
            {
                var path = UniqueTemp(dir, safe + ".lnk");
                ShortcutHelper.CreateShortcut(path, item.TargetPath, item.DisplayName);
                _tempDragFiles.Add(path);
                paths.Add(path);
            }
        }
        return paths.ToArray();
    }

    private static string UniqueTemp(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
    }

    private void CleanupTempDragFiles()
    {
        foreach (var path in _tempDragFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* shell may have moved it */ }
        }
        _tempDragFiles.Clear();
    }

    private static bool IsUrlItem(ItemInfo item) =>
        string.Equals(item.ItemKind, "Url", StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.StorageMode, "Url", StringComparison.OrdinalIgnoreCase);

    private static int ComputeIconInsert(PageRowUi row, Point local)
    {
        if (row.Cells.Count == 0) return 0;
        var best = 0;
        var bestD = double.MaxValue;
        double maxBottom = 0;
        for (var i = 0; i < row.Cells.Count; i++)
        {
            var cell = row.Cells[i].Root;
            var origin = cell.TranslatePoint(new Point(0, 0), row.Icons);
            var center = new Point(origin.X + cell.ActualWidth / 2, origin.Y + cell.ActualHeight / 2);
            var d = (local - center).LengthSquared;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
            maxBottom = Math.Max(maxBottom, origin.Y + cell.ActualHeight);
        }
        if (local.Y > maxBottom + 10) return row.Cells.Count;
        var chosen = row.Cells[best].Root;
        var chosenOrigin = chosen.TranslatePoint(new Point(0, 0), row.Icons);
        return local.X >= chosenOrigin.X + chosen.ActualWidth / 2 ? best + 1 : best;
    }

    private void ShowAddUrlMenu(string pageId)
    {
        var menu = CreateSettingsMenu();
        var style = TryFindResource("SettingsMenuItem") as Style;
        var add = new MenuItem { Header = "添加网址", Style = style };
        add.Click += (_, _) => AddUrl(pageId);
        menu.Items.Add(add);
        menu.IsOpen = true;
    }

    private void ShowItemMenu(ItemInfo item)
    {
        var menu = CreateSettingsMenu();
        var style = TryFindResource("SettingsMenuItem") as Style;
        var del = new MenuItem { Header = "删除", Style = style };
        del.Click += (_, _) => DeleteItem(item);
        menu.Items.Add(del);
        menu.IsOpen = true;
    }

    private ContextMenu CreateSettingsMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        if (TryFindResource("SettingsContextMenu") is Style menuStyle)
            menu.Style = menuStyle;
        MenuChrome.Attach(menu);
        return menu;
    }

    private void AddUrl(string pageId)
    {
        var dlg = new UrlEditorWindow(this);
        if (dlg.ShowDialog() != true) return;
        ApplyUrlEdits(pageId, dlg.Results, isEdit: false);
    }

    private void EditItem(ItemInfo item)
    {
        if (IsUrlItem(item))
        {
            EditUrl(item);
            return;
        }

        var name = FlatDialog.PromptText(this, "名称", "编辑图标", item.DisplayName ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        item.DisplayName = name.Trim();
        _library.Save();
        LibraryChanged?.Invoke();
        RebuildPageList();
    }

    private void EditUrl(ItemInfo item)
    {
        var dlg = new UrlEditorWindow(this, item);
        if (dlg.ShowDialog() != true) return;
        ApplyUrlEdits(item.CategoryId, dlg.Results, isEdit: true);
    }

    private void ApplyUrlEdits(string pageId, IReadOnlyList<UrlEditResult> results, bool isEdit)
    {
        if (results.Count == 0) return;
        foreach (var r in results)
        {
            ItemInfo item;
            if (isEdit && !string.IsNullOrEmpty(r.ExistingId))
            {
                var found = _library.Current.Items.FirstOrDefault(i => i.Id == r.ExistingId);
                if (found is null) continue;
                item = found;
                item.DisplayName = r.DisplayName;
                item.TargetPath = r.Url;
                item.ItemKind = "Url";
                item.StorageMode = "Url";
                item.IconCacheKey = r.IconCacheKey;
            }
            else
            {
                item = new ItemInfo
                {
                    CategoryId = pageId,
                    DisplayName = r.DisplayName,
                    ItemKind = "Url",
                    StorageMode = "Url",
                    TargetPath = r.Url,
                    Source = "UrlNav",
                    SortOrder = _library.Current.Items.Where(i => i.CategoryId == pageId)
                        .Select(i => i.SortOrder).DefaultIfEmpty(-1).Max() + 1,
                    IconCacheKey = r.IconCacheKey
                };
                _library.Current.Items.Add(item);
            }

            if (r.IconCleared)
                _pageIcons.ClearUrlIcon(r.IconCacheKey);
            else if (r.IconPng is { Length: > 0 })
                _pageIcons.SaveUrlIcon(r.IconCacheKey, r.IconPng);
            CollectService.RefreshBroken(item);
        }
        _library.Save();
        _expandedPages.Add(pageId);
        LibraryChanged?.Invoke();
        RebuildPageList();
    }

    private void DeleteItem(ItemInfo item)
    {
        var name = string.IsNullOrWhiteSpace(item.DisplayName) ? "该图标" : item.DisplayName;
        if (IsShortcutOrUrl(item))
        {
            var ask = IsUrlItem(item) ? $"将删除网址「{name}」，是否继续？" : $"将删除快捷图标「{name}」，是否继续？";
            if (!FlatDialog.Confirm(this, ask, "删除", "删除", "取消", warn: true)) return;
        }
        else if (!FlatDialog.Confirm(this, $"删除「{name}」？"))
        {
            return;
        }

        if (IsShortcut(item))
        {
            switch (AskShortcutFate("是否将该快捷图标还原到原来的位置？"))
            {
                case ShortcutFate.Cancel:
                    return;
                case ShortcutFate.Restore:
                    RestoreOrRemove(item);
                    break;
                default:
                    RemoveItem(item);
                    break;
            }
        }
        else
        {
            RemoveItem(item);
        }
        LibraryChanged?.Invoke();
        RebuildPageList();
    }
}
