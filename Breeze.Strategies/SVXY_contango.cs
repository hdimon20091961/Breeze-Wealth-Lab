using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using WealthLab.Backtest;
using WealthLab.Core;

namespace BreezeHoldPosition.Strategies;

/// <summary>
/// Holds exactly one SVXY contract while the exported VIX term-structure
/// state is green (Contango or Strong contango). All other states are flat.
/// This strategy is intentionally independent from Breeze.dll.
/// </summary>
public sealed class SVXY_contango : UserStrategyBase
{
    public const string DefaultDataDirectory = @"D:\fear_greed_close\Breeze";
    private IReadOnlyDictionary<DateTime, bool> _greenByDate =
        new Dictionary<DateTime, bool>();
    private TimeSeries _positionSeries = null!;

    public override string StrategyName => "SVXY_contango";

    public override string Description =>
        "One-contract SVXY strategy: hold during green Contango/Strong contango " +
        "states and remain outside the market otherwise.";

    public SVXY_contango()
    {
        StartIndex = 0;
    }

    public override void Initialize(BarHistory bars)
    {
        _greenByDate = LoadGreenStates(
            Path.Combine(DefaultDataDirectory, "contango_market_state.csv"));

        int matchingBars = 0;
        foreach (DateTime date in bars.DateTimes)
        {
            if (_greenByDate.ContainsKey(date.Date))
                matchingBars++;
        }

        if (matchingBars == 0)
        {
            throw new InvalidOperationException(
                $"Contango state data has no dates in common with {bars.Symbol}.");
        }

        _positionSeries = new TimeSeries(bars.DateTimes, false);
        foreach (DateTime date in bars.DateTimes)
        {
            bool green = _greenByDate.TryGetValue(date.Date, out bool state) && state;
            _positionSeries.Add(green ? 1.0 : 0.0);
        }

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

        WriteToDebugLog(
            $"SVXY_contango: loaded {_greenByDate.Count} dated states; " +
            $"{matchingBars} match {bars.Symbol}. " +
            "Green = Contango or Strong contango; signals execute at next bar open.");
    }

    public override void Execute(BarHistory bars, int idx)
    {
        bool green = _greenByDate.TryGetValue(bars.DateTimes[idx].Date, out bool state) && state;
        int targetQuantity = green ? 1 : 0;
        int currentQuantity = Math.Max(0, (int)OpenQuantity);

        if (currentQuantity < targetQuantity)
        {
            PlaceTrade(
                bars,
                TransactionType.Buy,
                OrderType.Market,
                0,
                "SVXY_contango green: hold 1 contract");
        }
        else if (currentQuantity > targetQuantity)
        {
            PlaceTrade(
                bars,
                TransactionType.Sell,
                OrderType.Market,
                0,
                "SVXY_contango non-green: exit");
        }
    }

    private static IReadOnlyDictionary<DateTime, bool> LoadGreenStates(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Contango state export was not found.", path);

        var result = new Dictionary<DateTime, bool>();
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (lineNumber == 1 || string.IsNullOrWhiteSpace(line))
                continue;

            string[] columns = line.Split(',');
            if (columns.Length < 2)
                throw new FormatException($"Invalid contango state row {lineNumber}.");

            DateTime date = DateTime.ParseExact(
                columns[0].Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None);
            string state = columns[1].Trim();
            bool green = state.Equals("Contango", StringComparison.OrdinalIgnoreCase) ||
                         state.Equals("Strong contango", StringComparison.OrdinalIgnoreCase);
            result[date.Date] = green;
        }

        if (result.Count == 0)
            throw new InvalidOperationException("The contango state export contains no data rows.");

        return result;
    }
}
