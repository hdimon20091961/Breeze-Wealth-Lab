namespace BreezeVL;

internal static class ChartDrawingMetrics
{
    public static double CandleBodyWidth(double slotWidth) =>
        Math.Max(1.0, slotWidth * 0.5);
}
