using System.Windows.Media;

namespace BreezeVL;

internal static class ChartCursorStyle
{
    private static readonly Brush PinnedBrush = CreatePinnedBrush();

    public static Brush Stroke(bool pinned, Brush normal) => pinned ? PinnedBrush : normal;

    public static double Thickness(bool pinned, double normal) => pinned ? 2.0 : normal;

    public static double Opacity(bool pinned, double normal) => pinned ? 1.0 : normal;

    private static Brush CreatePinnedBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(36, 116, 210));
        brush.Freeze();
        return brush;
    }
}
