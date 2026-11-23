using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpiritNest.Services;

namespace SpiritNest.Views;

/// <summary>
/// Flat frosted-glass prompts shared by settings and panel flows.
/// </summary>
public static class FlatDialog
{
    public static void Info(Window? owner, string message, string title = "灵栖")
    {
        var w = CreateShell(owner, title, 380, 160);
        var ok = CreateButton(owner, "确定", primary: true, isDefault: true, isCancel: true);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Content = BuildLayout(owner, BuildBody(CreateMessage(owner, message)), ok);
        w.ShowDialog();
    }

    public readonly record struct Choice(string Text, bool Primary = false, bool Warn = false);

    /// <summary>Returns the clicked choice index, or -1 when dismissed.</summary>
    public static int Ask(Window? owner, string title, string message, params Choice[] choices)
    {
        var w = CreateShell(owner, title, 420, double.NaN);
        var picked = -1;
        var buttons = new Button[choices.Length];
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var choice = choices[i];
            var button = CreateButton(owner, choice.Text, primary: choice.Primary, warn: choice.Warn,
                isDefault: choice.Primary, isCancel: i == 0 && !choice.Primary);
            button.Click += (_, _) =>
            {
                picked = index;
                w.DialogResult = true;
            };
            buttons[i] = button;
        }

        w.Content = BuildLayout(owner, BuildBody(CreateTitle(owner, title), CreateMessage(owner, message, lead: true)), buttons);
        w.ShowDialog();
        return picked;
    }

    public static bool Confirm(Window? owner, string message, string title = "灵栖",
        string okText = "确定", string cancelText = "取消", bool warn = false)
    {
        return Ask(owner, title, message,
            new Choice(cancelText),
            new Choice(okText, Primary: true, Warn: warn)) == 1;
    }

    public static string? PromptText(Window? owner, string message, string title, string defaultValue = "")
    {
        var w = CreateShell(owner, title, 400, 200);
        var box = CreateTextBox(owner, defaultValue);
        string? result = null;

        var ok = CreateButton(owner, "确定", primary: true, isDefault: true);
        var cancel = CreateButton(owner, "取消", primary: false, isCancel: true);
        ok.Click += (_, _) => { result = box.Text; w.DialogResult = true; };

        w.Content = BuildLayout(owner,
            BuildBody(CreateMessage(owner, message), box),
            ok, cancel);

        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return w.ShowDialog() == true ? result : null;
    }

    public static T? PickItem<T>(
        Window? owner,
        string message,
        string title,
        IReadOnlyList<T> options,
        Func<T, string> display)
        where T : class
    {
        if (options.Count == 0) return null;

        var w = CreateShell(owner, title, 400, 220);
        var face = FaceFor(owner);
        var box = new ComboBox
        {
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 0, 4),
            BorderBrush = face.FieldBorder,
            BorderThickness = new Thickness(1),
            Background = face.Field,
            Foreground = face.Ink,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ItemsSource = options.Select(o => new PickOption<T>(o, display(o))).ToList(),
            SelectedIndex = 0,
            DisplayMemberPath = nameof(PickOption<T>.Display)
        };

        T? result = null;
        var ok = CreateButton(owner, "确定", primary: true, isDefault: true);
        var cancel = CreateButton(owner, "取消", primary: false, isCancel: true);
        ok.Click += (_, _) =>
        {
            if (box.SelectedItem is PickOption<T> pick)
                result = pick.Value;
            w.DialogResult = true;
        };

        w.Content = BuildLayout(owner,
            BuildBody(CreateMessage(owner, message), box),
            ok, cancel);

        return w.ShowDialog() == true ? result : null;
    }

    private sealed class PickOption<T>(T value, string display)
    {
        public T Value { get; } = value;
        public string Display { get; } = display;
    }

    private static Window CreateShell(Window? owner, string title, double width, double height)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            Height = double.IsNaN(height) ? 160 : height,
            SizeToContent = double.IsNaN(height) ? SizeToContent.Height : SizeToContent.Manual,
            MinHeight = 148,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            Topmost = false,
            ShowInTaskbar = false,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI")
        };
        GlassChrome.Attach(w);
        var palette = ResolvePalette();
        GlassChrome.ApplyTheme(w, palette,
            (Application.Current as SpiritNest.App)?.CurrentSettings ?? new Models.AppSettings());
        if (owner is SettingsWindow)
        {
            void PaintSettings()
            {
                if (w.Content is not Border shell) return;
                var face = FaceFor(owner);
                shell.Background = face.Page;
                shell.BorderBrush = face.Edge;
            }
            w.SourceInitialized += (_, _) => PaintSettings();
            w.Loaded += (_, _) => PaintSettings();
        }
        w.MouseLeftButtonDown += (_, e) =>
            GlassChrome.DragFromChrome(w, e, e.OriginalSource as DependencyObject);
        ScreenDpi.Attach(w);
        return w;
    }

    private static ThemePalette ResolvePalette()
    {
        var live = (Application.Current as SpiritNest.App)?.CurrentSettings;
        return ThemeService.Resolve(live ?? new Models.AppSettings());
    }

    private readonly record struct DialogFace(
        Brush Page, Brush Ink, Brush Muted, Brush Edge, Brush Field, Brush FieldBorder, Brush Hover, Brush Press, Brush Primary);

    private static DialogFace FaceFor(Window? owner)
    {
        if (owner is SettingsWindow)
        {
            return new DialogFace(
                Freeze(Color.FromRgb(0xF2, 0xF2, 0xF7)),
                Freeze(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                Freeze(Color.FromRgb(0x8A, 0x8A, 0x8A)),
                Freeze(Color.FromArgb(0x1A, 0, 0, 0)),
                Freeze(Colors.White),
                Freeze(Color.FromRgb(0xE5, 0xE5, 0xEA)),
                Freeze(Color.FromArgb(0x0A, 0, 0, 0)),
                Freeze(Color.FromArgb(0x14, 0, 0, 0)),
                Freeze(Colors.White));
        }

        var p = ResolvePalette();
        return new DialogFace(
            GlassChrome.OverlayFill,
            ThemeService.Brush(p.Text),
            ThemeService.Brush(p.TextMuted),
            GlassChrome.Edge,
            ThemeService.Brush(p.IsDark
                ? Color.FromArgb(40, 255, 255, 255)
                : Color.FromArgb(220, 255, 255, 255)),
            ThemeService.Brush(p.Border),
            GlassChrome.Hover,
            GlassChrome.Press,
            ThemeService.Brush(p.AccentSoft));
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static TextBlock CreateTitle(Window? owner, string title)
    {
        var face = FaceFor(owner);
        return new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = face.Ink,
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private static TextBlock CreateMessage(Window? owner, string message, bool lead = false)
    {
        var face = FaceFor(owner);
        return new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = lead ? face.Ink : face.Muted,
            FontSize = lead ? 14 : 13,
            Margin = new Thickness(0, 0, 0, lead ? 4 : 12),
            LineHeight = lead ? 22 : 20
        };
    }

    private static TextBox CreateTextBox(Window? owner, string text)
    {
        var face = FaceFor(owner);
        return new TextBox
        {
            Text = text,
            Height = 32,
            Padding = new Thickness(8, 6, 8, 6),
            BorderBrush = face.FieldBorder,
            BorderThickness = new Thickness(1),
            Background = face.Field,
            Foreground = face.Ink,
            CaretBrush = face.Ink,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private static Button CreateButton(Window? owner, string content, bool primary, bool isDefault = false, bool isCancel = false, bool warn = false)
    {
        var face = FaceFor(owner);
        var danger = warn && primary;
        var btn = new Button
        {
            Content = content,
            MinWidth = 76,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            Foreground = danger ? Brushes.White : face.Ink,
            Background = danger ? Freeze(Color.FromRgb(0xFF, 0x3B, 0x30)) : primary ? face.Primary : Brushes.Transparent,
            BorderBrush = danger ? Brushes.Transparent : face.Edge,
            BorderThickness = new Thickness(danger ? 0 : 1),
            Cursor = Cursors.Hand,
            IsDefault = isDefault,
            IsCancel = isCancel,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Template = danger ? CreateWarnButtonTemplate() : CreateFlatButtonTemplate(face)
        };
        return btn;
    }

    private static ControlTemplate CreateWarnButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Freeze(Color.FromRgb(0xE0, 0x35, 0x2B))) { TargetName = "Bd" });
        template.Triggers.Add(hover);

        var press = new Trigger { Property = Button.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
        template.Triggers.Add(press);

        return template;
    }

    private static ControlTemplate CreateFlatButtonTemplate(DialogFace face)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, face.Hover) { TargetName = "Bd" });
        template.Triggers.Add(hover);

        var press = new Trigger { Property = Button.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(Border.BackgroundProperty, face.Press) { TargetName = "Bd" });
        template.Triggers.Add(press);

        return template;
    }

    private static StackPanel BuildBody(params UIElement[] children)
    {
        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 6) };
        foreach (var c in children) body.Children.Add(c);
        return body;
    }

    private static Border BuildLayout(Window? owner, UIElement body, params Button[] buttons)
    {
        var face = FaceFor(owner);
        var bar = new Border
        {
            BorderBrush = face.Edge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 12, 16, 16),
            Background = Brushes.Transparent
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        foreach (var b in buttons) row.Children.Add(b);
        bar.Child = row;

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(body);

        var shell = new Border
        {
            Background = face.Page,
            BorderBrush = face.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GlassChrome.CornerRadiusDip),
            Child = root
        };
        CornerClip.Attach(shell, GlassChrome.CornerRadiusDip);
        return shell;
    }
}
