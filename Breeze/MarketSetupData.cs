using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;
using BreezeRules2.Research;

namespace BreezeVL;

internal sealed record MarketSetupPoint(
    DateTime Date,
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyDictionary<string, double> Details)
{
    public double Value(string ruleId) =>
        Values.TryGetValue(ruleId, out double value) ? value : double.NaN;
    public double Detail(string key) => Details.TryGetValue(key, out double value) ? value : double.NaN;
}

internal sealed record MarketSetupSeries(IReadOnlyList<MarketSetupPoint> Points)
{
    public static readonly MarketSetupSeries Empty = new([]);
    public IReadOnlyDictionary<DateTime, MarketSetupPoint> ByDate { get; } = Points.ToDictionary(point => point.Date);
}

internal static class MarketSetupDataLoader
{
    private static readonly string DataDirectory = @"D:\fear_greed_close\Breeze";
    private static readonly string CurvePath = @"D:\fear_greed_close\vix_term_structure.csv";

    public static bool TryLoad(string model, out MarketSetupSeries series, out string error)
    {
        try
        {
            if (string.Equals(model, "Rules2", StringComparison.Ordinal))
            {
                IReadOnlyList<InputRow> inputs = ResearchData.Load(
                    Rules2HoldConfidenceCalculator.DataDirectory,
                    Rules2HoldConfidenceCalculator.CurvePath).Inputs;
                IReadOnlyList<FeatureRow> features = Rules2HoldConfidenceCalculator.CalculateFeatures(inputs);
                MarketSetupPoint[] rules2Points = features.Select(row => new MarketSetupPoint(
                    row.Date, Rules2MarketCatalog.Values(row),
                    new Dictionary<string, double>(StringComparer.Ordinal))).ToArray();
                if (rules2Points.Length == 0)
                    throw new InvalidOperationException("Rules2 не сформировала ни одной даты Market Setup.");
                series = new MarketSetupSeries(rules2Points);
                error = string.Empty;
                return true;
            }
            IReadOnlyList<M.Row> rows = M.LoadCached(DataDirectory, CurvePath);
            IReadOnlyList<EmaStatePoint> additional = EmaAdditionalStates.Calculate(rows, BreezeRuleCatalog.Current.AdditionalStates);
            var prepared = new List<MarketSetupPoint>();
            var byRow = new Dictionary<int, MarketSetupPoint>();
            int lastSlowExit = -1;
            for (int index = 0; index < rows.Count; index++)
            {
                M.Row row = rows[index];
                if (!row.Ready) continue;
                bool slowExit = index > 0 && rows[index - 1].R[0] <= -0.999 && row.R[0] > -0.999;
                var values = new Dictionary<string, double>(row.Values, StringComparer.Ordinal);
                var details = new Dictionary<string, double>(row.Details, StringComparer.Ordinal)
                {
                    ["MyRSI14"] = row.R[0], ["MyRSI10"] = row.R[1],
                    ["U01.current_exit"] = slowExit ? 1.0 : 0.0,
                    ["U01.previous_exit_date"] = lastSlowExit >= 0 ? rows[lastSlowExit].Date.ToOADate() : double.NaN,
                    ["U01.previous_exit_age"] = lastSlowExit >= 0 ? index - lastSlowExit : double.NaN,
                    ["U01.renewed_saturation"] = lastSlowExit >= 0 && rows.Skip(lastSlowExit + 1)
                        .Take(index - lastSlowExit - 1).Any(item => item.R[0] <= -0.999) ? 1.0 : 0.0,
                    ["U01.body_pct"] = row.Body
                };
                double[] pastBodies = rows.Skip(Math.Max(0, index - 20)).Take(Math.Min(20, index))
                    .Select(item => Math.Abs(item.Body)).Order().ToArray();
                details["U01.median_body_pct"] = pastBodies.Length == 0 ? double.NaN :
                    pastBodies.Length % 2 == 0
                        ? (pastBodies[pastBodies.Length / 2 - 1] + pastBodies[pastBodies.Length / 2]) / 2.0
                        : pastBodies[pastBodies.Length / 2];
                var point = new MarketSetupPoint(row.Date, values, details);
                foreach(var pair in additional[index].Values) values.Add(pair.Key,pair.Value);
                foreach(var pair in additional[index].Details) details[pair.Key]=pair.Value;
                prepared.Add(point);
                byRow[index] = point;
                if (row.Get("U01") > 0 && lastSlowExit >= 0)
                {
                    if (byRow.TryGetValue(lastSlowExit, out MarketSetupPoint? first))
                        ((Dictionary<string, double>)first.Values)["U01.first_exit"] = 1.0;
                    for (int between = lastSlowExit + 1; between < index; between++)
                        if (rows[between].R[0] <= -0.999 && byRow.TryGetValue(between, out MarketSetupPoint? waiting))
                            ((Dictionary<string, double>)waiting.Values)["U01.waiting"] = 1.0;
                }
                if (slowExit) lastSlowExit = index;
            }
            MarketSetupPoint[] points = prepared.ToArray();
            if (points.Length == 0)
                throw new InvalidOperationException("The market-setup model produced no usable dates.");
            series = new MarketSetupSeries(points);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            series = MarketSetupSeries.Empty;
            error = ex.Message;
            return false;
        }
    }
}
