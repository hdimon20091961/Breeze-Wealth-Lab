using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using WealthLab.Backtest;
using WealthLab.Core;

namespace BreezeHoldPosition.Strategies;

/// <summary>
/// Rebuilds Hold from dated Breeze component exports with independently
/// optimizable Breadth and Zweig influence. A signal read for bar idx is
/// submitted from Execute(idx), so Wealth-Lab fills it at the next bar's open.
/// </summary>
public sealed class BreezeHoldPositionStrategy : UserStrategyBase
{
    public const string DefaultDataDirectory = @"D:\fear_greed_close\Breeze";

    public override string StrategyName => "Breeze Hold Position";

    public override string Description =>
        "Engine 0 follows the 0-to-10 Target contracts series. Engine 1 uses a " +
        "base Buy level and an offset Sell level and switches between 0 and 10 contracts.";

    private IReadOnlyDictionary<DateTime, HoldInput> _holdInputs =
        new Dictionary<DateTime, HoldInput>();
    private IReadOnlyDictionary<DateTime, double> _holdByDate =
        new Dictionary<DateTime, double>();
    private TimeSeries _baseHoldLevel = null!;
    private TimeSeries _holdLevel = null!;
    private TimeSeries _baseHoldRibbon = null!;
    private TimeSeries _optimizedHoldRibbon = null!;
    private TimeSeries _targetContracts = null!;
    private TimeSeries _tradingTarget = null!;
    private int _engine;
    private int _baseLevel;
    private double _hysteresisOffset;
    private double _sellLevel;
    private double _breadthInfluence;
    private double _zweigInfluence;
    private double _trendInfluence;
    private double _riskPenaltyInfluence;
    private double _downSmoothing;
    private double _upSmoothing;

    public BreezeHoldPositionStrategy()
    {
        StartIndex = 0;
        AddParameter(
            "Engine (0=Target, 1=Level)",
            ParameterType.Int32,
            1,
            1,
            1,
            1);
        AddParameter(
            "Level Base (fixed 4)",
            ParameterType.Int32,
            4,
            4,
            4,
            1);
        AddParameter(
            "Level Hysteresis (2..3)",
            ParameterType.Double,
            2.5,
            2.0,
            3.0,
            0.5);
        AddParameter(
            "Breadth Influence (0..0.5)",
            ParameterType.Double,
            0.25,
            0.0,
            0.5,
            0.25);
        AddParameter(
            "Zweig Influence (0.25..0.75)",
            ParameterType.Double,
            0.5,
            0.25,
            0.75,
            0.25);
        AddParameter(
            "Trend Influence (1.5..2)",
            ParameterType.Double,
            1.75,
            1.5,
            2.0,
            0.25);
        AddParameter(
            "Risk Penalty Influence (1..1.5)",
            ParameterType.Double,
            1.25,
            1.0,
            1.5,
            0.25);
        AddParameter(
            "Down Smoothing (0.60..0.90)",
            ParameterType.Double,
            0.75,
            0.60,
            0.90,
            0.05);
        AddParameter(
            "Up Smoothing (0.30..0.60)",
            ParameterType.Double,
            0.45,
            0.30,
            0.60,
            0.05);
    }

    public override void Initialize(BarHistory bars)
    {
        _engine = Parameters[0].AsInt;
        _baseLevel = Parameters[1].AsInt;
        _hysteresisOffset = Parameters[2].AsDouble;
        _sellLevel = _baseLevel + _hysteresisOffset / 10.0;
        _breadthInfluence = Parameters[3].AsDouble;
        _zweigInfluence = Parameters[4].AsDouble;
        _trendInfluence = Parameters[5].AsDouble;
        _riskPenaltyInfluence = Parameters[6].AsDouble;
        _downSmoothing = Parameters[7].AsDouble;
        _upSmoothing = Parameters[8].AsDouble;
        _holdInputs = LoadHoldInputs(DefaultDataDirectory);
        _holdByDate = CalculateHoldValues(
            _holdInputs.Values.OrderBy(point => point.Date),
            _breadthInfluence,
            _zweigInfluence,
            _trendInfluence,
            _riskPenaltyInfluence,
            _downSmoothing,
            _upSmoothing);

        int matchingBars = bars.DateTimes.Count(date => _holdByDate.ContainsKey(date.Date));
        if (matchingBars == 0)
        {
            throw new InvalidOperationException(
                $"Breeze component data in '{DefaultDataDirectory}' has no dates in common with {bars.Symbol}.");
        }

        // This is the actual control series that Execute trades. Missing dates
        // carry the preceding target forward because no new Hold signal exists.
        _baseHoldLevel = new TimeSeries(bars.DateTimes, false);
        _holdLevel = new TimeSeries(bars.DateTimes, false);
        _baseHoldRibbon = new TimeSeries(bars.DateTimes, false);
        _optimizedHoldRibbon = new TimeSeries(bars.DateTimes, false);
        _targetContracts = new TimeSeries(bars.DateTimes, false);
        double carriedBaseHoldLevel = 0.0;
        double carriedHoldLevel = 0.0;
        int carriedTarget = 0;
        foreach (DateTime date in bars.DateTimes)
        {
            if (_holdInputs.TryGetValue(date.Date, out HoldInput? input))
                carriedBaseHoldLevel = input.OriginalHold / 10.0;
            if (_holdByDate.TryGetValue(date.Date, out double hold))
            {
                carriedHoldLevel = hold / 10.0;
                carriedTarget = TargetContracts(hold);
            }
            _baseHoldLevel.Add(carriedBaseHoldLevel);
            _holdLevel.Add(carriedHoldLevel);
            _baseHoldRibbon.Add(1.0);
            _optimizedHoldRibbon.Add(1.0);
            _targetContracts.Add(carriedTarget);
        }

        // Engine 0 trades the source target literally. Engine 1 is a two-level
        // state machine. While flat, only a rise above Buy opens 10 contracts.
        // While invested, only a fall below Sell closes them. Inside the band
        // (and exactly on either threshold) the preceding state is retained.
        _tradingTarget = new TimeSeries(bars.DateTimes, false);
        int switchedTarget = 0;
        double previousHoldLevel = 0.0;
        for (int idx = 0; idx < bars.Count; idx++)
        {
            int sourceTarget = (int)_targetContracts[idx];
            if (_engine == 0)
            {
                switchedTarget = sourceTarget;
            }
            else
            {
                double holdLevel = _holdLevel[idx];
                bool crossedBuyUp =
                    previousHoldLevel <= _baseLevel && holdLevel > _baseLevel;
                bool crossedSellDown =
                    previousHoldLevel >= _sellLevel && holdLevel < _sellLevel;

                if (switchedTarget == 0 && crossedBuyUp)
                    switchedTarget = 10;
                else if (switchedTarget == 10 && crossedSellDown)
                    switchedTarget = 0;
            }

            _tradingTarget.Add(switchedTarget);
            previousHoldLevel = _holdLevel[idx];
        }

        const string pane = "Breeze Position 0-10";
        if (_engine == 0)
        {
            PlotTimeSeries(
                _targetContracts,
                "Target contracts",
                pane,
                WLColor.Blue,
                PlotStyle.Line);
        }
        else
        {
            PlotTimeSeries(
                _holdLevel,
                "Hold level / 10",
                pane,
                WLColor.Blue,
                PlotStyle.Line);
            PlotTimeSeries(
                _tradingTarget,
                "Trading target 0/10",
                pane,
                WLColor.Orange,
                PlotStyle.Line);
            DrawHorzLine(_baseLevel, WLColor.Green, 1, LineStyle.Dashed, pane, false);
            DrawHorzLine(_sellLevel, WLColor.Red, 1, LineStyle.Dashed, pane, false);
        }
        DrawHorzLine(0.0, WLColor.Gray, 1, LineStyle.Dotted, pane, false);
        DrawHorzLine(10.0, WLColor.Gray, 1, LineStyle.Dotted, pane, false);
        SetPaneDrawingOptions(pane, 35);

        PlotHoldRibbon(
            _baseHoldRibbon,
            _baseHoldLevel,
            "Base Hold (frozen)",
            "Base Hold color");
        PlotHoldRibbon(
            _optimizedHoldRibbon,
            _holdLevel,
            "Optimized Hold",
            "Optimized Hold color");

        WriteToDebugLog(
            $"Breeze Hold: loaded {_holdByDate.Count} dated values; " +
            $"{matchingBars} match {bars.Symbol}. Engine={_engine}, " +
            $"BuyBase={_baseLevel:F1}, Hysteresis={_hysteresisOffset:+0.0;-0.0;0.0} pp, " +
            $"Sell={_sellLevel:F2}, Breadth x{_breadthInfluence:F2}, " +
            $"Zweig x{_zweigInfluence:F2}, Trend x{_trendInfluence:F2}, " +
            $"Risk penalty x{_riskPenaltyInfluence:F2}, " +
            $"Down smoothing {_downSmoothing:F2}, Up smoothing {_upSmoothing:F2}. " +
            "Signals execute at the next bar's market open.");
    }

    public override void Execute(BarHistory bars, int idx)
    {
        // _tradingTarget is the exact series being traded by the selected engine.
        int desiredQuantity = (int)_tradingTarget[idx];
        int currentQuantity = (int)OpenQuantity;

        // Match the proven TorchSharp mechanism literally: every PlaceTrade is
        // one contract/position, and the loop balances OpenQuantity to the
        // 0..10 target series on every bar.
        while (currentQuantity < desiredQuantity)
        {
            PlaceTrade(
                bars,
                TransactionType.Buy,
                OrderType.Market,
                0,
                $"Breeze target {desiredQuantity}/10; add {currentQuantity + 1}");
            currentQuantity++;
        }

        while (currentQuantity > desiredQuantity)
        {
            PlaceTrade(
                bars,
                TransactionType.Sell,
                OrderType.Market,
                0,
                $"Breeze target {desiredQuantity}/10; reduce {currentQuantity}");
            currentQuantity--;
        }
    }

    private void PlotHoldRibbon(
        TimeSeries ribbon,
        TimeSeries holdLevel,
        string pane,
        string label)
    {
        PlotTimeSeries(ribbon, label, pane, WLColor.Gray, PlotStyle.Histogram);
        for (int idx = 0; idx < ribbon.Count; idx++)
            SetSeriesBarColor(ribbon, idx, HoldColor(10.0 * holdLevel[idx]));
        SetPaneMinMax(pane, 0.0, 1.0);
        SetPaneDrawingOptions(pane, 18);
    }

    private static WLColor HoldColor(double holdPercent)
    {
        double value = Math.Clamp(holdPercent, 0.0, 100.0);
        byte fromR;
        byte fromG;
        byte fromB;
        byte toR;
        byte toG;
        byte toB;
        double t;

        if (value <= 50.0)
        {
            fromR = 196; fromG = 55; fromB = 55;
            toR = 242; toG = 201; toB = 76;
            t = value / 50.0;
        }
        else
        {
            fromR = 242; fromG = 201; fromB = 76;
            toR = 24; toG = 128; toB = 64;
            t = (value - 50.0) / 50.0;
        }

        byte Blend(byte from, byte to) =>
            (byte)Math.Round(from + (to - from) * t);
        return WLColor.FromRgb(
            Blend(fromR, toR),
            Blend(fromG, toG),
            Blend(fromB, toB));
    }

    /// <summary>
    /// Converts the continuous Hold value into the strategy's own 0..10
    /// position scale. This method deliberately lives in this standalone
    /// strategy and has no dependency on Breeze.dll.
    /// </summary>
    private static int TargetContracts(double holdPercent)
    {
        double clamped = Math.Clamp(holdPercent, 0.0, 100.0);
        if (clamped <= 0.0)
            return 0;
        if (clamped >= 90.0)
            return 10;
        return (int)Math.Ceiling(clamped / 10.0);
    }

    private static IReadOnlyDictionary<DateTime, HoldInput> LoadHoldInputs(string directory)
    {
        string componentsPath = Path.Combine(directory, "phase_model_components.csv");
        IReadOnlyDictionary<DateTime, double> spy =
            LoadCloseSeries(Path.Combine(directory, "SPY.csv"));
        IReadOnlyDictionary<DateTime, double> ema200 =
            LoadCloseSeries(Path.Combine(directory, "spy_ema200.csv"));
        IReadOnlyDictionary<DateTime, double> breadth50 =
            LoadCloseSeries(Path.Combine(directory, "sp500_sma50.csv"));
        IReadOnlyDictionary<DateTime, double> breadth100 =
            LoadCloseSeries(Path.Combine(directory, "sp500_sma100.csv"));
        IReadOnlyDictionary<DateTime, double> breadth200 =
            LoadCloseSeries(Path.Combine(directory, "sp500_sma200.csv"));
        IReadOnlyDictionary<DateTime, double> nasdaq100 =
            LoadCloseSeries(Path.Combine(directory, "nasdaq100_sma100.csv"));

        if (!File.Exists(componentsPath))
            throw new FileNotFoundException("Breeze component export was not found.", componentsPath);

        var result = new Dictionary<DateTime, HoldInput>();
        int lineNumber = 0;
        foreach (string line in File.ReadLines(componentsPath))
        {
            lineNumber++;
            if (lineNumber == 1 || string.IsNullOrWhiteSpace(line))
                continue;

            string[] columns = line.Split(',');
            if (columns.Length < 14)
                throw new FormatException($"Invalid Breeze component row {lineNumber}.");
            DateTime date = DateTime.ParseExact(
                columns[0],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None);

            if (!spy.TryGetValue(date.Date, out double spyClose) ||
                !ema200.TryGetValue(date.Date, out double ema200Value) ||
                !breadth50.TryGetValue(date.Date, out double b50) ||
                !breadth100.TryGetValue(date.Date, out double b100) ||
                !breadth200.TryGetValue(date.Date, out double b200) ||
                !nasdaq100.TryGetValue(date.Date, out double ndx100))
                throw new InvalidOperationException($"Missing Breeze component input for {date:yyyy-MM-dd}.");

            double breadthAverage = 0.30 * b50 + 0.25 * b100 + 0.25 * b200 + 0.20 * ndx100;
            result[date.Date] = new HoldInput(
                date.Date,
                ParseNumber(columns[4]),
                columns[1],
                ParseNumber(columns[6]),
                ParseNumber(columns[7]),
                ParseNumber(columns[8]),
                ParseNumber(columns[9]),
                ParseNumber(columns[10]),
                ParseNumber(columns[11]),
                ParseNumber(columns[12]),
                ParseNumber(columns[13]),
                spyClose < ema200Value && breadthAverage < 30.0);
        }

        if (result.Count == 0)
            throw new InvalidOperationException("The Breeze component export contains no data rows.");

        return result;
    }

    private static IReadOnlyDictionary<DateTime, double> LoadCloseSeries(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("A required Breeze source series was not found.", path);

        var result = new Dictionary<DateTime, double>();
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (lineNumber == 1 || string.IsNullOrWhiteSpace(line))
                continue;
            string[] columns = line.Split(',');
            if (columns.Length < 5)
                throw new FormatException($"Invalid CSV row {lineNumber} in '{path}'.");
            DateTime date = DateTime.ParseExact(
                columns[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);
            result[date.Date] = ParseNumber(columns[4]);
        }
        return result;
    }

    private static IReadOnlyDictionary<DateTime, double> CalculateHoldValues(
        IEnumerable<HoldInput> source,
        double breadthInfluence,
        double zweigInfluence,
        double trendInfluence,
        double riskPenaltyInfluence,
        double downSmoothing,
        double upSmoothing)
    {
        double breadthWeight = 0.25 * breadthInfluence;
        double zweigWeight = 0.05 * zweigInfluence;
        double trendWeight = 0.30 * trendInfluence;
        double positiveWeightTotal = trendWeight + 0.20 + 0.10 + 0.10 + breadthWeight + zweigWeight;
        var result = new Dictionary<DateTime, double>();
        double? previous = null;

        foreach (HoldInput point in source)
        {
            double positive =
                (trendWeight * point.Trend +
                 breadthWeight * point.Breadth +
                 0.20 * point.Phase +
                 0.10 * point.Volatility +
                 0.10 * point.Contango +
                 zweigWeight * point.Zweig) /
                positiveWeightTotal;
            double value = 100.0 * Math.Clamp(
                positive - riskPenaltyInfluence *
                (0.20 * point.TitanicPenalty + 0.25 * point.ShockPenalty),
                0.0,
                1.0);

            if (point.ZoneLabel == "Strong sell")
                value = Math.Min(value, 15.0);
            else if (point.ZoneLabel == "Sell")
                value = Math.Min(value, 39.0);
            if (point.DeepRiskCap)
                value = Math.Min(value, 10.0);

            if (previous.HasValue)
            {
                double currentWeight = value < previous.Value ? downSmoothing : upSmoothing;
                value = currentWeight * value + (1.0 - currentWeight) * previous.Value;
            }
            value = Math.Clamp(value, 0.0, 100.0);
            previous = value;
            result[point.Date] = value;
        }

        return result;
    }

    private static double ParseNumber(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private sealed record HoldInput(
        DateTime Date,
        double OriginalHold,
        string ZoneLabel,
        double Trend,
        double Breadth,
        double Phase,
        double Volatility,
        double Contango,
        double Zweig,
        double TitanicPenalty,
        double ShockPenalty,
        bool DeepRiskCap);
}
