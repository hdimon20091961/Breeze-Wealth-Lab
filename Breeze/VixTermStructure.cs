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
    int? SvxyBarsSinceCandidate);

internal sealed record VixCurveStateSeries(IReadOnlyList<VixCurveStatePoint> Points);

internal static class VixCurveStateClassifier
{
    public const int MaximumDays = 180;

    public static VixCurveStatePoint Classify(
        DateTime date,
        IReadOnlyList<VixTermPoint> sourcePoints,
        double? spotVix)
    {
        IReadOnlyList<VixTermPoint> points = sourcePoints
            .Where(point => point.DaysToExpiration <= MaximumDays)
            .OrderBy(point => point.DaysToExpiration)
            .ToList();
        var snapshot = new VixTermSnapshot(date, date, points, spotVix);
        int rising = 0;
        int falling = 0;
        for (int index = 1; index < points.Count; index++)
        {
            double delta = points[index].Price - points[index - 1].Price;
            if (delta > 0.000001)
                rising++;
            else if (delta < -0.000001)
                falling++;
        }

        int pairs = Math.Max(0, points.Count - 1);
        double risingShare = pairs == 0 ? 0.0 : rising / (double)pairs;
        double contangoPct = points.Count == 0 ? 0.0 : snapshot.Contango30To60 * 100.0;
        double adjacentBalance = pairs == 0 ? 0.0 : (rising - falling) / (double)pairs;
        double slopeStrength = Math.Tanh(contangoPct / 5.0);
        double regimeScore = 0.65 * adjacentBalance + 0.35 * slopeStrength;
        VixCurveState state = pairs == 0
            ? VixCurveState.Unknown
            : regimeScore >= 0.60
                ? VixCurveState.StrongContango
                : regimeScore >= 0.18
                    ? VixCurveState.Contango
                    : regimeScore <= -0.60
                        ? VixCurveState.StrongBackwardation
                        : regimeScore <= -0.18
                            ? VixCurveState.Backwardation
                            : VixCurveState.Mixed;

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
            null);
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
    private readonly DateTime _newestSourceWriteUtc;
    private IReadOnlyList<(DateTime Date, double Value)> _spotVix = [];
    private VixTermSnapshot? _cachedSnapshot;

    private VixTermStructureStore(IReadOnlyList<(DateTime Date, string Path)> files)
    {
        _files = files;
        _newestSourceWriteUtc = files.Max(item => File.GetLastWriteTimeUtc(item.Path));
    }

    public int Count => _files.Count;
    public DateTime FirstDate => _files[0].Date;
    public DateTime LastDate => _files[^1].Date;

    public void SetSpotVix(IEnumerable<(DateTime Date, double Value)> values)
    {
        _spotVix = values
            .Where(item => item.Value > 0 && !double.IsNaN(item.Value) && !double.IsInfinity(item.Value))
            .Select(item => (item.Date.Date, item.Value))
            .OrderBy(item => item.Date)
            .ToList();
        _cachedSnapshot = null;
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

    public VixTermSnapshot GetNearest(DateTime requestedDate)
    {
        int index = FindNearestFileIndex(requestedDate.Date);
        (DateTime tradeDate, string path) = _files[index];
        if (_cachedSnapshot?.TradeDate == tradeDate)
            return _cachedSnapshot with { RequestedDate = requestedDate.Date };

        IReadOnlyList<VixTermPoint> points = ReadPoints(path);
        if (points.Count == 0)
            throw new InvalidDataException($"VIX futures file contains no valid rows: {path}");
        _cachedSnapshot = new VixTermSnapshot(requestedDate.Date, tradeDate, points, FindNearestSpot(tradeDate));
        return _cachedSnapshot;
    }

    public VixCurveStateSeries BuildStateSeries(CancellationToken cancellationToken)
    {
        var points = new List<VixCurveStatePoint>(_files.Count);
        int activeSvxyBars = -1;
        foreach ((DateTime date, string path) in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<VixTermPoint> termPoints = ReadPoints(path);
            if (termPoints.Count == 0)
                continue;
            VixCurveStatePoint point = VixCurveStateClassifier.Classify(
                date, termPoints, FindNearestSpot(date));
            bool priorWasRed = points.Count > 0 && points[^1].CurveRegimeScore <= -0.18;
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
        }
        return new VixCurveStateSeries(points);
    }

    public string ExportStateCsv(string outputPath, CancellationToken cancellationToken)
    {
        VixCurveStateSeries series = BuildStateSeries(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryPath = outputPath + ".tmp";
        using (var writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("Date,State,M0_VIX,M1_Price,M2_Price,M0ToM1Pct,M1ToM2Pct,Price30,Price60,Contango30To60Pct,RisingPairs,FallingPairs,PairCount,RisingPairShare,CurveSlope30DayPct,CurveRegimeScore,SVXYBuyCandidate,SVXYRecommendation,SVXYBarsSinceCandidate");
            foreach (VixCurveStatePoint point in series.Points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(VixCurveStateClassifier.Label(point.State));
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
        string expectedHeader = BuildExportHeader();
        if (File.Exists(outputPath))
        {
            string? existingHeader = File.ReadLines(outputPath).FirstOrDefault();
            if (string.Equals(existingHeader, expectedHeader, StringComparison.Ordinal))
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
                    bool historicalSourceChanged = _files.Any(item =>
                        item.Date <= lastExportedDate && File.GetLastWriteTimeUtc(item.Path) > outputWriteUtc);
                    if (!historicalSourceChanged && !spotNeedsBackfill)
                    {
                        IReadOnlyList<(DateTime Date, string Path)> additions = _files
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

        var rows = new List<(DateTime Date, IReadOnlyList<VixTermPoint> Points)>(_files.Count);
        foreach ((DateTime date, string path) in _files)
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
        double? spot = FindNearestSpot(date);
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

    private double? FindNearestSpot(DateTime date)
    {
        if (_spotVix.Count == 0)
            return null;

        int low = 0;
        int high = _spotVix.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            DateTime candidate = _spotVix[middle].Date;
            if (candidate == date)
                return _spotVix[middle].Value;
            if (candidate < date)
                low = middle + 1;
            else
                high = middle - 1;
        }

        if (low <= 0)
            return _spotVix[0].Value;
        if (low >= _spotVix.Count)
            return _spotVix[^1].Value;
        return (date - _spotVix[low - 1].Date) <= (_spotVix[low].Date - date)
            ? _spotVix[low - 1].Value
            : _spotVix[low].Value;
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

    private int FindNearestFileIndex(DateTime date)
    {
        int low = 0;
        int high = _files.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            DateTime candidate = _files[middle].Date;
            if (candidate == date)
                return middle;
            if (candidate < date)
                low = middle + 1;
            else
                high = middle - 1;
        }

        if (low <= 0)
            return 0;
        if (low >= _files.Count)
            return _files.Count - 1;
        return (date - _files[low - 1].Date) <= (_files[low].Date - date) ? low - 1 : low;
    }

    private static string Clean(string value) => value.Trim().Trim('"');
}
