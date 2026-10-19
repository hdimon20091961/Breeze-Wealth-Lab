namespace BreezeVL;

internal sealed record HoldConfidencePoint(
    DateTime Date,
    double Value,
    double TrendComponent,
    double BreadthComponent,
    double PhaseComponent,
    double VolatilityComponent,
    double ContangoComponent,
    double ZweigComponent,
    double TitanicPenalty,
    double ShockPenalty,
    string Rationale,
    string Profile = "Legacy",
    string MainSupport = "",
    string MainRisk = "",
    string ActiveEvents = "");

internal sealed record HoldConfidenceSeries(IReadOnlyList<HoldConfidencePoint> Points);

internal static class HoldConfidenceCalculator
{
    // Selected from the broad Wealth-Lab optimization run (run 10502):
    // 196.88% profit, -7.80% maximum drawdown, Sharpe 1.373.
    // The component values remain unchanged; these constants only tune their
    // influence in the optimized Hold confidence series.
    private const double BreadthInfluence = 0.25;
    private const double ZweigInfluence = 0.75;
    private const double TrendInfluence = 2.0;
    private const double RiskPenaltyInfluence = 1.25;
    private const double DownSmoothing = 0.70;
    private const double UpSmoothing = 0.45;

    public static HoldConfidenceSeries Calculate(
        FuzzyMarketStateSeries fuzzyStates,
        TechnicalIndicatorSeries technicalIndicators,
        VixCurveStateSeries? curveStates,
        TitanicSeries? titanicSeries,
        ZweigSeries? zweigSeries)
    {
        var technicalByDate = technicalIndicators.Points.ToDictionary(point => point.Date);
        var curveByDate = curveStates?.Points.ToDictionary(point => point.Date) ?? [];
        var titanicByDate = titanicSeries?.Points.ToDictionary(point => point.Date) ?? [];
        var zweigByDate = zweigSeries?.Points.ToDictionary(point => point.Date) ?? [];
        var points = new List<HoldConfidencePoint>(fuzzyStates.Points.Count);
        double? previous = null;

        foreach (FuzzyMarketStatePoint fuzzy in fuzzyStates.Points)
        {
            if (!technicalByDate.TryGetValue(fuzzy.Date, out TechnicalIndicatorPoint? technical))
                continue;

            curveByDate.TryGetValue(fuzzy.Date, out VixCurveStatePoint? curve);
            titanicByDate.TryGetValue(fuzzy.Date, out TitanicPoint? titanic);
            zweigByDate.TryGetValue(fuzzy.Date, out ZweigPoint? zweig);

            double breadthAverage =
                0.30 * fuzzy.Sp50 + 0.25 * fuzzy.Sp100 +
                0.25 * fuzzy.Sp200 + 0.20 * fuzzy.Nasdaq100;
            double trend = Clamp01(fuzzy.BullishEmaOrder);
            double breadth = SmoothRamp(breadthAverage, 25.0, 75.0);
            double phase = Clamp01(0.50 + 0.50 * fuzzy.Score);
            double volatility = 1.0 - SmoothRamp(technical.Atrp1, 1.20, 3.50);
            double contango = curve is null || curve.State == VixCurveState.Unknown
                ? 0.50
                : Clamp01(0.50 + 0.50 * curve.CurveRegimeScore);
            double zweigSupport = zweig is null ? 0.0 : Clamp01(zweig.Density / 30.0);
            double titanicPenalty = titanic?.Signal == true
                ? titanic.Persistent ? 1.0 : 0.65
                : 0.0;
            double shockPenalty = Math.Max(
                fuzzy.ReduceRisk,
                Math.Max(fuzzy.StrongReduceRiskComplex, fuzzy.LargeRedDay));

            double trendWeight = 0.30 * TrendInfluence;
            double breadthWeight = 0.25 * BreadthInfluence;
            double zweigWeight = 0.05 * ZweigInfluence;
            double positiveWeightTotal =
                trendWeight + breadthWeight + 0.20 + 0.10 + 0.10 + zweigWeight;
            double positive =
                (trendWeight * trend +
                 breadthWeight * breadth +
                 0.20 * phase +
                 0.10 * volatility +
                 0.10 * contango +
                 zweigWeight * zweigSupport) /
                positiveWeightTotal;
            double raw = positive - RiskPenaltyInfluence *
                (0.20 * titanicPenalty + 0.25 * shockPenalty);
            double value = 100.0 * Clamp01(raw);

            // Risk states act as explicit position-management caps.  This makes
            // zero/low values actionable instead of merely decorative.
            if (fuzzy.DominantZone == MarketZone.ReduceRisk)
                value = Math.Min(value, 15.0);
            else if (fuzzy.DominantZone == MarketZone.PrepareSell)
                value = Math.Min(value, 39.0);

            if (fuzzy.SpyClose < fuzzy.Ema200 && breadthAverage < 30.0)
                value = Math.Min(value, 10.0);

            // Asymmetric smoothing prevents one-day green flicker but lets risk
            // reduce the confidence quickly.  It is calculated once, never while
            // zooming or moving the cursor.
            if (previous.HasValue)
            {
                double currentWeight = value < previous.Value
                    ? DownSmoothing
                    : UpSmoothing;
                value = currentWeight * value + (1.0 - currentWeight) * previous.Value;
            }
            value = Math.Clamp(value, 0.0, 100.0);
            previous = value;

            points.Add(new HoldConfidencePoint(
                fuzzy.Date,
                value,
                trend,
                breadth,
                phase,
                volatility,
                contango,
                zweigSupport,
                titanicPenalty,
                shockPenalty,
                BuildRationale(value, trend, breadth, phase, volatility, contango,
                    zweigSupport, titanicPenalty, shockPenalty)));
        }

        return new HoldConfidenceSeries(points);
    }

    internal static string Band(double value) => value switch
    {
        < 20.0 => "No position",
        < 40.0 => "Weak hold",
        < 60.0 => "Cautious hold",
        < 80.0 => "Hold",
        _ => "Strong hold"
    };

    private static string BuildRationale(
        double value,
        double trend,
        double breadth,
        double phase,
        double volatility,
        double contango,
        double zweig,
        double titanicPenalty,
        double shockPenalty) =>
        $"{Band(value)} {value:F0}%; EMA {trend:P0}, breadth {breadth:P0}, " +
        $"phase {phase:P0}, calm ATRP {volatility:P0}, contango {contango:P0}, " +
        $"Zweig {zweig:P0}; Titanic penalty {titanicPenalty:P0}, shock penalty {shockPenalty:P0}.";

    private static double SmoothRamp(double value, double start, double end)
    {
        if (value <= start) return 0.0;
        if (value >= end) return 1.0;
        double t = (value - start) / (end - start);
        return t * t * (3.0 - 2.0 * t);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
