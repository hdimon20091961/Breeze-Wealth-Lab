namespace BreezeVL;

internal sealed record TitanicPoint(
    DateTime Date,
    double QqqOpen,
    double QqqHigh,
    double QqqLow,
    double QqqClose,
    double AnnualHigh,
    bool RecentlyAtHigh,
    double NasdaqNewHighs,
    double NasdaqNewLows,
    bool LowsExceedHighs,
    double NasdaqBreadth50,
    int ValidBreadthMembers,
    bool BreadthFilterPassed,
    bool Signal,
    int RunLength,
    bool Persistent);

internal sealed record TitanicSeries(
    int AnnualHighLookback,
    int RecentHighWindow,
    double HighTolerancePercent,
    int PersistentDays,
    bool UseBreadthFilter,
    double BreadthThreshold,
    IReadOnlyList<TitanicPoint> Points);

internal static class TitanicSyndromeCalculator
{
    public const int AnnualHighLookback = 252;
    public const int RecentHighWindow = 7;
    public const double HighTolerancePercent = 3.0;
    public const int PersistentDays = 7;
    public const bool UseBreadthFilter = false;
    public const double BreadthThreshold = 35.0;
    public const int MinimumValidMembers = 80;

    public static TitanicSeries Calculate(
        PriceSeries qqq,
        PriceSeries nasdaqNewHighs,
        PriceSeries nasdaqNewLows,
        BreadthSeries nasdaqBreadth50,
        DateTime visibleStart,
        DateTime visibleEnd)
    {
        var highsByDate = nasdaqNewHighs.Points.ToDictionary(point => point.Date);
        var lowsByDate = nasdaqNewLows.Points.ToDictionary(point => point.Date);
        var breadthByDate = nasdaqBreadth50.Points.ToDictionary(point => point.Date);
        IReadOnlyList<PricePoint> qqqPoints = qqq.Points.OrderBy(point => point.Date).ToList();
        var annualHigh = new double[qqqPoints.Count];
        var maximumCandidates = new LinkedList<int>();
        for (int index = 0; index < qqqPoints.Count; index++)
        {
            int firstAllowed = index - AnnualHighLookback + 1;
            while (maximumCandidates.First is not null && maximumCandidates.First.Value < firstAllowed)
                maximumCandidates.RemoveFirst();
            while (maximumCandidates.Last is not null &&
                   qqqPoints[maximumCandidates.Last.Value].Close <= qqqPoints[index].Close)
                maximumCandidates.RemoveLast();
            maximumCandidates.AddLast(index);
            annualHigh[index] = qqqPoints[maximumCandidates.First!.Value].Close;
        }

        var points = new List<TitanicPoint>();
        int run = 0;
        for (int index = AnnualHighLookback - 1; index < qqqPoints.Count; index++)
        {
            DateTime date = qqqPoints[index].Date;
            if (!highsByDate.TryGetValue(date, out PricePoint? newHighs) ||
                !lowsByDate.TryGetValue(date, out PricePoint? newLows))
            {
                run = 0;
                continue;
            }

            bool recentlyAtHigh = false;
            int recentStart = Math.Max(AnnualHighLookback - 1, index - RecentHighWindow + 1);
            for (int recent = recentStart; recent <= index; recent++)
            {
                if (qqqPoints[recent].Close >= annualHigh[recent] * (1.0 - HighTolerancePercent / 100.0))
                {
                    recentlyAtHigh = true;
                    break;
                }
            }

            bool lowsExceedHighs = newLows.Close > newHighs.Close;
            BreadthPoint? breadth = breadthByDate.GetValueOrDefault(date);
            double breadthValue = breadth?.Close ?? double.NaN;
            int validMembers = breadth?.EligibleCount ?? 0;
            bool breadthWeak = validMembers >= MinimumValidMembers &&
                               double.IsFinite(breadthValue) && breadthValue < BreadthThreshold;
            bool breadthFilterPassed = !UseBreadthFilter || breadthWeak;
            bool signal = recentlyAtHigh && lowsExceedHighs && breadthFilterPassed;
            run = signal ? run + 1 : 0;

            if (date >= visibleStart && date <= visibleEnd)
            {
                points.Add(new TitanicPoint(
                    date, qqqPoints[index].Open, qqqPoints[index].High,
                    qqqPoints[index].Low, qqqPoints[index].Close,
                    annualHigh[index], recentlyAtHigh,
                    newHighs.Close, newLows.Close, lowsExceedHighs,
                    breadthValue, validMembers, breadthFilterPassed,
                    signal, run, signal && run >= PersistentDays));
            }
        }

        return new TitanicSeries(
            AnnualHighLookback, RecentHighWindow, HighTolerancePercent,
            PersistentDays, UseBreadthFilter, BreadthThreshold, points);
    }

    public static TitanicSeries WithPersistentDays(TitanicSeries source, int persistentDays)
    {
        int days = Math.Clamp(persistentDays, 1, 30);
        IReadOnlyList<TitanicPoint> points = source.Points
            .Select(point => point with { Persistent = point.Signal && point.RunLength >= days })
            .ToList();
        return source with { PersistentDays = days, Points = points };
    }
}
