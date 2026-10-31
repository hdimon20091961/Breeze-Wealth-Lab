using WealthLab.AdvancedSmoothers;
using WealthLab.Core;
using WealthLab.Indicators;
using WealthLab.TASC;
using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;

namespace BreezeVL;

internal enum SentimentIndicator
{
    CnnFearGreed,
    RiskMeasure,
    PanicEuphoria,
    Arsi,
    MyRsi,
    MyRsiFast,
    TrendScore,
    AroonUp,
    AroonDown,
    DiMinus,
    DiPlus,
    CorrelationAngle,
    CorrelationTrend,
    Hurst,
    Cmf,
    Decibel,
    Jocker,
    MarketBreadth,
    DumbMoney,
    SmartMoney
}

internal sealed record SentimentPoint(
    DateTime Date,
    double CnnFearGreed,
    double RiskMeasure,
    double PanicEuphoria,
    double Arsi,
    double MyRsi,
    double MyRsiFast,
    double TrendScore,
    double AroonUp,
    double AroonDown,
    double DiMinus,
    double DiPlus,
    double CorrelationAngle,
    double CorrelationTrend,
    double Hurst,
    double Cmf,
    double Decibel,
    double Jocker,
    double MarketBreadth,
    double DumbMoney,
    double SmartMoney)
{
    public double Value(SentimentIndicator indicator) => indicator switch
    {
        SentimentIndicator.CnnFearGreed => CnnFearGreed,
        SentimentIndicator.RiskMeasure => RiskMeasure,
        SentimentIndicator.PanicEuphoria => PanicEuphoria,
        SentimentIndicator.Arsi => Arsi,
        SentimentIndicator.MyRsi => MyRsi,
        SentimentIndicator.MyRsiFast => MyRsiFast,
        SentimentIndicator.TrendScore => TrendScore,
        SentimentIndicator.AroonUp => AroonUp,
        SentimentIndicator.AroonDown => AroonDown,
        SentimentIndicator.DiMinus => DiMinus,
        SentimentIndicator.DiPlus => DiPlus,
        SentimentIndicator.CorrelationAngle => CorrelationAngle,
        SentimentIndicator.CorrelationTrend => CorrelationTrend,
        SentimentIndicator.Hurst => Hurst,
        SentimentIndicator.Cmf => Cmf,
        SentimentIndicator.Decibel => Decibel,
        SentimentIndicator.Jocker => Jocker,
        SentimentIndicator.MarketBreadth => MarketBreadth,
        SentimentIndicator.DumbMoney => DumbMoney,
        SentimentIndicator.SmartMoney => SmartMoney,
        _ => double.NaN
    };
}

internal sealed record SentimentSeries(IReadOnlyList<SentimentPoint> Points);

internal static class SentimentIndicatorCatalog
{
    public static readonly SentimentIndicator[] All = Enum.GetValues<SentimentIndicator>();

    public static string DisplayName(SentimentIndicator indicator) => indicator switch
    {
        SentimentIndicator.CnnFearGreed => "CNN Fear Greed",
        SentimentIndicator.RiskMeasure => "Risk Measure",
        SentimentIndicator.PanicEuphoria => "Panic Euphoria",
        SentimentIndicator.Arsi => "ARSI 14",
        SentimentIndicator.MyRsi => "MyRSI_rep 14,8 + AGC(77)",
        SentimentIndicator.MyRsiFast => "MyRSI_rep 10,8 + AGC(77)",
        SentimentIndicator.TrendScore => "Trend Score",
        SentimentIndicator.AroonUp => "Aroon Up 40",
        SentimentIndicator.AroonDown => "Aroon Down 48",
        SentimentIndicator.DiMinus => "DI− 7 + AGC",
        SentimentIndicator.DiPlus => "DI+ 14 + AGC",
        SentimentIndicator.CorrelationAngle => "Correlation Angle 28",
        SentimentIndicator.CorrelationTrend => "Correlation Trend 35 + AGC",
        SentimentIndicator.Hurst => "Hurst",
        SentimentIndicator.Cmf => "CMF",
        SentimentIndicator.Decibel => "Decibel",
        SentimentIndicator.Jocker => "Jocker",
        SentimentIndicator.MarketBreadth => "Market Breadth",
        SentimentIndicator.DumbMoney => "Dumb Money",
        SentimentIndicator.SmartMoney => "Smart Money",
        _ => indicator.ToString()
    };

    public static string FileStem(SentimentIndicator indicator) => indicator switch
    {
        SentimentIndicator.CnnFearGreed => "cnn_fear_greed",
        SentimentIndicator.RiskMeasure => "risk_measure",
        SentimentIndicator.PanicEuphoria => "panic_euphoria",
        SentimentIndicator.Arsi => "arsi",
        SentimentIndicator.MyRsi => "myrsi_14_8",
        SentimentIndicator.MyRsiFast => "myrsi_10_8",
        SentimentIndicator.TrendScore => "trend_score",
        SentimentIndicator.AroonUp => "aroon_up_40",
        SentimentIndicator.AroonDown => "aroon_down_48",
        SentimentIndicator.DiMinus => "di_minus_7_agc",
        SentimentIndicator.DiPlus => "di_plus_14_agc",
        SentimentIndicator.CorrelationAngle => "correlation_angle_28",
        SentimentIndicator.CorrelationTrend => "correlation_trend_35_agc",
        SentimentIndicator.Hurst => "hurst",
        SentimentIndicator.Cmf => "cmf",
        SentimentIndicator.Decibel => "decibel",
        SentimentIndicator.Jocker => "jocker",
        SentimentIndicator.MarketBreadth => "market_breadth",
        SentimentIndicator.DumbMoney => "dumb_money",
        SentimentIndicator.SmartMoney => "smart_money",
        _ => indicator.ToString().ToLowerInvariant()
    };
}

internal static class SentimentCalculator
{
    // Ensemble feature transforms with the agreed ZZ1/ZZ2 defaults.
    // Breeze keeps its own two MyRSI_rep(14,8)/(10,8) + AGC(77) series.
    public const int ArsiPeriod = 14;
    public const int HurstPeriod = 120;
    public const int CmfPeriod = 80;
    public const int DecibelPeriod = 96;
    public const int JockerEmaPeriod = 3;
    public const int JockerHighLowPeriod = 65;
    public const int MarketBreadthEmaPeriod = 1;
    public const int MarketBreadthVwmaPeriod = 64;

    public const string CnnFearGreedSymbol = "MODEL_CNN_FEAR_GREED";
    public const string RiskMeasureSymbol = "RISK_MEASURE";
    public const string PanicEuphoriaSymbol = "MODEL_PANIC_EUPHORIA_DAY";
    public const string DumbMoneySymbol = "MODEL_DUMB_MONEY";
    public const string SmartMoneySymbol = "MODEL_SMART_MONEY";

    public static SentimentSeries Calculate(
        BarHistory spy,
        BarHistory cnnFearGreed,
        BarHistory riskMeasure,
        BarHistory panicEuphoria,
        BarHistory dumbMoney,
        BarHistory smartMoney,
        BarHistory nasdaqNewHighs,
        BarHistory nasdaqNewLows,
        IReadOnlyList<BarHistory> marketBreadthHistories,
        DateTime startDate,
        DateTime endDate)
    {
        if (spy.Count == 0)
            return new SentimentSeries([]);

        var arsi = new ARSI(spy.Close, ArsiPeriod);
        double[] myRsi = M.MyRsi(Enumerable.Range(0, spy.Count).Select(index => spy.Close[index]).ToArray(), 14);
        double[] myRsiFast = M.MyRsi(Enumerable.Range(0, spy.Count).Select(index => spy.Close[index]).ToArray(), 10);
        var trendScore = new TrendScore(spy);
        var aroonUp = new AroonUp(spy.Close, 40);
        var aroonDown = new AroonDown(spy.Close, 48);
        double[] diMinus = Agc((-30.0 + new DIMinus(spy, 7)) / 30.0);
        double[] diPlus = Agc((-25.0 + new DIPlus(spy, 14)) / 30.0);
        var correlationAngle = new CorrelationAngle_rep(spy.Close, 28);
        var correlationTrend = new CorrelationTrend(spy.Close, 35);
        double[] normalizedCorrelationTrend = Agc(correlationTrend, correlationTrend.FirstValidIndex);
        var hurst = new Hurst(spy.Close, HurstPeriod);
        var hurstFisher = new EhlersInverseFisherTransform_gpt(hurst, 3, 1);
        var cmf = new CMF(spy, CmfPeriod);
        var cmfFisher = new EhlersInverseFisherTransform_gpt(cmf, 3, 10);
        var decibel = new Decibel(spy.Close, DecibelPeriod);
        var decibelFisher = new EhlersInverseFisherTransform_gpt(decibel, 8, 0.1);

        DateTime[] masterDates = Enumerable.Range(0, spy.Count)
            .Select(index => spy.DateTimes[index].Date)
            .ToArray();
        double[] cnnValues = CarryForward(cnnFearGreed.Close, masterDates);
        double[] riskValues = CarryForward(riskMeasure.Close, masterDates);
        double[] panicValues = CarryForward(panicEuphoria.Close, masterDates);
        double[] dumbValues = CarryForward(dumbMoney.Close, masterDates);
        double[] smartValues = CarryForward(smartMoney.Close, masterDates);
        double[] jockerValues = CalculateJocker(nasdaqNewHighs.Close, nasdaqNewLows.Close, masterDates);
        double[] marketBreadthValues = CalculateMarketBreadth(marketBreadthHistories, masterDates);

        var points = new List<SentimentPoint>(spy.Count);
        for (int index = 0; index < spy.Count; index++)
        {
            DateTime date = masterDates[index];
            if (date < startDate.Date || date > endDate.Date)
                continue;

            points.Add(new SentimentPoint(
                date,
                NormalizeFifty(cnnValues[index]),
                NormalizeFifty(riskValues[index]),
                double.IsFinite(panicValues[index]) ? panicValues[index] - 0.5 : double.NaN,
                NormalizeFifty(arsi[index]),
                myRsi[index],
                myRsiFast[index],
                trendScore[index] * 0.1,
                NormalizeFifty(aroonUp[index]),
                NormalizeFifty(aroonDown[index]),
                diMinus[index],
                diPlus[index],
                correlationAngle[index] / 180.0,
                normalizedCorrelationTrend[index],
                7.0 * (hurstFisher[index] - 0.6),
                cmfFisher[index] - 0.1,
                decibelFisher[index] + 0.55,
                jockerValues[index],
                marketBreadthValues[index],
                NormalizeUnitInterval(dumbValues[index]),
                NormalizeUnitInterval(smartValues[index])));
        }

        return new SentimentSeries(points);
    }

    private static double NormalizeFifty(double value) =>
        double.IsFinite(value) ? (value - 50.0) / 50.0 : double.NaN;

    // Match TradingEnsembleInference: an exponentially decaying absolute peak.
    private static double[] Agc(TimeSeries source, int startIndex = 0)
    {
        var result = Enumerable.Repeat(double.NaN, source.Count).ToArray();
        double peak = 0.0000001;
        for (int index = Math.Max(0, startIndex); index < source.Count; index++)
        {
            if (double.IsNaN(source[index]))
            {
                result[index] = 0.0;
                continue;
            }
            peak = Math.Max(peak * 0.991, Math.Abs(source[index]));
            result[index] = source[index] / peak;
        }
        return result;
    }

    private static double NormalizeUnitInterval(double value) =>
        double.IsFinite(value) ? Math.Clamp((value - 0.5) * 2.0, -1.0, 1.0) : double.NaN;

    private static double[] CalculateJocker(TimeSeries highs, TimeSeries lows, IReadOnlyList<DateTime> masterDates)
    {
        double[] alignedHighs = CarryForward(highs, masterDates);
        double[] alignedLows = CarryForward(lows, masterDates);
        var raw = new double[masterDates.Count];
        double sumHighs = 0.0;
        double sumLows = 0.0;
        var highWindow = new Queue<double>();
        var lowWindow = new Queue<double>();

        for (int index = 0; index < masterDates.Count; index++)
        {
            double high = double.IsFinite(alignedHighs[index]) ? alignedHighs[index] : 0.0;
            double low = double.IsFinite(alignedLows[index]) ? alignedLows[index] : 0.0;
            highWindow.Enqueue(high);
            lowWindow.Enqueue(low);
            sumHighs += high;
            sumLows += low;
            if (highWindow.Count > JockerHighLowPeriod)
            {
                sumHighs -= highWindow.Dequeue();
                sumLows -= lowWindow.Dequeue();
            }

            double sum = sumHighs + sumLows;
            raw[index] = sum > 0.0
                ? Math.Clamp((sumHighs - sumLows) / sum, -1.0, 1.0)
                : 0.0;
        }

        return Ema(raw, JockerEmaPeriod);
    }

    private static double[] CalculateMarketBreadth(
        IReadOnlyList<BarHistory> histories,
        IReadOnlyList<DateTime> masterDates)
    {
        var series = histories
            .Where(history => history.Count > 0)
            .Select(history =>
            {
                var vwma = new VWMA(history.Close, MarketBreadthVwmaPeriod, history);
                return (Close: CarryForward(history.Close, masterDates), Vwma: CarryForward(vwma, masterDates));
            })
            .ToList();
        var raw = new double[masterDates.Count];
        for (int index = 0; index < masterDates.Count; index++)
        {
            int valid = 0;
            int up = 0;
            foreach ((double[] close, double[] vwma) in series)
            {
                if (!double.IsFinite(close[index]) || !double.IsFinite(vwma[index]))
                    continue;
                valid++;
                if (close[index] > vwma[index])
                    up++;
            }
            raw[index] = valid == 0 ? 50.0 : 100.0 * up / valid;
        }

        double[] smoothed = Ema(raw, MarketBreadthEmaPeriod);
        for (int index = 0; index < smoothed.Length; index++)
            smoothed[index] = 0.01 * (smoothed[index] - 50.0);
        return smoothed;
    }

    private static double[] CarryForward(TimeSeries source, IReadOnlyList<DateTime> masterDates)
    {
        var result = new double[masterDates.Count];
        Array.Fill(result, double.NaN);
        int sourceIndex = 0;
        double lastValue = double.NaN;
        for (int index = 0; index < masterDates.Count; index++)
        {
            DateTime masterDate = masterDates[index].Date;
            while (sourceIndex < source.Count && source.DateTimes[sourceIndex].Date <= masterDate)
            {
                double candidate = source[sourceIndex];
                if (double.IsFinite(candidate))
                    lastValue = candidate;
                sourceIndex++;
            }
            result[index] = lastValue;
        }
        return result;
    }

    private static double[] Ema(IReadOnlyList<double> source, int period)
    {
        var result = new double[source.Count];
        if (source.Count == 0)
            return result;
        double alpha = 2.0 / (period + 1.0);
        result[0] = source[0];
        for (int index = 1; index < source.Count; index++)
            result[index] = alpha * source[index] + (1.0 - alpha) * result[index - 1];
        return result;
    }
}
