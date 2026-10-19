namespace BreezeVL;

internal enum ZweigMode
{
    Strong,
    Working
}

internal sealed record ZweigPoint(
    DateTime Date,
    double Zbt,
    int Density,
    int? BarsSinceSignal,
    DateTime? SignalDate,
    bool Signal,
    bool ClassicSignal);

internal sealed record ZweigSeries(
    ZweigMode Mode,
    int TriggerLevel,
    int TransitionDays,
    int MinimumMove,
    IReadOnlyList<ZweigPoint> Points);

internal static class ZweigBreadthCalculator
{
    private const int SmoothingPeriod = 10;
    private const double ClassicStartLevel = 40.0;
    private const double ClassicEndLevel = 61.5;
    private const int ClassicTransitionWindow = 10;

    public static ZweigSeries Calculate(
        PriceSeries advancing,
        PriceSeries declining,
        ZweigMode mode,
        DateTime displayStart,
        DateTime displayEnd)
    {
        int trigger = 60;
        int transitionDays = mode == ZweigMode.Strong ? 11 : 13;
        int minimumMove = mode == ZweigMode.Strong ? 21 : 15;

        var advancingByDate = advancing.Points.ToDictionary(point => point.Date, point => point.Close);
        var decliningByDate = declining.Points.ToDictionary(point => point.Date, point => point.Close);
        DateTime[] dates = advancingByDate.Keys
            .Intersect(decliningByDate.Keys)
            .OrderBy(date => date)
            .ToArray();
        if (dates.Length == 0)
            return new(mode, trigger, transitionDays, minimumMove, []);

        var rawBreadth = new double[dates.Length];
        for (int index = 0; index < dates.Length; index++)
        {
            double adv = advancingByDate[dates[index]];
            double dec = decliningByDate[dates[index]];
            double total = adv + dec;
            rawBreadth[index] = total > 0.0 ? 100.0 * adv / total : double.NaN;
        }

        double[] zbt = Ema(rawBreadth, SmoothingPeriod);
        var signals = new bool[dates.Length];
        var classicSignals = new bool[dates.Length];
        bool signalFiredInExcursion = false;

        for (int index = 1; index < dates.Length; index++)
        {
            double current = zbt[index];
            if (!double.IsFinite(current))
                continue;

            if (current < trigger)
            {
                signalFiredInExcursion = false;
            }
            else if (!signalFiredInExcursion)
            {
                int first = Math.Max(0, index - transitionDays);
                double minimum = double.PositiveInfinity;
                for (int candidate = first; candidate < index; candidate++)
                {
                    if (double.IsFinite(zbt[candidate]))
                        minimum = Math.Min(minimum, zbt[candidate]);
                }

                if (double.IsFinite(minimum) && current - minimum >= minimumMove)
                {
                    signals[index] = true;
                    signalFiredInExcursion = true;
                }
            }

            if (current > ClassicEndLevel && zbt[index - 1] <= ClassicEndLevel)
            {
                int first = Math.Max(0, index - ClassicTransitionWindow);
                for (int candidate = index - 1; candidate >= first; candidate--)
                {
                    if (double.IsFinite(zbt[candidate]) && zbt[candidate] < ClassicStartLevel)
                    {
                        classicSignals[index] = true;
                        break;
                    }
                }
            }
        }

        var points = new List<ZweigPoint>();
        int? lastSignalIndex = null;
        for (int index = 0; index < dates.Length; index++)
        {
            if (signals[index])
                lastSignalIndex = index;

            int? age = lastSignalIndex.HasValue ? index - lastSignalIndex.Value : null;
            int density = age switch
            {
                >= 0 and <= 50 => 30,
                >= 51 and <= 65 => 15,
                >= 66 and <= 85 => 5,
                _ => 0
            };
            if (dates[index] < displayStart || dates[index] > displayEnd)
                continue;

            points.Add(new ZweigPoint(
                dates[index], zbt[index], density, age,
                lastSignalIndex.HasValue ? dates[lastSignalIndex.Value] : null,
                signals[index], classicSignals[index]));
        }

        return new(mode, trigger, transitionDays, minimumMove, points);
    }

    private static double[] Ema(IReadOnlyList<double> source, int period)
    {
        var result = Enumerable.Repeat(double.NaN, source.Count).ToArray();
        double alpha = 2.0 / (period + 1.0);
        double previous = double.NaN;
        for (int index = 0; index < source.Count; index++)
        {
            double value = source[index];
            if (!double.IsFinite(value))
                continue;
            previous = double.IsFinite(previous)
                ? alpha * value + (1.0 - alpha) * previous
                : value;
            result[index] = previous;
        }
        return result;
    }
}
