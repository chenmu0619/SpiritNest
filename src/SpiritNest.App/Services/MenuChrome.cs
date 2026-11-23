using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SpiritNest.Services;

/// <summary>
/// Round chrome for ContextMenus. No system acrylic — layered popup + region + clip.
/// </summary>
public static class MenuChrome
{
    public const double RadiusDip = 12;

    public static void Attach(ContextMenu menu)
    {
        if (menu is null) return;
        menu.Opened -= OnOpened;
        menu.Opened += OnOpened;
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                menu.ApplyTemplate();
                ApplyRoundClip(menu);

                if (PresentationSource.FromVisual(menu) is HwndSource { Handle: var hwnd } && hwnd != IntPtr.Zero)
                    AcrylicHelper.ApplyRoundRegionToVisual(menu, RadiusDip);

                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    item.SubmenuOpened -= SubmenuOpened;
                    item.SubmenuOpened += SubmenuOpened;
                }
            }
            catch { /* ignore */ }
        }, DispatcherPriority.Loaded);
    }

    private static void SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        mi.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                mi.ApplyTemplate();
                if (mi.Template?.FindName("PART_Popup", mi) is Popup { Child: Border chrome })
                {
                    CornerClip.AttachBorder(chrome);
                    AcrylicHelper.ApplyRoundRegionToVisual(chrome, RadiusDip);
                }
            }
            catch { /* ignore */ }
        }, DispatcherPriority.Loaded);
    }

    private static void ApplyRoundClip(ContextMenu menu)
    {
        if (menu.Template?.FindName("MenuChrome", menu) is Border chrome)
            CornerClip.AttachBorder(chrome);
        else
            CornerClip.Attach(menu, RadiusDip);
    }
}
