using WealthLab.Core;
using WealthLab.Data;

namespace BreezeVL;

internal enum BreadthProgressLevel
{
    Normal,
    Waiting,
    Error
}

internal sealed record BreadthProgress(string Message, BreadthProgressLevel Level = BreadthProgressLevel.Normal);

internal sealed record BreadthPoint(
    DateTime Date,
    double Open,
    double High,
    double Low,
    double Close,
    int EligibleCount)
{
    public double Value => Close;
}

internal sealed record BreadthSeries(
    string Name,
    int Period,
    IReadOnlyList<BreadthPoint> Points,
    bool HasOhlc);

internal sealed record BreadthCalculationResult(
    IReadOnlyList<BreadthSeries> Series,
    int RequestedHistoryCount,
    int LoadedHistoryCount,
    int UnavailableHistoryCount,
    int FailedHistoryCount);

internal sealed record PricePoint(
    DateTime Date,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume);

internal sealed record PriceSeries(string Name, IReadOnlyList<PricePoint> Points);

internal static class BreadthCalculator
{
    // WLHost/GetHistory is an external data-provider API.  Keeping this at the
    // previously stable level avoids overloading Wealth-Lab's own workers;
    // CPU-only aggregation below may still use all but four logical CPUs.
    private const int MaxParallelHistoryRequests = 8;
    private static readonly TimeSpan HistoryRequestWarningTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HistoryRequestFailureTimeout = TimeSpan.FromSeconds(90);

    private sealed record HistoryLoadResult(string Symbol, BarHistory? History, Exception? Error)
    {
        public bool HasHistory => Error is null && History is { Count: > 0 };
    }

    public static int RecommendedCpuWorkerCount => Math.Max(1, Environment.ProcessorCount - 4);

    public static BreadthCalculationResult Calculate(
        DataSet dataSet,
        IReadOnlyList<int> periods,
        DateTime startDate,
        DateTime endDate,
        IProgress<BreadthProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var warmupStart = startDate.AddDays(-(periods.Max() * 2 + 30));
        IEnumerable<string> currentSymbols = dataSet.Symbols ?? [];
        List<DateRange> membershipRanges = (dataSet.DynamicDateRanges ?? []).ToList();

        // A dynamic DataSet can contain symbols that belonged to the index long
        // before the requested model interval.  Asking the provider for those
        // histories is both unnecessary and, for delisted aliases, potentially
        // very slow.  Symbols without a dated membership record are ordinary
        // static/current members and remain included.
        var dynamicallyDatedSymbols = membershipRanges
            .Where(range => !string.IsNullOrWhiteSpace(range.Symbol))
            .Select(range => range.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> staticOrCurrentSymbols = currentSymbols
            .Where(symbol => !dynamicallyDatedSymbols.Contains(symbol));
        IEnumerable<string> relevantDynamicSymbols = membershipRanges
            .Where(range => range.StartDate.Date <= endDate.Date && range.EndDate.Date >= startDate.Date)
            .Select(range => range.Symbol);

        var symbols = staticOrCurrentSymbols
            .Concat(relevantDynamicSymbols)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbols.Count == 0)
            throw new InvalidOperationException($"DataSet '{dataSet.Name}' contains no symbols.");

        int workerCount = Math.Min(symbols.Count, MaxParallelHistoryRequests);
        progress?.Report(new BreadthProgress(
            $"{dataSet.Name}: loading {symbols.Count} symbols with {workerCount} workers …"));

        var loadedHistories = new List<BarHistory>();
        int completedSymbols = 0;
        int unavailable = 0;
        int failed = 0;
        var failedSymbols = new List<string>();

        var waitingSymbols = new Queue<(string Symbol, int Attempt)>(
            symbols.Select(symbol => (symbol, 1)));
        var activeRequests = new Dictionary<Task<HistoryLoadResult>, (string Symbol, int Attempt)>();

        Task<HistoryLoadResult> StartRequest(string symbol) => Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var options = new DataRequestOptions { DataSet = dataSet };
                BarHistory? history = WLHost.Instance.GetHistory(
                    symbol,
                    HistoryScale.Daily,
                    warmupStart,
                    endDate,
                    0,
                    options);
                return new HistoryLoadResult(symbol, history, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new HistoryLoadResult(symbol, null, ex);
            }
        }, cancellationToken);

        void FillWorkerSlots()
        {
            while (activeRequests.Count < workerCount && waitingSymbols.Count > 0)
            {
                (string symbol, int attempt) = waitingSymbols.Dequeue();
                activeRequests.Add(StartRequest(symbol), (symbol, attempt));
            }
        }

        FillWorkerSlots();
        DateTime lastCompletionUtc = DateTime.UtcNow;
        bool waitingReported = false;
        while (activeRequests.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<HistoryLoadResult>[] tasks = activeRequests.Keys.ToArray();
            int completedIndex = Task.WaitAny(tasks, HistoryRequestWarningTimeout);
            if (completedIndex < 0)
            {
                TimeSpan stalledFor = DateTime.UtcNow - lastCompletionUtc;
                if (stalledFor >= HistoryRequestFailureTimeout)
                {
                    progress?.Report(new BreadthProgress(
                        $"{dataSet.Name}: no history response for {stalledFor.TotalSeconds:F0}s.",
                        BreadthProgressLevel.Error));
                    throw new TimeoutException(
                        $"{dataSet.Name}: Wealth-Lab returned no history for {stalledFor.TotalSeconds:F0} seconds.");
                }

                if (!waitingReported)
                {
                    progress?.Report(new BreadthProgress(
                        $"{dataSet.Name}: Wealth-Lab is busy — waiting for local histories … " +
                        $"{completedSymbols}/{symbols.Count}",
                        BreadthProgressLevel.Waiting));
                    waitingReported = true;
                }
                continue;
            }

            Task<HistoryLoadResult> completedTask = tasks[completedIndex];
            (string symbol, int attempt) = activeRequests[completedTask];
            activeRequests.Remove(completedTask);
            HistoryLoadResult result = completedTask.Result;
            lastCompletionUtc = DateTime.UtcNow;
            waitingReported = false;
            if (result.HasHistory && result.History is not null)
            {
                loadedHistories.Add(result.History);
                completedSymbols++;
            }
            else if (result.Error is not null && attempt == 1)
            {
                // Retry only genuine provider exceptions.  A null/empty history is
                // normal for some dated historical constituents and is not retried.
                waitingSymbols.Enqueue((symbol, 2));
            }
            else
            {
                completedSymbols++;
                if (result.Error is null)
                    unavailable++;
                else
                {
                    failed++;
                    failedSymbols.Add(result.Symbol);
                }
            }

            if (completedSymbols % 25 == 0 || completedSymbols == symbols.Count)
            {
                progress?.Report(new BreadthProgress(
                    $"{dataSet.Name}: histories {completedSymbols}/{symbols.Count}; " +
                    $"available {loadedHistories.Count}, outside interval {unavailable}, errors {failed} …"));
            }
            FillWorkerSlots();
        }

        List<BarHistory> histories = loadedHistories
            .OrderBy(history => history.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (histories.Count == 0)
            throw new InvalidOperationException($"No price histories could be loaded for DataSet '{dataSet.Name}'.");
        if (failed > 0)
            progress?.Report(new BreadthProgress(
                $"{dataSet.Name}: continuing with {histories.Count} histories; " +
                $"{failed} provider errors remained after retry ({string.Join(", ", failedSymbols.Take(4))}" +
                (failedSymbols.Count > 4 ? " …" : string.Empty) + ").",
                BreadthProgressLevel.Waiting));

        var counts = periods.ToDictionary(
            period => period,
            _ => new Dictionary<DateTime, (int Open, int High, int Low, int Close, int Eligible)>());
        object mergeGate = new();

        int completed = 0;
        Parallel.ForEach(
            histories,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = RecommendedCpuWorkerCount,
                CancellationToken = cancellationToken
            },
            () => periods.ToDictionary(
                period => period,
                _ => new Dictionary<DateTime, (int Open, int High, int Low, int Close, int Eligible)>()),
            (bars, _, localCounts) =>
        {
            if (bars.Count == 0)
                return localCounts;

            var prefix = new double[bars.Count + 1];
            for (int index = 0; index < bars.Count; index++)
                prefix[index + 1] = prefix[index] + bars.Close[index];
            bool[] historicallyActive = BuildHistoricallyActiveMask(bars);

            foreach (int period in periods)
            {
                Dictionary<DateTime, (int Open, int High, int Low, int Close, int Eligible)> periodCounts = localCounts[period];
                for (int index = period - 1; index < bars.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DateTime date = bars.DateTimes[index].Date;
                    if (date < startDate || date > endDate || !historicallyActive[index])
                        continue;

                    double sma = (prefix[index + 1] - prefix[index + 1 - period]) / period;
                    bool closeAbove = bars.Close[index] > sma;
                    bool openAbove = bars.Open[index] > sma;
                    bool highAbove = bars.High[index] > sma;
                    bool lowAbove = bars.Low[index] > sma;
                    periodCounts.TryGetValue(date, out var previous);
                    periodCounts[date] = (
                        previous.Open + (openAbove ? 1 : 0),
                        previous.High + (highAbove ? 1 : 0),
                        previous.Low + (lowAbove ? 1 : 0),
                        previous.Close + (closeAbove ? 1 : 0),
                        previous.Eligible + 1);
                }
            }

            int current = Interlocked.Increment(ref completed);
            if (current % 50 == 0 || current == histories.Count)
                progress?.Report(new BreadthProgress(
                    $"{dataSet.Name}: processed {current}/{histories.Count} symbols …"));
            return localCounts;
        },
        localCounts =>
        {
            lock (mergeGate)
            {
                foreach (int period in periods)
                {
                    Dictionary<DateTime, (int Open, int High, int Low, int Close, int Eligible)> target = counts[period];
                    foreach ((DateTime date, var local) in localCounts[period])
                    {
                        target.TryGetValue(date, out var previous);
                        target[date] = (
                            previous.Open + local.Open,
                            previous.High + local.High,
                            previous.Low + local.Low,
                            previous.Close + local.Close,
                            previous.Eligible + local.Eligible);
                    }
                }
            }
        });

        IReadOnlyList<BreadthSeries> series = periods.Select(period =>
        {
            var points = counts[period]
                .Where(item => item.Value.Eligible > 0)
                .OrderBy(item => item.Key)
                .Select(item => new BreadthPoint(
                    item.Key,
                    100.0 * item.Value.Open / item.Value.Eligible,
                    100.0 * item.Value.High / item.Value.Eligible,
                    100.0 * item.Value.Low / item.Value.Eligible,
                    100.0 * item.Value.Close / item.Value.Eligible,
                    item.Value.Eligible))
                .ToList();
            return new BreadthSeries($"{dataSet.Name} — above SMA({period})", period, points, true);
        }).ToList();

        if (series.All(item => item.Points.Count == 0))
            throw new InvalidOperationException(
                $"DataSet '{dataSet.Name}' has no eligible constituent observations in the requested interval.");

        return new BreadthCalculationResult(
            series,
            symbols.Count,
            histories.Count,
            unavailable,
            failed);
    }

    public static PriceSeries LoadSp500Price(DataSet dataSet, DateTime startDate, DateTime endDate)
        => LoadPriceSeries(dataSet, "SPY", startDate, endDate, "S&P 500 (SPY)");

    public static PriceSeries LoadPriceSeries(
        DataSet dataSet,
        string symbol,
        DateTime startDate,
        DateTime endDate,
        string? displayName = null)
    {
        BarHistory? bars = WLHost.Instance.GetHistory(
            symbol,
            HistoryScale.Daily,
            startDate,
            endDate,
            0,
            new DataRequestOptions { DataSet = dataSet });

        if (bars is null || bars.Count == 0)
            throw new InvalidOperationException($"{symbol} history could not be loaded.");

        var points = Enumerable.Range(0, bars.Count)
            .Select(index => new PricePoint(
                bars.DateTimes[index].Date,
                bars.Open[index],
                bars.High[index],
                bars.Low[index],
                bars.Close[index],
                bars.Volume[index]))
            .Where(point =>
                !double.IsNaN(point.Open) && !double.IsInfinity(point.Open) &&
                !double.IsNaN(point.High) && !double.IsInfinity(point.High) &&
                !double.IsNaN(point.Low) && !double.IsInfinity(point.Low) &&
                !double.IsNaN(point.Close) && !double.IsInfinity(point.Close))
            .ToList();
        return new PriceSeries(displayName ?? symbol, points);
    }

    private static bool[] BuildHistoricallyActiveMask(BarHistory bars)
    {
        var result = new bool[bars.Count];
        if (bars.ExecutableRanges is null || bars.ExecutableRanges.Count == 0)
        {
            Array.Fill(result, true);
            return result;
        }

        var ranges = bars.ExecutableRanges.OrderBy(range => range.StartDate).ToArray();
        int rangeIndex = 0;
        for (int index = 0; index < bars.Count; index++)
        {
            DateTime date = bars.DateTimes[index].Date;
            while (rangeIndex < ranges.Length && date > ranges[rangeIndex].EndDate)
                rangeIndex++;
            result[index] = rangeIndex < ranges.Length &&
                            date >= ranges[rangeIndex].StartDate &&
                            date <= ranges[rangeIndex].EndDate;
        }
        return result;
    }
}
