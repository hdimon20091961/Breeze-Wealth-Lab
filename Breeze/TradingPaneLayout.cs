namespace BreezeVL;

internal static class TradingPaneLayout
{
    internal const double HeaderHeight = 26;
    internal const double MinimumExpandedHeight = 46;

    internal static double[] Calculate(double available, IReadOnlyList<double> weights, IReadOnlyList<bool> collapsed)
    {
        if (weights.Count != 4 || collapsed.Count != 4 || weights.Any(w => !double.IsFinite(w) || w <= 0))
            throw new ArgumentException("Four positive pane weights and four collapse states are required.");
        var heights = new double[4];
        var remaining = Enumerable.Range(0, 4).Where(i => !collapsed[i]).ToList();
        for (int i = 0; i < 4; i++) if (collapsed[i]) heights[i] = HeaderHeight;
        double space = Math.Max(0, available - collapsed.Count(c => c) * HeaderHeight);
        double minimum = remaining.Count == 0 ? 0 : Math.Min(MinimumExpandedHeight, space / remaining.Count);
        while (remaining.Count > 0)
        {
            double totalWeight = remaining.Sum(i => weights[i]);
            int tiny = remaining.FindIndex(i => space * weights[i] / totalWeight < minimum);
            if (tiny < 0)
            {
                foreach (int i in remaining) heights[i] = space * weights[i] / totalWeight;
                break;
            }
            int index = remaining[tiny];
            heights[index] = minimum; space -= minimum; remaining.RemoveAt(tiny);
        }
        return heights;
    }
}
