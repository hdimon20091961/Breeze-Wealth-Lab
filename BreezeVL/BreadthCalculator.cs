using WealthLab.Core;
using WealthLab.Data;
using System.Collections.Concurrent;

namespace BreezeVL;

internal sealed record BreadthPoint(DateTime Date, double Value, int EligibleCount);

internal sealed record BreadthSeries(string Name, int Period, IReadOnlyList<BreadthPoint> Points);

internal sealed record PricePoint(DateTime Date, double Open, double High, double Low, double Close);

internal sealed record PriceSeries(string Name, IReadOnlyList<PricePoint> Points);

internal static class BreadthCalculator
{
    private const int MaxParallelHistoryRequests = 8;

    public static IReadOnlyList<BreadthSeries> Calculate(
        DataSet dataSet,
        IReadOnlyList<int> periods,
        DateTime startDate,
        DateTime endDate,
        IProgress<string>? progress = null)
    {
        IEnumerable<string> currentSymbols = dataSet.Symbols ?? [];
        IEnumerable<DateRange> membershipRanges = dataSet.DynamicDateRanges ?? [];

        var symbols = currentSymbols
            .Concat(membershipRanges.Select(range => range.Symbol))
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbols.Count == 0)
            throw new InvalidOperationException($"DataSet '{dataSet.Name}' contains no symbols.");

        int workerCount = Math.Min(MaxParallelHistoryRequests, Math.Max(2, Environment.ProcessorCount));
        progress?.Report($"{dataSet.Name}: loading {symbols.Count} symbols with {workerCount} workers …");

        var warmupStart = startDate.AddDays(-(periods.Max() * 2 + 30));
        var loadedHistories = new ConcurrentBag<BarHistory>();
        int loaded = 0;
        int failed = 0;

        Parallel.ForEach(
            symbols,
            new ParallelOptions { MaxDegreeOfParallelism = workerCount },
            symbol =>
            {
                try
                {
                    var options = new DataRequestOptions { DataSet = dataSet };
                    BarHistory? history = WLHost.Instance.GetHistory(
                        symbol,
                        HistoryScale.Daily,
                        warmupStart,
                        endDate,
                        0,
                        options);
                    if (history is not null && history.Count > 0)
                        loadedHistories.Add(history);
                    else
                        Interlocked.Increment(ref failed);
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                }

                int current = Interlocked.Increment(ref loaded);
                if (current % 25 == 0 || current == symbols.Count)
                    progress?.Report($"{dataSet.Name}: loaded {current}/{symbols.Count}; unavailable {Volatile.Read(ref failed)} …");
            });

        List<BarHistory> histories = loadedHistories
            .OrderBy(history => history.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (histories.Count == 0)
            throw new InvalidOperationException($"No price histories could be loaded for DataSet '{dataSet.Name}'.");

        var counts = periods.ToDictionary(
            period => period,
            _ => new ConcurrentDictionary<DateTime, (int Above, int Eligible)>());

        int completed = 0;
        Parallel.ForEach(
            histories,
            new ParallelOptions { MaxDegreeOfParallelism = workerCount },
            bars =>
        {
            if (bars.Count == 0)
                return;

            var prefix = new double[bars.Count + 1];
            for (int index = 0; index < bars.Count; index++)
                prefix[index + 1] = prefix[index] + bars.Close[index];

            foreach (int period in periods)
            {
                ConcurrentDictionary<DateTime, (int Above, int Eligible)> periodCounts = counts[period];
                for (int index = period - 1; index < bars.Count; index++)
                {
                    DateTime date = bars.DateTimes[index].Date;
                    if (date < startDate || date > endDate || !IsHistoricallyActive(bars, date))
                        continue;

                    double sma = (prefix[index + 1] - prefix[index + 1 - period]) / period;
                    bool isAbove = bars.Close[index] > sma;
                    periodCounts.AddOrUpdate(
                        date,
                        _ => (isAbove ? 1 : 0, 1),
                        (_, previous) => (previous.Above + (isAbove ? 1 : 0), previous.Eligible + 1));
                }
            }

            int current = Interlocked.Increment(ref completed);
            if (current % 50 == 0 || current == histories.Count)
                progress?.Report($"{dataSet.Name}: processed {completed}/{histories.Count} symbols …");
        });

        return periods.Select(period =>
        {
            var points = counts[period]
                .Where(item => item.Value.Eligible > 0)
                .OrderBy(item => item.Key)
                .Select(item => new BreadthPoint(
                    item.Key,
                    100.0 * item.Value.Above / item.Value.Eligible,
                    item.Value.Eligible))
                .ToList();
            return new BreadthSeries($"{dataSet.Name} — above SMA({period})", period, points);
        }).ToList();
    }

    public static PriceSeries LoadSp500Price(DataSet dataSet, DateTime startDate, DateTime endDate)
    {
        BarHistory? bars = WLHost.Instance.GetHistory(
            "SPY",
            HistoryScale.Daily,
            startDate,
            endDate,
            0,
            new DataRequestOptions { DataSet = dataSet });

        if (bars is null || bars.Count == 0)
            throw new InvalidOperationException("SPY history could not be loaded for the S&P 500 overlay.");

        var points = Enumerable.Range(0, bars.Count)
            .Select(index => new PricePoint(
                bars.DateTimes[index].Date,
                bars.Open[index],
                bars.High[index],
                bars.Low[index],
                bars.Close[index]))
            .Where(point =>
                !double.IsNaN(point.Open) && !double.IsInfinity(point.Open) &&
                !double.IsNaN(point.High) && !double.IsInfinity(point.High) &&
                !double.IsNaN(point.Low) && !double.IsInfinity(point.Low) &&
                !double.IsNaN(point.Close) && !double.IsInfinity(point.Close))
            .ToList();
        return new PriceSeries("S&P 500 (SPY)", points);
    }

    private static bool IsHistoricallyActive(BarHistory bars, DateTime date)
    {
        if (bars.ExecutableRanges is null || bars.ExecutableRanges.Count == 0)
            return true;

        return bars.ExecutableRanges.Any(range => date >= range.StartDate && date <= range.EndDate);
    }
}
