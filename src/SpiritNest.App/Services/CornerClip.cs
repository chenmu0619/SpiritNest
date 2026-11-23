using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpiritNest.Services;

/// <summary>
/// WPF <see cref="Border.CornerRadius"/> does not clip children — hover fills leak as square corners.
/// Attach a matching round <see cref="RectangleGeometry"/> clip so content follows the round.
/// </summary>
public static class CornerClip
{
    private static readonly ConditionalWeakTable<FrameworkElement, Box> State = new();

    private sealed class Box
    {
        public double Radius;
    }

    public static void Attach(FrameworkElement? element, double radiusDip)
    {
        if (element is null || radiusDip < 0) return;

        if (!State.TryGetValue(element, out var box))
        {
            box = new Box { Radius = radiusDip };
            State.Add(element, box);
            element.SizeChanged += (_, _) => Apply(element);
            element.Loaded += (_, _) => Apply(element);
        }
        else
        {
            box.Radius = radiusDip;
        }

        Apply(element);
    }

    public static void AttachBorder(Border? border)
    {
        if (border is null) return;
        var r = Math.Max(
            border.CornerRadius.TopLeft,
            Math.Max(border.CornerRadius.TopRight,
                Math.Max(border.CornerRadius.BottomLeft, border.CornerRadius.BottomRight)));
        // ClipToBounds is rectangular and fights round clips — turn off
        border.ClipToBounds = false;
        Attach(border, r > 0 ? r : 12);
    }

    private static void Apply(FrameworkElement element)
    {
        if (!State.TryGetValue(element, out var box)) return;
        var w = element.ActualWidth;
        var h = element.ActualHeight;
        if (w < 1 || h < 1)
        {
            element.Clip = null;
            return;
        }

        var r = Math.Min(box.Radius, Math.Min(w, h) / 2);
        var g = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        g.Freeze();
        element.Clip = g;
    }
}
