using System.Globalization;
using System.IO;
using System.Text;

namespace BreezeVL;

internal sealed record VixTermPoint(int DaysToExpiration, double Price);

internal enum VixCurveState
{
    Unknown,
    StrongContango,
    Contango,
    Mixed,
    Backwardation,
    StrongBackwardation
}

internal enum SvxyRecommendation
{
    Neutral,
    Buy,
    ExtendedHold,
    StrongBuy
}

internal sealed record VixCurveStatePoint(
    DateTime Date,
    VixCurveState State,
    double? SpotVix,
    double? M1Price,
    double? M2Price,
    double? M0ToM1Pct,
    double? M1ToM2Pct,
    double Price30,
    double Price60,
    double Contango30To60Pct,
    int RisingAdjacentPairs,
    int FallingAdjacentPairs,
    int AdjacentPairCount,
    double RisingPairShare,
    double CurveSlope30DayPct,
    double CurveRegimeScore,
    bool SvxyBuyCandidate,
    SvxyRecommendation SvxyRecommendation,
    int? SvxyBarsSinceCandidate)
{
    public VixCurveState LegacyState { get; init; } = State;
    public double LegacyCurveRegimeScore { get; init; } = CurveRegimeScore;
    public double? SpotToM1Pct { get; init; }
    public int IgnoredMinorInversions { get; init; }
    public double LargestIgnoredInversionPct { get; init; }
    public double MaximumTailDrawdownPct { get; init; }
    public bool BroadFrontInversion { get; init; }
    public bool SpotStress { get; init; }
    public bool PriceStress { get; init; }
    public bool TransitionConfirmationApplied { get; init; }
    public string AdjustmentExplanation { get; init; } = string.Empty;
}

internal sealed record VixCurveStateSeries(IReadOnlyList<VixCurveStatePoint> Points);

internal sealed record VixMarketStressContext(double SpyReturnPct, double Atrp1);

internal static class VixCurveStateClassifier
{
    public const int MaximumDays = 180;
    public const double FrontTolerancePct = 0.50;
    public const double TailTolerancePct = 1.00;
    public const double TailCumulativeLimitPct = 1.50;
    public const double SpotStressTolerancePct = 0.50;

    public static VixCurveStatePoint Classify(
        DateTime date,
        IReadOnlyList<VixTermPoint> sourcePoints,
        double? spotVix,
        VixMarketStressContext? marketStress = null)
    {
        IReadOnlyList<VixTermPoint> points = sourcePoints
            .Where(point => point.DaysToExpiration <= MaximumDays)
            .OrderBy(point => point.DaysToExpiration)
            .ToList();
        var snapshot = new VixTermSnapshot(date, date, points, spotVix);
        int rising = 0;
        int falling = 0;
        int adjustedRising = 0;
        int adjustedFalling = 0;
        int ignoredMinorInversions = 0;
        double largestIgnoredInversion = 0.0;
        var pairChanges = new List<double>();
        for (int index = 1; index < points.Count; index++)
        {
            double delta = points[index].Price - points[index - 1].Price;
            if (delta > 0.000001)
                rising++;
            else if (delta < -0.000001)
                falling++;

            double changePct = points[index - 1].Price > 0
                ? 100.0 * (points[index].Price / points[index - 1].Price - 1.0)
                : 0.0;
            pairChanges.Add(changePct);
            double tolerance = index <= 2 ? FrontTolerancePct : TailTolerancePct;
            if (changePct > 0.000001)
                adjustedRising++;
            else if (changePct < -tolerance)
                adjustedFalling++;
            else if (changePct < -0.000001)
            {
                ignoredMinorInversions++;
                largestIgnoredInversion = Math.Max(largestIgnoredInversion, -changePct);
            }
        }

        int pairs = Math.Max(0, points.Count - 1);
        double risingShare = pairs == 0 ? 0.0 : rising / (double)pairs;
        double contangoPct = points.Count == 0 ? 0.0 : snapshot.Contango30To60 * 100.0;
        double legacyAdjacentBalance = pairs == 0 ? 0.0 : (rising - falling) / (double)pairs;
        double slopeStrength = Math.Tanh(contangoPct / 5.0);
        double legacyScore = 0.65 * legacyAdjacentBalance + 0.35 * slopeStrength;
        VixCurveState legacyState = StateForScore(legacyScore, pairs);

        IReadOnlyList<VixTermPoint> tail = points.Where(point => point.DaysToExpiration >= 90).ToList();
        double maximumTailDrawdown = MaximumDrawdownPct(tail);
        bool hasSignificantTailPair = pairChanges.Skip(2).Any(change => change < -TailTolerancePct);
        if (!hasSignificantTailPair && maximumTailDrawdown > TailCumulativeLimitPct)
            adjustedFalling++;

        int meaningfulPairs = adjustedRising + adjustedFalling;
        double adjustedBalance = meaningfulPairs == 0
            ? 0.0
            : (adjustedRising - adjustedFalling) / (double)meaningfulPairs;
        double regimeScore = 0.65 * adjustedBalance + 0.35 * slopeStrength;
        VixCurveState state = pairs == 0
            ? VixCurveState.Unknown
            : StateForScore(regimeScore, pairs);

        double slope30DayPct = 0.0;
        if (points.Count >= 2)
        {
            VixTermPoint first = points[0];
            VixTermPoint last = points[^1];
            int daySpan = last.DaysToExpiration - first.DaysToExpiration;
            if (daySpan > 0 && first.Price > 0)
                slope30DayPct = (last.Price / first.Price - 1.0) * 30.0 / daySpan * 100.0;
        }

        double? m1 = points.ElementAtOrDefault(0)?.Price;
        double? m2 = points.ElementAtOrDefault(1)?.Price;
        double? spotToM1 = spotVix.HasValue && m1.HasValue && m1.Value > 0
            ? 100.0 * (spotVix.Value / m1.Value - 1.0)
            : null;
        int spotAboveFront = spotVix.HasValue
            ? points.Take(3).Count(point => point.Price > 0 &&
                100.0 * (spotVix.Value / point.Price - 1.0) > SpotStressTolerancePct)
            : 0;
        bool spotStress = spotToM1 > SpotStressTolerancePct;
        bool broadFrontInversion = points.Count >= 3 && points[0].Price > 0 &&
            100.0 * (1.0 - points[1].Price / points[0].Price) > FrontTolerancePct &&
            100.0 * (1.0 - points[2].Price / points[0].Price) > FrontTolerancePct;
        bool frontInversion = pairChanges.Take(2).Any(change => change < -FrontTolerancePct);
        bool priceStress = marketStress is not null && marketStress.SpyReturnPct < 0.0 &&
            marketStress.Atrp1 >= 2.0 && spotAboveFront >= 3 && frontInversion;
        var explanations = new List<string>();
        if (ignoredMinorInversions > 0)
            explanations.Add($"{ignoredMinorInversions} локальных снижений до {largestIgnoredInversion:0.00}% " +
                $"признаны незначительными (допуск {FrontTolerancePct:0.0}% для front и {TailTolerancePct:0.0}% для хвоста)");
        if (maximumTailDrawdown > TailCumulativeLimitPct)
            explanations.Add($"накопленное снижение хвоста {maximumTailDrawdown:0.00}% превышает предел {TailCumulativeLimitPct:0.0}%");
        if (broadFrontInversion)
            explanations.Add("второй и третий контракты значимо ниже первого; повышение до выраженного контанго запрещено");

        if (broadFrontInversion && state == VixCurveState.StrongContango)
        {
            state = VixCurveState.Contango;
            regimeScore = Math.Min(regimeScore, 0.599999);
        }
        if (spotStress && state is VixCurveState.StrongContango or VixCurveState.Contango)
        {
            explanations.Add($"геометрия контанго восстановилась, но рыночный стресс ещё сохраняется: " +
                $"spot VIX выше ближайшего фьючерса на {spotToM1:0.00}%");
            if (state == VixCurveState.StrongContango)
            {
                state = VixCurveState.Contango;
                regimeScore = Math.Min(regimeScore, 0.599999);
            }
        }
        if (priceStress)
        {
            explanations.Add($"выраженный ценовой стресс: SPY {marketStress!.SpyReturnPct:+0.00;-0.00;0.00}%, " +
                $"ATRP(1) {marketStress.Atrp1:0.00}%, spot VIX выше первых трёх контрактов и front инвертирован");
            if (state is VixCurveState.StrongContango or VixCurveState.Contango)
            {
                state = VixCurveState.Mixed;
                regimeScore = Math.Min(regimeScore, 0.179999);
            }
        }

        return new VixCurveStatePoint(
            date.Date,
            state,
            spotVix,
            m1,
            m2,
            spotVix.HasValue && m1.HasValue && spotVix.Value > 0
                ? (m1.Value / spotVix.Value - 1.0) * 100.0
                : null,
            m1.HasValue && m2.HasValue && m1.Value > 0
                ? (m2.Value / m1.Value - 1.0) * 100.0
                : null,
            snapshot.Price30,
            snapshot.Price60,
            contangoPct,
            rising,
            falling,
            pairs,
            risingShare,
            slope30DayPct,
            regimeScore,
            false,
            SvxyRecommendation.Neutral,
            null)
        {
            LegacyState = legacyState,
            LegacyCurveRegimeScore = legacyScore,
            SpotToM1Pct = spotToM1,
            IgnoredMinorInversions = ignoredMinorInversions,
            LargestIgnoredInversionPct = largestIgnoredInversion,
            MaximumTailDrawdownPct = maximumTailDrawdown,
            BroadFrontInversion = broadFrontInversion,
            SpotStress = spotStress,
            PriceStress = priceStress,
            AdjustmentExplanation = string.Join("; ", explanations)
        };
    }

    private static VixCurveState StateForScore(double score, int pairCount) => pairCount == 0
        ? VixCurveState.Unknown
        : score >= 0.60 ? VixCurveState.StrongContango
        : score >= 0.18 ? VixCurveState.Contango
        : score <= -0.60 ? VixCurveState.StrongBackwardation
        : score <= -0.18 ? VixCurveState.Backwardation
        : VixCurveState.Mixed;

    private static double MaximumDrawdownPct(IReadOnlyList<VixTermPoint> points)
    {
        double peak = 0.0;
        double maximum = 0.0;
        foreach (VixTermPoint point in points)
        {
            if (point.Price > peak)
                peak = point.Price;
            else if (peak > 0)
                maximum = Math.Max(maximum, 100.0 * (1.0 - point.Price / peak));
        }
        return maximum;
    }

    public static string Label(VixCurveState state) => state switch
    {
        VixCurveState.StrongContango => "Strong contango",
        VixCurveState.Contango => "Contango",
        VixCurveState.Mixed => "Mixed / partial inversion",
        VixCurveState.Backwardation => "Backwardation",
        VixCurveState.StrongBackwardation => "Broad backwardation",
        _ => "No data"
    };
}

internal sealed record VixTermSnapshot(
    DateTime RequestedDate,
    DateTime TradeDate,
    IReadOnlyList<VixTermPoint> Points,
    double? SpotVix = null)
{
    public double Price30 => Interpolate(30);
    public double Price60 => Interpolate(60);
    public double Difference30To60 => Price60 - Price30;
    public double Contango30To60 => Price30 == 0 ? 0 : Price60 / Price30 - 1.0;

    private double Interpolate(int targetDays)
    {
        if (Points.Count == 0)
            return 0;
        if (targetDays <= Points[0].DaysToExpiration)
            return Points[0].Price;
        if (targetDays >= Points[^1].DaysToExpiration)
            return Points[^1].Price;

        for (int index = 1; index < Points.Count; index++)
        {
            VixTermPoint right = Points[index];
            if (right.DaysToExpiration < targetDays)
                continue;

            VixTermPoint left = Points[index - 1];
            int span = right.DaysToExpiration - left.DaysToExpiration;
            if (span <= 0)
                return left.Price;
            double fraction = (targetDays - left.DaysToExpiration) / (double)span;
            return left.Price + fraction * (right.Price - left.Price);
        }

        return Points[^1].Price;
    }
}

internal sealed class VixTermStructureStore
{
    private const int ExportContractCount = 12;
    private readonly IReadOnlyList<(DateTime Date, string Path)> _files;
    private readonly IReadOnlyDictionary<DateTime, string> _fileByDate;
    private readonly DateTime _newestSourceWriteUtc;
    private IReadOnlyDictionary<DateTime, double> _spotVix = new Dictionary<DateTime, double>();
    private HashSet<DateTime> _tradingDates = [];
    private IReadOnlyDictionary<DateTime, int> _tradingDateIndex = new Dictionary<DateTime, int>();
    private IReadOnlyList<(DateTime Date, string Path)> _tradingFiles = [];
    private VixTermSnapshot? _cachedSnapshot;

    private VixTermStructureStore(IReadOnlyList<(DateTime Date, string Path)> files)
    {
        _files = files;
        _fileByDate = files.ToDictionary(item => item.Date, item => item.Path);
        _newestSourceWriteUtc = files.Max(item => File.GetLastWriteTimeUtc(item.Path));
    }

    public int Count => _files.Count;
    public int TradingDateCount => _tradingFiles.Count;
    public int ExcludedNonTradingDateCount => Count - TradingDateCount;
    public DateTime FirstDate => _files[0].Date;
    public DateTime LastDate => _files[^1].Date;

    public void SetTradingCalendar(IEnumerable<DateTime> spyTradingDates)
    {
        _tradingDates = spyTradingDates.Select(date => date.Date).ToHashSet();
        if (_tradingDates.Count == 0)
            throw new InvalidOperationException("The SPY trading calendar is empty.");
        _tradingDateIndex = _tradingDates.OrderBy(date => date)
            .Select((date, index) => (date, index))
            .ToDictionary(item => item.date, item => item.index);
        _tradingFiles = _files.Where(item => _tradingDates.Contains(item.Date)).ToList();
        if (_tradingFiles.Count == 0)
            throw new InvalidOperationException("No VIX term files match actual SPY trading dates.");
        _cachedSnapshot = null;
    }

    public void SetSpotVix(IEnumerable<(DateTime Date, double Value)> values)
    {
        _spotVix = values
            .Where(item => item.Value > 0 && !double.IsNaN(item.Value) && !double.IsInfinity(item.Value))
            .Select(item => (item.Date.Date, item.Value))
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.Last().Value);
        _cachedSnapshot = null;
    }

    public static DateTime? FindFirstSourceDate(string directory)
    {
        if (!Directory.Exists(directory))
            return null;
        DateTime? first = null;
        foreach (string path in Directory.EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date) && (!first.HasValue || date.Date < first.Value))
                first = date.Date;
        }
        return first;
    }

    public static VixTermStructureStore BuildIndex(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"VIX futures directory was not found: {directory}");

        var files = new List<(DateTime Date, string Path)>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(path);
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date))
                files.Add((date.Date, path));
        }

        files.Sort((left, right) => left.Date.CompareTo(right.Date));
        if (files.Count == 0)
            throw new InvalidOperationException($"No dated VIX futures CSV files were found in {directory}");
        return new VixTermStructureStore(files);
    }

    public VixTermSnapshot GetExact(DateTime requestedDate)
    {
        EnsureTradingCalendar();
        DateTime tradeDate = requestedDate.Date;
        if (!_tradingDates.Contains(tradeDate))
            throw new InvalidOperationException($"{tradeDate:yyyy-MM-dd} is not an actual SPY trading date.");
        if (!_fileByDate.TryGetValue(tradeDate, out string? path))
            throw new InvalidOperationException($"No exact VIX term file exists for SPY trading date {tradeDate:yyyy-MM-dd}.");
        if (_cachedSnapshot?.TradeDate == tradeDate)
            return _cachedSnapshot;

        IReadOnlyList<VixTermPoint> points = ReadPoints(path);
        if (points.Count == 0)
            throw new InvalidDataException($"VIX futures file contains no valid rows: {path}");
        _cachedSnapshot = new VixTermSnapshot(tradeDate, tradeDate, points, FindExactSpot(tradeDate));
        return _cachedSnapshot;
    }

    public VixCurveStateSeries BuildStateSeries(
        CancellationToken cancellationToken,
        IReadOnlyDictionary<DateTime, VixMarketStressContext>? marketStressByDate = null)
    {
        EnsureTradingCalendar();
        var points = new List<VixCurveStatePoint>(_tradingFiles.Count);
        int activeSvxyBars = -1;
        int strongCandidateRun = 0;
        int weakGreenCandidateRun = 0;
        int? previousTradingDateIndex = null;
        bool previousTradingSessionHadPoint = false;
        foreach ((DateTime date, string path) in _tradingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentTradingDateIndex = _tradingDateIndex[date];
            bool followsPreviousTradingSession = previousTradingSessionHadPoint &&
                                                 previousTradingDateIndex.HasValue &&
                                                 currentTradingDateIndex == previousTradingDateIndex.Value + 1;
            if (previousTradingDateIndex.HasValue && !followsPreviousTradingSession)
            {
                strongCandidateRun = 0;
                weakGreenCandidateRun = 0;
                activeSvxyBars = -1;
            }
            IReadOnlyList<VixTermPoint> termPoints = ReadPoints(path);
            if (termPoints.Count == 0)
            {
                previousTradingDateIndex = currentTradingDateIndex;
                previousTradingSessionHadPoint = false;
                continue;
            }
            VixMarketStressContext? marketStress = null;
            marketStressByDate?.TryGetValue(date, out marketStress);
            VixCurveStatePoint point = VixCurveStateClassifier.Classify(
                date, termPoints, FindExactSpot(date), marketStress);
            VixCurveState targetState = point.State;
            VixCurveState previousState = points.Count == 0 || !followsPreviousTradingSession
                ? VixCurveState.Unknown
                : points[^1].State;
            bool confirmationApplied = false;
            string confirmationText = string.Empty;

            if (targetState == VixCurveState.StrongContango)
            {
                strongCandidateRun++;
                weakGreenCandidateRun = 0;
                if (previousState != VixCurveState.StrongContango && strongCandidateRun < 2)
                {
                    point = point with
                    {
                        State = VixCurveState.Contango,
                        CurveRegimeScore = Math.Min(point.CurveRegimeScore, 0.599999)
                    };
                    confirmationApplied = true;
                    confirmationText = "первый благоприятный снимок сохранён светло-зелёным; для New требуется второе подтверждение";
                }
            }
            else if (targetState == VixCurveState.Contango)
            {
                strongCandidateRun = 0;
                if (previousState == VixCurveState.StrongContango && !point.SpotStress && !point.PriceStress)
                {
                    weakGreenCandidateRun++;
                    if (weakGreenCandidateRun < 2)
                    {
                        point = point with
                        {
                            State = VixCurveState.StrongContango,
                            CurveRegimeScore = Math.Max(point.CurveRegimeScore, 0.60)
                        };
                        confirmationApplied = true;
                        confirmationText = "первое слабое геометрическое отклонение не меняет New; понижение требует второго подтверждения";
                    }
                }
                else
                    weakGreenCandidateRun = 0;
            }
            else
            {
                strongCandidateRun = 0;
                weakGreenCandidateRun = 0;
            }

            if (confirmationApplied)
            {
                point = point with
                {
                    TransitionConfirmationApplied = true,
                    AdjustmentExplanation = string.Join("; ", new[]
                    {
                        point.AdjustmentExplanation,
                        confirmationText
                    }.Where(text => !string.IsNullOrWhiteSpace(text)))
                };
            }
            bool priorWasRed = followsPreviousTradingSession && points.Count > 0 &&
                               points[^1].CurveRegimeScore <= -0.18;
            bool exitsRed = priorWasRed && point.CurveRegimeScore > -0.18;
            SvxyRecommendation recommendation;
            int? barsSinceCandidate;
            if (exitsRed)
            {
                activeSvxyBars = 0;
                recommendation = SvxyRecommendation.StrongBuy;
                barsSinceCandidate = 0;
            }
            else if (point.CurveRegimeScore <= -0.18)
            {
                activeSvxyBars = -1;
                recommendation = SvxyRecommendation.Neutral;
                barsSinceCandidate = null;
            }
            else if (activeSvxyBars >= 0)
            {
                activeSvxyBars++;
                recommendation = activeSvxyBars <= 60
                    ? SvxyRecommendation.Buy
                    : SvxyRecommendation.ExtendedHold;
                barsSinceCandidate = activeSvxyBars;
            }
            else
            {
                recommendation = SvxyRecommendation.Neutral;
                barsSinceCandidate = null;
            }
            points.Add(point with
            {
                SvxyBuyCandidate = exitsRed,
                SvxyRecommendation = recommendation,
                SvxyBarsSinceCandidate = barsSinceCandidate
            });
            previousTradingDateIndex = currentTradingDateIndex;
            previousTradingSessionHadPoint = true;
        }
        return new VixCurveStateSeries(points);
    }

    public string ExportStateCsv(string outputPath, CancellationToken cancellationToken) =>
        ExportStateCsv(outputPath, BuildStateSeries(cancellationToken), cancellationToken);

    public string ExportStateCsv(
        string outputPath,
        VixCurveStateSeries series,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryPath = outputPath + ".tmp";
        using (var writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("Date,State,LegacyState,M0_VIX,M1_Price,M2_Price,M0ToM1Pct,M1ToM2Pct,Price30,Price60,Contango30To60Pct,RisingPairs,FallingPairs,PairCount,RisingPairShare,CurveSlope30DayPct,CurveRegimeScore,LegacyCurveRegimeScore,SpotToM1Pct,IgnoredMinorInversions,LargestIgnoredInversionPct,MaximumTailDrawdownPct,BroadFrontInversion,SpotStress,PriceStress,TransitionConfirmationApplied,AdjustmentExplanation,SVXYBuyCandidate,SVXYRecommendation,SVXYBarsSinceCandidate");
            foreach (VixCurveStatePoint point in series.Points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(VixCurveStateClassifier.Label(point.State));
                writer.Write(',');
                writer.Write(VixCurveStateClassifier.Label(point.LegacyState));
                WriteNullable(writer, point.SpotVix);
                WriteNullable(writer, point.M1Price);
                WriteNullable(writer, point.M2Price);
                WriteNullable(writer, point.M0ToM1Pct);
                WriteNullable(writer, point.M1ToM2Pct);
                writer.Write(',');
                writer.Write(point.Price30.ToString("0.########", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.Price60.ToString("0.########", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.Contango30To60Pct.ToString("0.########", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.RisingAdjacentPairs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.FallingAdjacentPairs.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.AdjacentPairCount.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.RisingPairShare.ToString("0.########", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.CurveSlope30DayPct.ToString("0.########", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.CurveRegimeScore.ToString("0.########", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.LegacyCurveRegimeScore.ToString("0.########", CultureInfo.InvariantCulture));
                WriteNullable(writer, point.SpotToM1Pct);
                writer.Write(',');
                writer.Write(point.IgnoredMinorInversions.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.LargestIgnoredInversionPct.ToString("0.########", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.MaximumTailDrawdownPct.ToString("0.########", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(point.BroadFrontInversion ? "1" : "0");
                writer.Write(',');
                writer.Write(point.SpotStress ? "1" : "0");
                writer.Write(',');
                writer.Write(point.PriceStress ? "1" : "0");
                writer.Write(',');
                writer.Write(point.TransitionConfirmationApplied ? "1" : "0");
                writer.Write(',');
                writer.Write('"');
                writer.Write(point.AdjustmentExplanation.Replace("\"", "\"\""));
                writer.Write('"');
                writer.Write(',');
                writer.Write(point.SvxyBuyCandidate ? "1" : "0");
                writer.Write(',');
                writer.Write(point.SvxyRecommendation);
                writer.Write(',');
                writer.WriteLine(point.SvxyBarsSinceCandidate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }
        File.Move(temporaryPath, outputPath, true);
        return $"{Path.GetFileName(outputPath)}: {series.Points.Count:N0} dated curve states";
    }

    private static void WriteNullable(TextWriter writer, double? value)
    {
        writer.Write(',');
        if (value.HasValue)
            writer.Write(value.Value.ToString("0.########", CultureInfo.InvariantCulture));
    }

    public string ExportWideCsv(string outputPath, CancellationToken cancellationToken)
    {
        EnsureTradingCalendar();
        string expectedHeader = BuildExportHeader();
        if (File.Exists(outputPath))
        {
            string? existingHeader = File.ReadLines(outputPath).FirstOrDefault();
            if (string.Equals(existingHeader, expectedHeader, StringComparison.Ordinal) &&
                !ExportContainsNonTradingDates(outputPath))
            {
                DateTime outputWriteUtc = File.GetLastWriteTimeUtc(outputPath);
                string? lastLine = File.ReadLines(outputPath).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
                string? firstToken = lastLine?.Split(',').FirstOrDefault();
                if (DateTime.TryParseExact(firstToken, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime lastExportedDate))
                {
                    string[] lastTokens = lastLine!.Split(',');
                    bool spotNeedsBackfill = _spotVix.Count > 0 &&
                                             (lastTokens.Length < 2 || string.IsNullOrWhiteSpace(lastTokens[1]));
                    bool historicalSourceChanged = _tradingFiles.Any(item =>
                        item.Date <= lastExportedDate && File.GetLastWriteTimeUtc(item.Path) > outputWriteUtc);
                    if (!historicalSourceChanged && !spotNeedsBackfill)
                    {
                        IReadOnlyList<(DateTime Date, string Path)> additions = _tradingFiles
                            .Where(item => item.Date > lastExportedDate)
                            .ToList();
                        if (additions.Count == 0)
                            return $"{Path.GetFileName(outputPath)} is current";

                        using var appendWriter = new StreamWriter(outputPath, true, new UTF8Encoding(false));
                        int appended = 0;
                        foreach ((DateTime date, string path) in additions)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            IReadOnlyList<VixTermPoint> points = ReadPoints(path);
                            if (points.Count == 0)
                                continue;
                            WriteExportRow(appendWriter, date, points);
                            appended++;
                        }
                        return $"{Path.GetFileName(outputPath)}: appended {appended:N0} dates";
                    }
                }
            }
        }

        var rows = new List<(DateTime Date, IReadOnlyList<VixTermPoint> Points)>(_tradingFiles.Count);
        foreach ((DateTime date, string path) in _tradingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<VixTermPoint> points = ReadPoints(path);
            if (points.Count == 0)
                continue;
            rows.Add((date, points));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryPath = outputPath + ".tmp";
        using (var writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(expectedHeader);

            foreach ((DateTime date, IReadOnlyList<VixTermPoint> points) in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteExportRow(writer, date, points);
            }
        }

        File.Move(temporaryPath, outputPath, true);
        return $"{Path.GetFileName(outputPath)} rebuilt: {rows.Count:N0} dates, {ExportContractCount} contracts + M0 VIX";
    }

    private static string BuildExportHeader()
    {
        var header = new StringBuilder("Date,M0_VIX");
        for (int contract = 1; contract <= ExportContractCount; contract++)
            header.Append($",M{contract}_Days,M{contract}_Price");
        header.Append(",Price30,Price60,Difference30To60,Contango30To60Pct");
        return header.ToString();
    }

    private void WriteExportRow(TextWriter writer, DateTime date, IReadOnlyList<VixTermPoint> points)
    {
        writer.Write(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.Write(',');
        double? spot = FindExactSpot(date);
        if (spot.HasValue)
            writer.Write(spot.Value.ToString("0.########", CultureInfo.InvariantCulture));

        for (int contract = 0; contract < ExportContractCount; contract++)
        {
            if (contract < points.Count)
            {
                writer.Write(',');
                writer.Write(points[contract].DaysToExpiration.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(points[contract].Price.ToString("0.########", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.Write(",,");
            }
        }

        var snapshot = new VixTermSnapshot(date, date, points, spot);
        writer.Write(',');
        writer.Write(snapshot.Price30.ToString("0.########", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(snapshot.Price60.ToString("0.########", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(snapshot.Difference30To60.ToString("0.########", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write((snapshot.Contango30To60 * 100.0).ToString("0.########", CultureInfo.InvariantCulture));
        writer.WriteLine();
    }

    private double? FindExactSpot(DateTime date)
    {
        return _spotVix.TryGetValue(date.Date, out double value) ? value : null;
    }

    private void EnsureTradingCalendar()
    {
        if (_tradingDates.Count == 0)
            throw new InvalidOperationException(
                "SPY trading dates must be supplied before VIX states or exports are calculated.");
    }

    private bool ExportContainsNonTradingDates(string outputPath)
    {
        foreach (string line in File.ReadLines(outputPath).Skip(1))
        {
            string token = line.Split(',', 2)[0];
            if (DateTime.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date) && !_tradingDates.Contains(date.Date))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<VixTermPoint> ReadPoints(string path)
    {
        var points = new List<VixTermPoint>();
        string[] lines = File.ReadAllLines(path);
        for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            string? line = lines[lineIndex]?.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string[] parts = line.Split(',', ';', '\t');
            if (parts.Length < 2 ||
                !int.TryParse(Clean(parts[0]), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) ||
                !double.TryParse(Clean(parts[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out double price) ||
                days < 0 || price <= 0)
                continue;
            points.Add(new VixTermPoint(days, price));
        }

        points.Sort((left, right) => left.DaysToExpiration.CompareTo(right.DaysToExpiration));
        return points;
    }

    private static string Clean(string value) => value.Trim().Trim('"');
}
