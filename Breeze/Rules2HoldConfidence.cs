using System.Globalization;
using System.IO;
using BreezeRules2.Research;

namespace BreezeVL;

/// <summary>The unchanged research engine with a dated UI/position adapter.</summary>
internal static class Rules2HoldConfidenceCalculator
{
    internal const string DataDirectory = @"D:\fear_greed_close\Breeze";
    internal const string CurvePath = @"D:\fear_greed_close\vix_term_structure_rules2.csv";
    internal static readonly ScoreWeights Weights = new(-4.5, -2.5, -1.5, 1, 0, -.25, .5, 1.75, 0, -1);
    private static readonly FeatureSettings Settings = new(-.9, 3, 6, 3, 11, 35, 67.5, 57.5, 7.5, 2.5, .10);
    private static readonly object CacheLock = new();
    private static string? _signature;
    private static HoldConfidenceSeries[]? _profiles;
    private static string _warning = string.Empty;

    internal static (double Entry, double Exit) Thresholds(int profile) => profile switch
    {
        0 => (50, 50),
        2 => (55, 53.75),
        _ => (55, 50)
    };

    internal static double Normalize(double raw) => double.IsFinite(raw)
        ? 100 * (raw - Weights.MinimumScore) / (Weights.MaximumScore - Weights.MinimumScore)
        : double.NaN;

    public static bool TryCalculate(int profile, out HoldConfidenceSeries series, out string error)
    {
        try
        {
            string[] files = ["SPY.csv", "spy_ema_myrsi_agc.csv", "sp500_sma50.csv", "sp500_sma100.csv",
                "sp500_sma200.csv", "nasdaq100_sma100.csv"];
            string signature = string.Join("|", files.Select(f => Path.Combine(DataDirectory, f)).Append(CurvePath)
                .Select(path => { var f = new FileInfo(path); return $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"; }));
            lock (CacheLock)
            {
                if (_profiles is null || signature != _signature)
                {
                    var inputs = ResearchData.Load(DataDirectory, CurvePath).Inputs;
                    DateTime lastVix = inputs.LastOrDefault(r => double.IsFinite(r.FrontSpreadPct) && double.IsFinite(r.M1Days)).Date;
                    _warning = lastVix < inputs[^1].Date
                        ? $"Rules2: данные ближайших фьючерсов VIX заканчиваются {lastVix:yyyy-MM-dd}; цена — {inputs[^1].Date:yyyy-MM-dd}. На новых датах прежняя позиция сохраняется."
                        : string.Empty;
                    var features = CalculateFeatures(inputs);
                    _profiles = Enumerable.Range(0, 3).Select(p => Calculate(features, p)).ToArray();
                    _signature = signature;
                }
                series = _profiles[Math.Clamp(profile, 0, 2)];
            }
            error = _warning;
            return true;
        }
        catch (Exception ex)
        {
            series = new HoldConfidenceSeries([]);
            error = $"Rules2: {ex.Message}";
            return false;
        }
    }

    internal static IReadOnlyList<FeatureRow> CalculateFeatures(IReadOnlyList<InputRow> inputs) =>
        Rules2Engine.Calculate(inputs, Settings, PullbackResumeSettings.Default,
            new(20, 10, 1, 0, 0), new(4, -.2, 0, .5, 0), new(7, 1.5, 3, 5, .5),
            new(4, 1.5, 5, 7, .25), new(.02, 0, 0, 20, 1), new(10, 2, .25, .5, 1.5));

    internal static HoldConfidenceSeries Calculate(IReadOnlyList<FeatureRow> features, int profile)
    {
        var (entry, exit) = Thresholds(profile);
        string profileName = profile switch { 0 => "Soft", 2 => "Hard", _ => "Standard" };
        double span = Weights.MaximumScore - Weights.MinimumScore;
        double rawEntry = Weights.MinimumScore + entry * span / 100;
        double rawExit = Weights.MinimumScore + exit * span / 100;
        bool holding = false;
        var points = new List<HoldConfidencePoint>(features.Count);
        foreach (FeatureRow row in features)
        {
            double raw = Weights.Score(row), value = Normalize(raw);
            Rules2Action action = Rules2Trading.Decide(raw, holding, rawEntry, rawExit);
            if (action == Rules2Action.Buy) holding = true;
            else if (action == Rules2Action.Sell) holding = false;
            // Contributions are expressed in the same normalized points as the UI score.
            var contributions = new (string Name, double Points)[]
            {
                ("ранний разворот цены при нижнем MyRSI и росте широт", row.EarlyTurn),
                ("подтверждённый срыв раннего восстановления", -4.5 * row.Failure),
                ("ослабление быстрой широты при устойчивой долгой широте", -2.5 * row.Resilience),
                ("порядок четырёх EMA", -1.5 * row.EmaContext),
                ("возврат цены над EMA20 при удержанной EMA50", row.PullbackResume),
                ("ценовой стресс при сохраняющемся контанго VIX", -.25 * row.UnconfirmedStress),
                ("согласованная скорость четырёх широт", .5 * row.BreadthVelocity),
                ("согласованные расстояния и наклоны EMA", 1.75 * row.EmaFanTrend),
                ("относительный объём SPY с направлением движения цены", -row.VolumeConfirmation)
            }.Select(c => (c.Name, Points: c.Points * (50.0 / 14.0) * 100 / span))
                .Where(c => double.IsFinite(c.Item2) && Math.Abs(c.Item2) > 1e-9).ToArray();
            string Describe((string Name, double Points) c) =>
                $"{c.Name}: {c.Points.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)} балла к оценке модели";
            string support = contributions.Where(c => c.Points > 0).OrderByDescending(c => c.Points)
                .Select(Describe).FirstOrDefault() ?? "заметной поддержки нет";
            string risk = contributions.Where(c => c.Points < 0).OrderBy(c => c.Points)
                .Select(Describe).FirstOrDefault() ?? "заметного давления нет";
            string top = string.Join(Environment.NewLine, contributions.OrderByDescending(c => Math.Abs(c.Points))
                .Take(10).Select(c => "• " + Describe(c) + "."));
            string rationale = double.IsFinite(value)
                ? $"Rules2 {profileName}: {value:F1} из 100; вход {entry:0.##}, выход {exit:0.##}."
                : "Rules2: новой достоверной оценки нет; прежняя позиция сохраняется.";
            points.Add(new(row.Date, value, 0, 0, 0, 0, 0, 0, 0, 0,
                rationale, profileName, support, risk, top, top, "Rules2", holding ? 10 : 0,
                raw, entry, exit));
        }
        return new HoldConfidenceSeries(points);
    }
}
