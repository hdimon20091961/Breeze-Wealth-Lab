using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WealthLab.Backtest;
using WealthLab.Core;

namespace BreezeHoldPosition.Strategies;

/// <summary>
/// Optimization-ready SVXY strategy. It reconstructs the Old or New VIX
/// curve score from the exported daily term structure, then trades either
/// both green zones or Strong contango only. Position size is delegated to
/// the standard Wealth-Lab Position Size settings.
/// </summary>
public sealed class SVXY_contango : UserStrategyBase
{
    public const string DefaultDataDirectory = @"D:\fear_greed_close";

    private const double FrontTolerancePct = 0.50;
    private const double TailTolerancePct = 1.00;
    private const double TailCumulativeLimitPct = 1.50;

    private readonly Dictionary<DateTime, CurveInput> _curveByDate = new();
    private readonly Dictionary<DateTime, CurveContext> _contextByDate = new();
    private readonly Dictionary<DateTime, CurveZone> _zoneByDate = new();
    private TimeSeries _positionSeries = null!;

    private int _calculationMode;
    private int _greenZoneMode;
    private double _balanceWeight;
    private double _slopeScale;
    private double _greenThreshold;
    private double _strongThreshold;

    public override string StrategyName => "SVXY_contango";

    public override string Description =>
        "Optimization-ready SVXY strategy. Contango mode 0 reconstructs Old, " +
        "mode 1 reconstructs New. Green-zone mode 0 trades Contango and Strong " +
        "contango; mode 1 trades Strong contango only. Wealth-Lab controls size.";

    public SVXY_contango()
    {
        AddParameter("Contango (0=Old, 1=New)", ParameterType.Int32, 1, 0, 1, 1);
        AddParameter("Green zones (0=Both, 1=Dark)", ParameterType.Int32, 1, 0, 1, 1);
        AddParameter("Curve geometry weight", ParameterType.Double, 0.60, 0.35, 0.85, 0.05);
        AddParameter("30-60 slope scale", ParameterType.Double, 8.0, 2.0, 12.0, 1.0);
        AddParameter("Light-green threshold", ParameterType.Double, 0.18, 0.00, 0.40, 0.02);
        AddParameter("Dark-green threshold", ParameterType.Double, 0.85, 0.45, 0.98, 0.01);
        StartIndex = 0;
    }

    public override void Initialize(BarHistory bars)
    {
        _calculationMode = Parameters[0].AsInt;
        _greenZoneMode = Parameters[1].AsInt;
        _balanceWeight = Parameters[2].AsDouble;
        _slopeScale = Parameters[3].AsDouble;
        _greenThreshold = Parameters[4].AsDouble;
        _strongThreshold = Parameters[5].AsDouble;

        if (_strongThreshold <= _greenThreshold)
            throw new InvalidOperationException(
                "Dark-green threshold must be greater than Light-green threshold.");

        LoadCurveInputs(Path.Combine(DefaultDataDirectory, "vix_term_structure.csv"));
        LoadCurveContexts(Path.Combine(
            DefaultDataDirectory,
            "Breeze",
            "contango_market_state.csv"));
        BuildZones();

        int matchingBars = bars.DateTimes.Count(date => _zoneByDate.ContainsKey(date.Date));
        if (matchingBars == 0)
        {
            throw new InvalidOperationException(
                $"Contango source data has no dates in common with {bars.Symbol}.");
        }

        _positionSeries = new TimeSeries(bars.DateTimes, false);
        foreach (DateTime date in bars.DateTimes)
            _positionSeries.Add(IsTradableGreen(date.Date) ? 1.0 : 0.0);

        const string pane = "SVXY Contango Position 0-1";
        PlotTimeSeries(
            _positionSeries,
            "SVXY position (0/1)",
            pane,
            WLColor.Green,
            PlotStyle.Line);
        DrawHorzLine(0.0, WLColor.Gray, 1, LineStyle.Dotted, pane, false);
        DrawHorzLine(1.0, WLColor.Gray, 1, LineStyle.Dotted, pane, false);
        SetPaneMinMax(pane, 0.0, 1.0);
        SetPaneDrawingOptions(pane, 25);

        PlotZoneRibbon(
            bars,
            "Contango optimized",
            $"Strategy: {(_calculationMode == 0 ? "Old" : "New")}; " +
            $"w={_balanceWeight:F2}; s={_slopeScale:F2}; " +
            $"light={_greenThreshold:F2}; dark={_strongThreshold:F2}",
            date => _zoneByDate.TryGetValue(date, out CurveZone zone)
                ? zone
                : null);
        PlotZoneRibbon(
            bars,
            "Contango New official",
            "Contango New (Breeze)",
            date => _contextByDate.TryGetValue(date, out CurveContext? context)
                ? context.OfficialZone
                : null);

        WriteToDebugLog(
            $"SVXY_contango optimization: Contango={(_calculationMode == 0 ? "Old" : "New")}; " +
            $"zones={(_greenZoneMode == 0 ? "Both" : "Dark only")}; " +
            $"geometry weight={_balanceWeight:F2}; slope scale={_slopeScale:F2}; " +
            $"light={_greenThreshold:F2}; dark={_strongThreshold:F2}; " +
            $"{matchingBars} dates match {bars.Symbol}. Orders execute next open; " +
            "quantity is controlled by Wealth-Lab Position Size.");
    }

    public override void Execute(BarHistory bars, int idx)
    {
        bool green = IsTradableGreen(bars.DateTimes[idx].Date);
        bool hasLongPosition = HasOpenPosition(bars, PositionType.Long);

        if (green && !hasLongPosition)
        {
            PlaceTrade(
                bars,
                TransactionType.Buy,
                OrderType.Market,
                0,
                $"SVXY {_zoneByDate[bars.DateTimes[idx].Date]}: enter long");
        }
        else if (!green && hasLongPosition)
        {
            Position? position = FindOpenPositionAllSymbols(
                bars.Symbol,
                PositionType.Long);
            if (position != null)
            {
                ClosePosition(
                    position,
                    OrderType.Market,
                    0,
                    "SVXY: leave selected green zone");
            }
        }
    }

    private bool IsTradableGreen(DateTime date)
    {
        if (!_zoneByDate.TryGetValue(date, out CurveZone zone))
            return false;
        return zone == CurveZone.StrongContango ||
               (_greenZoneMode == 0 && zone == CurveZone.Contango);
    }

    private void BuildZones()
    {
        _zoneByDate.Clear();
        int strongCandidateRun = 0;
        int weakGreenCandidateRun = 0;
        CurveZone previousZone = CurveZone.Mixed;

        foreach (CurveInput input in _curveByDate.Values.OrderBy(value => value.Date))
        {
            _contextByDate.TryGetValue(input.Date, out CurveContext? context);
            CurveZone target = _calculationMode == 0
                ? ClassifyOld(input)
                : ClassifyNew(input, context);

            CurveZone final = target;
            if (_calculationMode == 1)
            {
                if (target == CurveZone.StrongContango)
                {
                    strongCandidateRun++;
                    weakGreenCandidateRun = 0;
                    if (previousZone != CurveZone.StrongContango && strongCandidateRun < 2)
                        final = CurveZone.Contango;
                }
                else if (target == CurveZone.Contango)
                {
                    strongCandidateRun = 0;
                    bool stress = context is not null &&
                        (context.SpotStress || context.PriceStress || context.VixShockFrontInversion);
                    if (previousZone == CurveZone.StrongContango && !stress)
                    {
                        weakGreenCandidateRun++;
                        if (weakGreenCandidateRun < 2)
                            final = CurveZone.StrongContango;
                    }
                    else
                        weakGreenCandidateRun = 0;
                }
                else
                {
                    strongCandidateRun = 0;
                    weakGreenCandidateRun = 0;
                }
            }

            _zoneByDate[input.Date] = final;
            previousZone = final;
        }
    }

    private CurveZone ClassifyOld(CurveInput input)
    {
        int rising = 0;
        int falling = 0;
        for (int index = 1; index < input.Points.Count; index++)
        {
            double delta = input.Points[index].Price - input.Points[index - 1].Price;
            if (delta > 0.000001)
                rising++;
            else if (delta < -0.000001)
                falling++;
        }

        int pairCount = Math.Max(0, input.Points.Count - 1);
        if (pairCount == 0)
            return CurveZone.Mixed;

        double balance = (rising - falling) / (double)pairCount;
        return ZoneForScore(WeightedScore(balance, input.Contango30To60Pct));
    }

    private CurveZone ClassifyNew(CurveInput input, CurveContext? context)
    {
        int rising = 0;
        int falling = 0;
        var pairChanges = new List<double>();
        bool vixHighShock = context?.VixHighRisePct is >= 10.0;

        for (int index = 1; index < input.Points.Count; index++)
        {
            CurvePoint previous = input.Points[index - 1];
            CurvePoint current = input.Points[index];
            double changePct = previous.Price > 0
                ? 100.0 * (current.Price / previous.Price - 1.0)
                : 0.0;
            pairChanges.Add(changePct);
            double tolerance = index <= 2 ? FrontTolerancePct : TailTolerancePct;

            if (changePct > 0.000001)
                rising++;
            else if (index == 1 && vixHighShock && changePct < -0.000001)
                falling++;
            else if (changePct < -tolerance)
                falling++;
        }

        List<CurvePoint> tail = input.Points
            .Where(point => point.Days >= 90)
            .ToList();
        double maximumTailDrawdown = MaximumDrawdownPct(tail);
        bool significantTailPair = pairChanges.Skip(2)
            .Any(change => change < -TailTolerancePct);
        if (!significantTailPair && maximumTailDrawdown > TailCumulativeLimitPct)
            falling++;

        int meaningfulPairs = rising + falling;
        if (meaningfulPairs == 0)
            return CurveZone.Mixed;

        double balance = (rising - falling) / (double)meaningfulPairs;
        CurveZone zone = ZoneForScore(WeightedScore(balance, input.Contango30To60Pct));

        if (zone == CurveZone.StrongContango && context is not null &&
            (context.BroadFrontInversion || context.SpotStress || context.VixShockFrontInversion))
            zone = CurveZone.Contango;

        if ((zone is CurveZone.Contango or CurveZone.StrongContango) &&
            context?.PriceStress == true)
            zone = CurveZone.Mixed;

        return zone;
    }

    private double WeightedScore(double balance, double contango30To60Pct)
    {
        double slopeStrength = Math.Tanh(contango30To60Pct / _slopeScale);
        return _balanceWeight * balance + (1.0 - _balanceWeight) * slopeStrength;
    }

    private CurveZone ZoneForScore(double score) =>
        score >= _strongThreshold ? CurveZone.StrongContango
        : score >= _greenThreshold ? CurveZone.Contango
        : score <= -_strongThreshold ? CurveZone.StrongBackwardation
        : score <= -_greenThreshold ? CurveZone.Backwardation
        : CurveZone.Mixed;

    private void PlotZoneRibbon(
        BarHistory bars,
        string pane,
        string label,
        Func<DateTime, CurveZone?> zoneForDate)
    {
        var ribbon = new TimeSeries(bars.DateTimes, false);
        var zones = new CurveZone?[bars.Count];
        for (int index = 0; index < bars.Count; index++)
        {
            CurveZone? zone = zoneForDate(bars.DateTimes[index].Date);
            zones[index] = zone;
            ribbon.Add(zone.HasValue ? 1.0 : double.NaN);
        }

        PlotTimeSeries(ribbon, label, pane, WLColor.Gray, PlotStyle.Histogram);
        for (int index = 0; index < ribbon.Count; index++)
        {
            if (zones[index].HasValue)
                SetSeriesBarColor(ribbon, index, ZoneColor(zones[index].Value));
        }
        SetPaneMinMax(pane, 0.0, 1.0);
        SetPaneDrawingOptions(pane, 12);
    }

    private static WLColor ZoneColor(CurveZone zone) => zone switch
    {
        CurveZone.StrongContango => WLColor.FromRgb(24, 128, 64),
        CurveZone.Contango => WLColor.FromRgb(126, 190, 118),
        CurveZone.Mixed => WLColor.FromRgb(232, 151, 55),
        CurveZone.Backwardation => WLColor.FromRgb(231, 139, 120),
        CurveZone.StrongBackwardation => WLColor.FromRgb(196, 55, 55),
        _ => WLColor.Gray
    };

    private static double MaximumDrawdownPct(IReadOnlyList<CurvePoint> points)
    {
        double peak = 0.0;
        double maximum = 0.0;
        foreach (CurvePoint point in points)
        {
            if (point.Price > peak)
                peak = point.Price;
            else if (peak > 0)
                maximum = Math.Max(maximum, 100.0 * (1.0 - point.Price / peak));
        }
        return maximum;
    }

    private void LoadCurveInputs(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("VIX term-structure export was not found.", path);

        _curveByDate.Clear();
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (lineNumber == 1 || string.IsNullOrWhiteSpace(line))
                continue;

            string[] columns = line.Split(',');
            if (columns.Length < 30 ||
                !DateTime.TryParseExact(columns[0].Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                throw new FormatException($"Invalid VIX term-structure row {lineNumber}.");

            var points = new List<CurvePoint>();
            for (int contract = 0; contract < 12; contract++)
            {
                int daysColumn = 2 + contract * 2;
                int priceColumn = daysColumn + 1;
                if (priceColumn >= columns.Length)
                    break;
                if (int.TryParse(columns[daysColumn].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int days) &&
                    double.TryParse(columns[priceColumn].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double price) &&
                    days >= 0 && days <= 180 && price > 0)
                    points.Add(new CurvePoint(days, price));
            }

            if (!double.TryParse(columns[29].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double contangoPct))
                contangoPct = 0.0;

            _curveByDate[date.Date] = new CurveInput(
                date.Date,
                points.OrderBy(point => point.Days).ToList(),
                contangoPct);
        }

        if (_curveByDate.Count == 0)
            throw new InvalidOperationException("The VIX term-structure export contains no data rows.");
    }

    private void LoadCurveContexts(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Contango state export was not found.", path);

        _contextByDate.Clear();
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (lineNumber == 1 || string.IsNullOrWhiteSpace(line))
                continue;

            string[] columns = line.Split(',');
            if (columns.Length < 27 ||
                !DateTime.TryParseExact(columns[0].Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                throw new FormatException($"Invalid contango context row {lineNumber}.");

            _contextByDate[date.Date] = new CurveContext(
                ParseZone(columns[1]),
                ParseBoolean(columns[22]),
                ParseBoolean(columns[23]),
                ParseBoolean(columns[24]),
                ParseNullableDouble(columns[25]),
                ParseBoolean(columns[26]));
        }
    }

    private static bool ParseBoolean(string value) =>
        value.Trim() == "1" || bool.TryParse(value.Trim(), out bool parsed) && parsed;

    private static double? ParseNullableDouble(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double parsed)
            ? parsed
            : null;

    private static CurveZone ParseZone(string value) => value.Trim() switch
    {
        "Strong contango" => CurveZone.StrongContango,
        "Contango" => CurveZone.Contango,
        "Backwardation" => CurveZone.Backwardation,
        "Broad backwardation" => CurveZone.StrongBackwardation,
        _ => CurveZone.Mixed
    };

    private enum CurveZone
    {
        StrongBackwardation,
        Backwardation,
        Mixed,
        Contango,
        StrongContango
    }

    private sealed record CurvePoint(int Days, double Price);
    private sealed record CurveInput(
        DateTime Date,
        IReadOnlyList<CurvePoint> Points,
        double Contango30To60Pct);
    private sealed record CurveContext(
        CurveZone OfficialZone,
        bool BroadFrontInversion,
        bool SpotStress,
        bool PriceStress,
        double? VixHighRisePct,
        bool VixShockFrontInversion);
}
