namespace BreezeVL;

internal sealed record TechnicalIndicatorPoint(
    DateTime Date,
    double Ema20,
    double Ema50,
    double Ema100,
    double Ema200,
    double MyRsiRep,
    double MyRsiRep10,
    double Atrp1);

internal sealed record TechnicalIndicatorSeries(
    int MyRsiPeriod,
    int MyRsiSmoothPeriod,
    int AgcHalfLife,
    IReadOnlyList<TechnicalIndicatorPoint> Points);

internal static class TechnicalIndicatorCalculator
{
    public const int MyRsiPeriod = 14;
    public const int FastMyRsiPeriod = 10;
    public const int MyRsiSmoothPeriod = 8;
    public const int AgcHalfLife = 77;

    public static TechnicalIndicatorSeries Calculate(PriceSeries prices)
    {
        if (prices.Points.Count == 0)
            return new(MyRsiPeriod, MyRsiSmoothPeriod, AgcHalfLife, []);

        double[] closes = prices.Points.Select(point => point.Close).ToArray();
        double[] ema20 = Ema(closes, 20);
        double[] ema50 = Ema(closes, 50);
        double[] ema100 = Ema(closes, 100);
        double[] ema200 = Ema(closes, 200);
        double[] myRsi = MyRsiRepAgc(closes, MyRsiPeriod, MyRsiSmoothPeriod, AgcHalfLife);
        double[] myRsi10 = MyRsiRepAgc(closes, FastMyRsiPeriod, MyRsiSmoothPeriod, AgcHalfLife);
        double[] atrp1 = Atrp1(prices.Points);

        var points = new List<TechnicalIndicatorPoint>(prices.Points.Count);
        for (int index = 0; index < prices.Points.Count; index++)
        {
            points.Add(new TechnicalIndicatorPoint(
                prices.Points[index].Date,
                ema20[index], ema50[index], ema100[index], ema200[index],
                myRsi[index], myRsi10[index], atrp1[index]));
        }

        return new(MyRsiPeriod, MyRsiSmoothPeriod, AgcHalfLife, points);
    }

    private static double[] Atrp1(IReadOnlyList<PricePoint> prices)
    {
        var result = new double[prices.Count];
        for (int index = 0; index < prices.Count; index++)
        {
            PricePoint point = prices[index];
            double previousClose = index == 0 ? point.Close : prices[index - 1].Close;
            double trueRange = Math.Max(
                point.High - point.Low,
                Math.Max(Math.Abs(point.High - previousClose), Math.Abs(point.Low - previousClose)));
            result[index] = point.Close == 0.0 ? 0.0 : 100.0 * trueRange / point.Close;
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

    // Exact stand-alone equivalent of MyRSI_AGC_gpt(Close, 14, 8, 77):
    // TorchSharp4GPU's MyRSI_rep followed by the user's half-life AGC normalization.
    private static double[] MyRsiRepAgc(
        IReadOnlyList<double> source,
        int rsiPeriod,
        int smoothPeriod,
        int agcHalfLife)
    {
        double[] filtered = SuperSmootherRep(source, smoothPeriod);
        var result = new double[source.Count];
        int firstValid = Math.Max(smoothPeriod, rsiPeriod);
        double decay = Math.Pow(0.5, 1.0 / agcHalfLife);
        double peak = 0.0000001;

        for (int bar = firstValid; bar < source.Count; bar++)
        {
            double up = 0.0;
            double down = 0.0;
            for (int offset = 0; offset < rsiPeriod; offset++)
            {
                double difference = filtered[bar - offset] - filtered[bar - offset - 1];
                if (difference > 0.0)
                    up += difference;
                else if (difference < 0.0)
                    down -= difference;
            }

            double total = up + down;
            double rawValue = total == 0.0 ? 0.0 : (up - down) / total;
            peak *= decay;
            peak = Math.Max(peak, Math.Abs(rawValue));
            result[bar] = Math.Clamp(rawValue / peak, -1.0, 1.0);
        }

        return result;
    }

    private static double[] SuperSmootherRep(IReadOnlyList<double> source, int period)
    {
        var result = new double[source.Count];
        if (source.Count == 0)
            return result;

        result[0] = source[0];
        if (source.Count == 1)
            return result;
        result[1] = source[1];

        double a = Math.Exp(-Math.Sqrt(2.0) * Math.PI / period);
        double a1 = -2.0 * a * Math.Cos(Math.Sqrt(2.0) * Math.PI / period);
        double a2 = a * a;
        double b0 = (1.0 + a1 + a2) / 2.0;

        for (int index = 2; index < source.Count; index++)
        {
            result[index] = b0 * source[index] + b0 * source[index - 1]
                - a1 * result[index - 1] - a2 * result[index - 2];
        }

        return result;
    }
}
