namespace BreezeVL;

internal enum MarketZone
{
    Buy,
    PrepareBuy,
    Neutral,
    Caution,
    PrepareSell,
    ReduceRisk
}

internal sealed record FuzzyMarketStatePoint(
    DateTime Date,
    MarketZone DominantZone,
    double Buy,
    double PrepareBuy,
    double Neutral,
    double Caution,
    double PrepareSell,
    double ReduceRisk,
    double Confidence,
    double Score,
    double SpyClose,
    double Drawdown,
    double Sp50,
    double Sp100,
    double Sp200,
    double Nasdaq100,
    double Ema20,
    double Ema50,
    double Ema100,
    double Ema200,
    double MyRsiRep,
    double BullishEmaOrder,
    double BreadthTurnUp,
    double DeepFallContext,
    double MyRsiReleaseFromLow,
    double EarlyBuyComplex,
    double StrongReduceRiskComplex,
    double LargeRedDay,
    string Rationale)
{
    public double Degree(MarketZone zone) => zone switch
    {
        MarketZone.Buy => Buy,
        MarketZone.PrepareBuy => PrepareBuy,
        MarketZone.Neutral => Neutral,
        MarketZone.Caution => Caution,
        MarketZone.PrepareSell => PrepareSell,
        MarketZone.ReduceRisk => ReduceRisk,
        _ => 0.0
    };
}

internal sealed record FuzzyMarketStateSeries(IReadOnlyList<FuzzyMarketStatePoint> Points);

internal static class FuzzyMarketStateCalculator
{
    private const int PricePeakWindow = 252;
    private const int BreadthPeakWindow = 63;
    private const int ShortMomentumWindow = 5;
    private const int MediumMomentumWindow = 10;

    public static FuzzyMarketStateSeries Calculate(
        PriceSeries price,
        BreadthSeries sp50,
        BreadthSeries sp100,
        BreadthSeries sp200,
        BreadthSeries nasdaq100,
        TechnicalIndicatorSeries technicalIndicators)
    {
        var priceByDate = price.Points.ToDictionary(point => point.Date);
        var sp50ByDate = sp50.Points.ToDictionary(point => point.Date);
        var sp100ByDate = sp100.Points.ToDictionary(point => point.Date);
        var sp200ByDate = sp200.Points.ToDictionary(point => point.Date);
        var nasdaqByDate = nasdaq100.Points.ToDictionary(point => point.Date);
        var technicalByDate = technicalIndicators.Points.ToDictionary(point => point.Date);

        List<DateTime> dates = priceByDate.Keys
            .Where(date => sp50ByDate.ContainsKey(date) && sp100ByDate.ContainsKey(date) &&
                           sp200ByDate.ContainsKey(date) && nasdaqByDate.ContainsKey(date) &&
                           technicalByDate.ContainsKey(date))
            .OrderBy(date => date)
            .ToList();

        var points = new List<FuzzyMarketStatePoint>(dates.Count);
        for (int index = 0; index < dates.Count; index++)
        {
            DateTime date = dates[index];
            PricePoint spy = priceByDate[date];
            double b50 = sp50ByDate[date].Close;
            double b100 = sp100ByDate[date].Close;
            double b200 = sp200ByDate[date].Close;
            double nd100 = nasdaqByDate[date].Close;
            TechnicalIndicatorPoint technical = technicalByDate[date];

            int priceStart = Math.Max(0, index - PricePeakWindow + 1);
            double rollingPeak = dates
                .Skip(priceStart)
                .Take(index - priceStart + 1)
                .Max(item => priceByDate[item].Close);
            double drawdown = rollingPeak > 0 ? Math.Max(0.0, 1.0 - spy.Close / rollingPeak) : 0.0;
            int recentDeclineStart = Math.Max(0, index - 15);
            double recentLow = dates
                .Skip(recentDeclineStart)
                .Take(index - recentDeclineStart + 1)
                .Min(item => priceByDate[item].Close);
            double recentDrawdown = rollingPeak > 0 ? Math.Max(drawdown, 1.0 - recentLow / rollingPeak) : drawdown;

            double priceReturn5 = Change(priceByDate, dates, index, ShortMomentumWindow, point => point.Close);
            double priceReturn10 = Change(priceByDate, dates, index, MediumMomentumWindow, point => point.Close);
            double priceReturn1 = Change(priceByDate, dates, index, 1, point => point.Close);
            double b50Delta5 = Difference(sp50ByDate, dates, index, ShortMomentumWindow, point => point.Close);
            double b100Delta5 = Difference(sp100ByDate, dates, index, ShortMomentumWindow, point => point.Close);
            double b200Delta5 = Difference(sp200ByDate, dates, index, ShortMomentumWindow, point => point.Close);
            double ndDelta5 = Difference(nasdaqByDate, dates, index, ShortMomentumWindow, point => point.Close);
            double breadthDelta5 = 0.35 * b50Delta5 + 0.30 * b100Delta5 + 0.25 * b200Delta5 + 0.10 * ndDelta5;

            double extreme50 = FallingShoulder(b50, 4.0, 24.0);
            double extreme100 = FallingShoulder(b100, 8.0, 30.0);
            double extreme200 = FallingShoulder(b200, 12.0, 34.0);
            double extremeNasdaq = FallingShoulder(nd100, 8.0, 32.0);
            double broadLow = 0.35 * extreme50 + 0.30 * extreme100 +
                              0.25 * extreme200 + 0.10 * extremeNasdaq;

            double fastWeak = FallingShoulder(b50, 20.0, 55.0);
            double mediumWeak = FallingShoulder(b100, 18.0, 55.0);
            double slowWeak = FallingShoulder(b200, 15.0, 50.0);
            double slowStrong = RisingShoulder(b200, 50.0, 78.0);
            double deepDrawdown = RisingShoulder(drawdown, 0.07, 0.28);
            double breadthFalling = RisingShoulder(-breadthDelta5, 0.5, 11.0);
            double breadthRecovering = RisingShoulder(breadthDelta5, 0.5, 11.0);
            double priceRecovering = RisingShoulder(priceReturn5, 0.0, 0.065);
            double priceRollingOver = RisingShoulder(-priceReturn5, 0.0, 0.055);
            double priceDeclining = RisingShoulder(-priceReturn10, 0.01, 0.14);
            double stabilization = 1.0 - Math.Max(breadthFalling, priceDeclining);

            // Technical context added after the author's second walkthrough.  All
            // boundaries are deliberately soft: the chart is a state map, not an
            // order generator.
            double myRsiLow = FallingShoulder(technical.MyRsiRep, -0.98, -0.68);
            double myRsiHigh = RisingShoulder(technical.MyRsiRep, 0.68, 0.98);
            double previousMyRsi = index == 0
                ? technical.MyRsiRep
                : technicalByDate[dates[index - 1]].MyRsiRep;
            double previousFastMyRsi = index == 0
                ? technical.MyRsiRep10
                : technicalByDate[dates[index - 1]].MyRsiRep10;
            double priorLow = FallingShoulder(previousMyRsi, -0.98, -0.72);
            double priorHigh = RisingShoulder(previousMyRsi, 0.72, 0.98);
            double releaseFromLow = And(
                priorLow,
                RisingShoulder(technical.MyRsiRep - previousMyRsi, 0.005, 0.12));
            double releaseFromHigh = And(
                priorHigh,
                RisingShoulder(previousMyRsi - technical.MyRsiRep, 0.005, 0.12));
            double previousReleaseFromLow = 0.0;
            double previousReleaseFromHigh = 0.0;
            if (index >= 2)
            {
                double twoBarsAgoMyRsi = technicalByDate[dates[index - 2]].MyRsiRep;
                previousReleaseFromLow = And(
                    FallingShoulder(twoBarsAgoMyRsi, -0.98, -0.72),
                    RisingShoulder(previousMyRsi - twoBarsAgoMyRsi, 0.005, 0.12));
                previousReleaseFromHigh = And(
                    RisingShoulder(twoBarsAgoMyRsi, 0.72, 0.98),
                    RisingShoulder(twoBarsAgoMyRsi - previousMyRsi, 0.005, 0.12));
            }
            double recentReleaseFromLow = Math.Max(releaseFromLow, 0.82 * previousReleaseFromLow);
            double recentReleaseFromHigh = Math.Max(releaseFromHigh, 0.82 * previousReleaseFromHigh);
            double fastMyRsiLow = FallingShoulder(technical.MyRsiRep10, -0.98, -0.68);
            double previousFastHigh = RisingShoulder(previousFastMyRsi, 0.72, 0.98);
            double fastReleaseFromHigh = And(
                previousFastHigh,
                RisingShoulder(previousFastMyRsi - technical.MyRsiRep10, 0.005, 0.12));
            double bothReleaseFromHigh = And(releaseFromHigh, fastReleaseFromHigh);

            double bullishEmaOrder = EmaOrderMembership(
                spy.Close, technical.Ema20, technical.Ema50, technical.Ema100, technical.Ema200);
            double belowEma20 = FallingShoulder(
                RelativeDistance(spy.Close, technical.Ema20), -0.025, 0.005);
            double belowEma50 = FallingShoulder(
                RelativeDistance(spy.Close, technical.Ema50), -0.035, 0.005);
            double belowEma100 = FallingShoulder(
                RelativeDistance(spy.Close, technical.Ema100), -0.045, 0.005);
            double belowEma200 = FallingShoulder(
                RelativeDistance(spy.Close, technical.Ema200), -0.055, 0.005);
            double emaDamage = Clamp(
                0.15 * belowEma20 + 0.25 * belowEma50 +
                0.27 * belowEma100 + 0.33 * belowEma200);
            double recentDeepFall = RisingShoulder(recentDrawdown, 0.035, 0.12);
            double deepFallContext = Clamp(
                0.55 * recentDeepFall + 0.25 * priceDeclining + 0.20 * emaDamage);

            double breadthTurnToday = BreadthTurnMembership(
                sp50ByDate[date], sp100ByDate[date], sp200ByDate[date], nasdaqByDate[date]);
            double breadthTurnPrevious = index == 0 ? 0.0 : BreadthTurnMembership(
                sp50ByDate[dates[index - 1]], sp100ByDate[dates[index - 1]],
                sp200ByDate[dates[index - 1]], nasdaqByDate[dates[index - 1]]);
            double breadthTurnUp = Math.Max(breadthTurnToday, 0.82 * breadthTurnPrevious);

            double previousClose = index == 0 ? spy.Close : priceByDate[dates[index - 1]].Close;
            double trueRange = Math.Max(
                spy.High - spy.Low,
                Math.Max(Math.Abs(spy.High - previousClose), Math.Abs(spy.Low - previousClose)));
            int volatilityStart = Math.Max(0, index - 20);
            var priorTrueRanges = new List<double>();
            for (int prior = volatilityStart; prior < index; prior++)
            {
                PricePoint priorPrice = priceByDate[dates[prior]];
                double priorClose = prior == 0 ? priorPrice.Close : priceByDate[dates[prior - 1]].Close;
                priorTrueRanges.Add(Math.Max(
                    priorPrice.High - priorPrice.Low,
                    Math.Max(Math.Abs(priorPrice.High - priorClose), Math.Abs(priorPrice.Low - priorClose))));
            }
            double normalTrueRange = priorTrueRanges.Count == 0 ? trueRange : Median(priorTrueRanges);
            double rangeRatio = normalTrueRange <= 0 ? 0.0 : trueRange / normalTrueRange;
            double redDay = FallingShoulder(spy.Close - spy.Open, -0.001 * spy.Close, 0.0);
            double largeRedDay = And(redDay, RisingShoulder(rangeRatio, 1.50, 2.20));

            double bottomPressure = And(deepDrawdown, broadLow);
            double bottomConfirmation = Clamp(
                0.45 * breadthRecovering +
                0.35 * priceRecovering +
                0.20 * stabilization);
            double dailyPriceTurn = RisingShoulder(priceReturn1, -0.002, 0.018);
            double reversalConfirmation = Clamp(
                0.55 * breadthTurnToday + 0.25 * dailyPriceTurn + 0.20 * breadthRecovering);

            // The author's sequence is explicit: a deep decline is the context;
            // the principal Buy event is the release upward from -1.  A synchronized
            // breadth/price reversal may anticipate it while MyRSI is still pinned,
            // but saturation by itself can never create Buy.
            double releaseEvent = And(deepFallContext, recentReleaseFromLow);
            double breadthLeadEvent = And(
                deepFallContext,
                And(And(myRsiLow, fastMyRsiLow), And(breadthTurnToday, dailyPriceTurn)));
            double releaseBuy = Math.Max(
                SmoothRamp(releaseEvent, 0.08, 0.45),
                And(deepFallContext,
                    Clamp(0.72 * recentReleaseFromLow + 0.28 * Math.Max(reversalConfirmation, breadthTurnUp))));
            double breadthLeadBuy = SmoothRamp(breadthLeadEvent, 0.24, 0.68);
            double buy = Math.Max(releaseBuy, breadthLeadBuy);
            double prepareBuy = And(
                deepFallContext,
                And(myRsiLow, reversalConfirmation));
            prepareBuy *= 1.0 - 0.88 * buy;

            int breadthStart = Math.Max(0, index - BreadthPeakWindow + 1);
            double gap50 = PeakGap(sp50ByDate, dates, breadthStart, index, b50);
            double gap100 = PeakGap(sp100ByDate, dates, breadthStart, index, b100);
            double gap200 = PeakGap(sp200ByDate, dates, breadthStart, index, b200);
            double gapNasdaq = PeakGap(nasdaqByDate, dates, breadthStart, index, nd100);
            double averagePeakGap = 0.30 * gap50 + 0.30 * gap100 + 0.25 * gap200 + 0.15 * gapNasdaq;
            double breadthBelowPriorHigh = RisingShoulder(averagePeakGap, 5.0, 28.0);
            double nearPriceHigh = FallingShoulder(drawdown, 0.015, 0.105);
            double thinning = And(nearPriceHigh, breadthBelowPriorHigh);
            double multiHorizonWeakening = Clamp(
                0.30 * fastWeak + 0.30 * mediumWeak + 0.25 * slowWeak + 0.15 * breadthFalling);
            double technicalRollOver = And(
                recentReleaseFromHigh,
                Clamp(0.45 * belowEma20 + 0.35 * breadthFalling + 0.20 * (1.0 - bullishEmaOrder)));
            double strongReduceRiskComplex = And(
                bothReleaseFromHigh,
                And(belowEma20, largeRedDay));
            double prepareSell = Math.Max(
                And(thinning, Math.Max(0.35, 0.65 * breadthFalling + 0.35 * multiHorizonWeakening)),
                technicalRollOver);
            double reduceRisk = Math.Max(
                strongReduceRiskComplex,
                Math.Max(
                    And(thinning, Math.Max(priceRollingOver, And(breadthFalling, multiHorizonWeakening))),
                    And(emaDamage, Clamp(0.50 * breadthFalling + 0.30 * priceDeclining + 0.20 * recentReleaseFromHigh))));

            double horizonDisagreement = RisingShoulder(
                Math.Max(Math.Abs(b50 - b200), Math.Abs(b100 - nd100)),
                12.0,
                42.0);
            double fastWeakSlowStrong = And(fastWeak, slowStrong);
            double unresolvedWeakness = And(
                Clamp(0.45 * fastWeak + 0.35 * mediumWeak + 0.20 * breadthFalling),
                1.0 - Math.Max(bottomPressure, thinning));
            double caution = Math.Max(
                0.80 * horizonDisagreement,
                Math.Max(0.90 * fastWeakSlowStrong,
                    Math.Max(0.75 * unresolvedWeakness,
                        And(myRsiHigh, Math.Max(0.42 * (1.0 - bullishEmaOrder), 0.58 * belowEma20)))));

            // A stretched, correctly ordered bull trend suppresses automatic sell
            // interpretation of +1.  It still leaves a visible caution component.
            double bullHoldProtection = And(myRsiHigh, bullishEmaOrder);
            prepareSell *= 1.0 - 0.72 * bullHoldProtection;
            reduceRisk *= 1.0 - 0.58 * bullHoldProtection;

            // Keep the display neutral unless a state is meaningfully activated. This
            // prevents small everyday oscillations from painting the whole history.
            double combinedBuy = Gate(buy, 0.20);
            double strongBuy = SmoothRamp(combinedBuy, 0.62, 0.92);
            double ordinaryBuy = Math.Max(
                Gate(prepareBuy, 0.20),
                combinedBuy * (1.0 - 0.85 * strongBuy));
            buy = strongBuy;
            prepareBuy = Clamp(ordinaryBuy);
            prepareSell = Gate(prepareSell, 0.22);
            reduceRisk = Gate(reduceRisk, 0.22);
            caution = Gate(caution, 0.24);
            double strongestNonNeutral = new[] { buy, prepareBuy, caution, prepareSell, reduceRisk }.Max();
            double neutral = Clamp(1.0 - SmoothRamp(strongestNonNeutral, 0.18, 0.72));

            var degrees = new Dictionary<MarketZone, double>
            {
                [MarketZone.Buy] = buy,
                [MarketZone.PrepareBuy] = prepareBuy,
                [MarketZone.Neutral] = neutral,
                [MarketZone.Caution] = caution,
                [MarketZone.PrepareSell] = prepareSell,
                [MarketZone.ReduceRisk] = reduceRisk
            };
            MarketZone dominant = degrees.MaxBy(item => item.Value).Key;
            double[] ranked = degrees.Values.OrderByDescending(value => value).ToArray();
            double confidence = Clamp(ranked[0] * (0.55 + 0.45 * (ranked[0] - ranked[1])));
            double score = ClampSigned(
                buy + 0.50 * prepareBuy - 0.50 * prepareSell - reduceRisk);
            string rationale = BuildRationale(
                dominant, drawdown, b50, b100, b200, nd100,
                breadthDelta5, averagePeakGap, bottomConfirmation,
                technical.MyRsiRep, bullishEmaOrder, breadthTurnUp, emaDamage,
                deepFallContext, recentReleaseFromLow);

            points.Add(new FuzzyMarketStatePoint(
                date,
                dominant,
                buy,
                prepareBuy,
                neutral,
                caution,
                prepareSell,
                reduceRisk,
                confidence,
                score,
                spy.Close,
                drawdown,
                b50,
                b100,
                b200,
                nd100,
                technical.Ema20,
                technical.Ema50,
                technical.Ema100,
                technical.Ema200,
                technical.MyRsiRep,
                bullishEmaOrder,
                breadthTurnUp,
                deepFallContext,
                recentReleaseFromLow,
                breadthLeadEvent,
                strongReduceRiskComplex,
                largeRedDay,
                rationale));
        }

        return new FuzzyMarketStateSeries(points);
    }

    private static string BuildRationale(
        MarketZone zone,
        double drawdown,
        double b50,
        double b100,
        double b200,
        double nasdaq,
        double breadthDelta,
        double peakGap,
        double bottomConfirmation,
        double myRsi,
        double bullishEmaOrder,
        double breadthTurnUp,
        double emaDamage,
        double deepFallContext,
        double releaseFromLow) => zone switch
    {
        MarketZone.Buy =>
            $"Deep-fall context {deepFallContext:P0}; MyRSI release from -1 {releaseFromLow:P0}; breadth turn {breadthTurnUp:P0}.",
        MarketZone.PrepareBuy =>
            $"Deep-fall context {deepFallContext:P0}; MyRSI remains {myRsi:+0.00;-0.00;0.00}; breadth reversal is incomplete.",
        MarketZone.Caution =>
            $"MyRSI {myRsi:+0.00;-0.00;0.00}; EMA bull order {bullishEmaOrder:P0}; mixed breadth {b50:F0}/{b100:F0}/{b200:F0}/{nasdaq:F0}.",
        MarketZone.PrepareSell =>
            $"MyRSI roll-over with EMA/breadth weakening; breadth trails 63-day highs by about {peakGap:F0} points.",
        MarketZone.ReduceRisk =>
            $"EMA damage {emaDamage:P0} joins price/breadth deterioration; breadth peak gap about {peakGap:F0} points.",
        _ =>
            $"No fuzzy condition dominates; MyRSI {myRsi:+0.00;-0.00;0.00}, EMA bull order {bullishEmaOrder:P0}, breadth {b50:F0}/{b100:F0}/{b200:F0}/{nasdaq:F0}."
    };

    private static double EmaOrderMembership(
        double price, double ema20, double ema50, double ema100, double ema200)
    {
        double scale = Math.Max(Math.Abs(price), 0.000001);
        double price20 = SmoothRamp((price - ema20) / scale, -0.008, 0.012);
        double order20_50 = SmoothRamp((ema20 - ema50) / scale, -0.004, 0.008);
        double order50_100 = SmoothRamp((ema50 - ema100) / scale, -0.004, 0.010);
        double order100_200 = SmoothRamp((ema100 - ema200) / scale, -0.006, 0.015);
        return Clamp(0.22 * price20 + 0.28 * order20_50 + 0.25 * order50_100 + 0.25 * order100_200);
    }

    private static double BreadthTurnMembership(
        BreadthPoint sp50, BreadthPoint sp100, BreadthPoint sp200, BreadthPoint nasdaq)
    {
        static double Up(BreadthPoint point) => SmoothRamp(point.Close - point.Open, -0.25, 3.5);
        return Clamp(0.30 * Up(sp50) + 0.28 * Up(sp100) + 0.25 * Up(sp200) + 0.17 * Up(nasdaq));
    }

    private static double RelativeDistance(double value, double reference) =>
        (value - reference) / Math.Max(Math.Abs(reference), 0.000001);

    private static double Change<T>(
        IReadOnlyDictionary<DateTime, T> values,
        IReadOnlyList<DateTime> dates,
        int index,
        int lookback,
        Func<T, double> selector)
    {
        int previous = Math.Max(0, index - lookback);
        double start = selector(values[dates[previous]]);
        double end = selector(values[dates[index]]);
        return Math.Abs(start) < 0.000001 ? 0.0 : end / start - 1.0;
    }

    private static double Difference<T>(
        IReadOnlyDictionary<DateTime, T> values,
        IReadOnlyList<DateTime> dates,
        int index,
        int lookback,
        Func<T, double> selector)
    {
        int previous = Math.Max(0, index - lookback);
        return selector(values[dates[index]]) - selector(values[dates[previous]]);
    }

    private static double PeakGap(
        IReadOnlyDictionary<DateTime, BreadthPoint> values,
        IReadOnlyList<DateTime> dates,
        int start,
        int end,
        double current)
    {
        double peak = dates.Skip(start).Take(end - start + 1).Max(date => values[date].Close);
        return Math.Max(0.0, peak - current);
    }

    private static double FallingShoulder(double value, double fullBelow, double zeroAbove) =>
        1.0 - SmoothRamp(value, fullBelow, zeroAbove);

    private static double RisingShoulder(double value, double zeroBelow, double fullAbove) =>
        SmoothRamp(value, zeroBelow, fullAbove);

    private static double SmoothRamp(double value, double start, double end)
    {
        if (end <= start)
            return value >= end ? 1.0 : 0.0;
        double t = Clamp((value - start) / (end - start));
        return t * t * (3.0 - 2.0 * t);
    }

    private static double Gate(double value, double activationStart) =>
        value <= activationStart ? 0.0 : SmoothRamp(value, activationStart, 1.0);

    private static double And(double left, double right) => Math.Min(left, right);

    private static double Clamp(double value) => Math.Clamp(value, 0.0, 1.0);

    private static double ClampSigned(double value) => Math.Clamp(value, -1.0, 1.0);

    private static double Median(List<double> values)
    {
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }
}
