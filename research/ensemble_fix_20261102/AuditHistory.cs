using System.Globalization;
using System.IO;
using WealthLab.Core;

internal static class AuditHistory
{
    public const string Install = @"C:\Program Files\Quantacula, LLC\WealthLab 8";
    private static readonly Dictionary<string, BarHistory> Cache = new();
    public static BarHistory Get(BarHistory bars, string symbol)
    {
        if (symbol == "SPY") return bars;
        string key = symbol + bars.DateTimes[0].ToString("O") + bars.Count;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var raw = new BarHistory(symbol, HistoryScale.Daily);
        if (symbol.StartsWith("MODEL_") || symbol == "RISK_MEASURE")
        {
            string path = Path.Combine(@"D:\fear_greed_close", symbol.ToLowerInvariant() + ".csv");
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                var c = line.Split(',');
                if (!DateTime.TryParse(c[0], CultureInfo.InvariantCulture, out var d) ||
                    !double.TryParse(c[1], CultureInfo.InvariantCulture, out var v)) continue;
                raw.Add(d, v, v, v, v, 0);
            }
        }
        else
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WealthLab8", "WealthData", "Daily", symbol + ".QX");
            raw.ReadFromBinaryFile(path, DateTime.MinValue, DateTime.MaxValue, 0);
        }
        // Common exact calendar for both engines; carry the latest available
        // external observation forward, and record missing dates separately.
        var aligned = new BarHistory(symbol, HistoryScale.Daily);
        int j = 0, missing = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            while (j + 1 < raw.Count && raw.DateTimes[j + 1].Date <= bars.DateTimes[i].Date) j++;
            if (raw.Count == 0 || raw.DateTimes[j].Date > bars.DateTimes[i].Date)
                throw new InvalidDataException("No earlier observation: " + symbol + " " + bars.DateTimes[i]);
            if (raw.DateTimes[j].Date != bars.DateTimes[i].Date) missing++;
            aligned.Add(bars.DateTimes[i], raw.Open[j], raw.High[j], raw.Low[j], raw.Close[j], raw.Volume[j]);
        }
        Console.WriteLine($"HISTORY {symbol}: {raw.Count} raw, {aligned.Count} aligned, missing exact dates={missing}");
        Cache[key] = aligned;
        return aligned;
    }
    public static void Log(string message, bool unused = false) => Console.WriteLine("STRATEGY: " + message);
}

namespace BreezeVL
{
    internal enum BreadthProgressLevel { Normal, Error }
    internal record BreadthProgress(string Message, BreadthProgressLevel Level = BreadthProgressLevel.Normal);
    internal static class SentimentCalculator
    {
        public const string CnnFearGreedSymbol = "MODEL_CNN_FEAR_GREED";
        public const string RiskMeasureSymbol = "RISK_MEASURE";
        public const string PanicEuphoriaSymbol = "MODEL_PANIC_EUPHORIA_DAY";
    }
}
