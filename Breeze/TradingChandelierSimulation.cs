namespace BreezeVL;

internal sealed record TradingChandelierResult(
    double ProfitPercent,
    IReadOnlyDictionary<DateTime, IReadOnlyList<double>> StopsByDate,
    IReadOnlyDictionary<DateTime, IReadOnlyList<double>> ExecutedStopsByDate,
    IReadOnlyList<double> NextBarStops,
    IReadOnlyDictionary<DateTime, int> HeldQuantityByDate);

internal static class TradingChandelierSimulation
{
    internal const double StopPercent = 3.75;
    internal const int ReentryDelayBars = 1;

    private sealed class Lot
    {
        internal double HighestHigh { get; set; }
        internal double StopForBar { get; set; } = double.NaN;
    }

    internal static TradingChandelierResult Run(
        IReadOnlyList<PricePoint> prices,
        TradingEnsembleSeries series,
        int startIndex,
        int endIndex,
        TradingActiveMode activeMode,
        double startingCapital)
    {
        if (startIndex < 0 || endIndex <= startIndex || endIndex >= prices.Count ||
            !(startingCapital > 0))
            return new(double.NaN,
                new Dictionary<DateTime, IReadOnlyList<double>>(),
                new Dictionary<DateTime, IReadOnlyList<double>>(),
                Array.Empty<double>(),
                new Dictionary<DateTime, int>());

        var signals = series.Points.ToDictionary(point => point.Date.Date);
        var lots = new List<Lot>(10);
        var stopsByDate = new Dictionary<DateTime, IReadOnlyList<double>>();
        var executedStopsByDate = new Dictionary<DateTime, IReadOnlyList<double>>();
        var heldQuantityByDate = new Dictionary<DateTime, int>();
        int? pendingTarget = null;
        int lastStopExitIndex = int.MinValue;
        double cash = 0.0;
        IReadOnlyList<double> nextBarStops = Array.Empty<double>();

        for (int index = startIndex; index <= endIndex; index++)
        {
            PricePoint bar = prices[index];
            if (!(bar.Open > 0) || !(bar.High > 0) || !(bar.Low > 0) || !(bar.Close > 0) ||
                !double.IsFinite(bar.Open) || !double.IsFinite(bar.High) ||
                !double.IsFinite(bar.Low) || !double.IsFinite(bar.Close))
                return new(double.NaN, stopsByDate, executedStopsByDate, nextBarStops,
                    heldQuantityByDate);

            if (pendingTarget.HasValue)
            {
                int target = Math.Clamp(pendingTarget.Value, 0, 10);
                while (lots.Count < target)
                {
                    cash -= bar.Open;
                    lots.Add(new Lot { HighestHigh = bar.High });
                }
                while (lots.Count > target)
                {
                    cash += bar.Open;
                    lots.RemoveAt(0);
                }
                pendingTarget = null;
            }

            // StopForBar was prepared after the previous bar and is the only
            // chandelier order that could have existed before today's OHLC was known.
            double[] activeStops = lots
                .Select(lot => lot.StopForBar)
                .Where(double.IsFinite)
                .OrderBy(value => value)
                .ToArray();
            if (activeStops.Length > 0)
                stopsByDate[bar.Date.Date] = activeStops;

            bool stopExecuted = false;
            var executedStops = new List<double>();
            for (int lotIndex = lots.Count - 1; lotIndex >= 0; lotIndex--)
            {
                Lot lot = lots[lotIndex];
                if (!double.IsFinite(lot.StopForBar) || bar.Low > lot.StopForBar)
                    continue;
                double executionPrice = bar.Open <= lot.StopForBar ? bar.Open : lot.StopForBar;
                cash += executionPrice;
                executedStops.Add(executionPrice);
                lots.RemoveAt(lotIndex);
                stopExecuted = true;
            }
            if (executedStops.Count > 0)
                executedStopsByDate[bar.Date.Date] = executedStops.OrderBy(value => value).ToArray();
            if (stopExecuted)
                lastStopExitIndex = index;

            // This is the position actually held after all orders executable on
            // the current bar, including normal market reductions and stops.
            heldQuantityByDate[bar.Date.Date] = lots.Count;

            foreach (Lot lot in lots)
                lot.HighestHigh = Math.Max(lot.HighestHigh, bar.High);

            if (!signals.TryGetValue(bar.Date.Date, out TradingEnsemblePoint? point))
                return new(double.NaN, stopsByDate, executedStopsByDate, nextBarStops,
                    heldQuantityByDate);

            int desired = BreezeWindow.TradingDesiredQuantity(point, activeMode, series.ModelCount);
            bool reentryBlocked = ReentryDelayBars > 0 && lastStopExitIndex != int.MinValue &&
                                  index - lastStopExitIndex < ReentryDelayBars;
            int scheduledTarget = desired > lots.Count && reentryBlocked ? lots.Count : desired;
            pendingTarget = scheduledTarget;

            // The reference strategy suppresses next-bar chandelier orders when
            // it has already submitted a normal market reduction.
            bool placeStops = lots.Count <= desired;
            foreach (Lot lot in lots)
                lot.StopForBar = placeStops
                    ? lot.HighestHigh * (1.0 - StopPercent / 100.0)
                    : double.NaN;

            if (index == endIndex)
            {
                // This level is known after the latest close and protects the
                // already open position on the next trading session.  It is a
                // display-only projection and never changes the completed-bar P&L.
                nextBarStops = lots
                    .Select(lot => lot.StopForBar)
                    .Where(double.IsFinite)
                    .OrderBy(value => value)
                    .ToArray();
                break;
            }
        }

        double finalClose = prices[endIndex].Close;
        return new((cash + lots.Count * finalClose) / startingCapital * 100.0,
            stopsByDate, executedStopsByDate, nextBarStops, heldQuantityByDate);
    }
}
