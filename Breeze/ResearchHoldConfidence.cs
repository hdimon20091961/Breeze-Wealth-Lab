using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;

namespace BreezeVL;

/// <summary>
/// Adapter between the autonomous 74-event research model and the Breeze UI.
/// The calculation itself is compiled from the same source as the standalone
/// Wealth-Lab strategy, so the DLL cannot silently drift to a different rule.
/// </summary>
internal static class ResearchHoldConfidenceCalculator
{
    private static readonly string DataDirectory = @"D:\fear_greed_close\Breeze";
    private static readonly string CurvePath = @"D:\fear_greed_close\vix_term_structure.csv";
    private const double BiasPct = 50.0;

    public static bool TryCalculate(int profile, out HoldConfidenceSeries series, out string error)
    {
        try
        {
            IReadOnlyList<M.Row> rows = M.LoadCached(DataDirectory, CurvePath);
            double[] weights = M.PresetWeights(profile);
            string profileName = M.PresetName(profile);
            var points = new List<HoldConfidencePoint>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                M.Row row = rows[i];
                if (!row.Ready)
                    continue;
                double value = M.ScoreRow(row, weights, BiasPct);
                var contributions = M.Features
                    .Select((feature, index) => new Contribution(
                        feature.Id, feature.Name, weights[index] * row.Get(feature.Id)))
                    .Where(item => Math.Abs(item.Value) > 0.0000001)
                    .ToArray();
                string support = DescribeContribution(contributions
                    .Where(item => item.Value > 0)
                    .OrderByDescending(item => item.Value)
                    .FirstOrDefault(), true);
                string risk = DescribeContribution(contributions
                    .Where(item => item.Value < 0)
                    .OrderBy(item => item.Value)
                    .FirstOrDefault(), false);
                string active = string.Join(", ", contributions
                    .OrderByDescending(item => Math.Abs(item.Value))
                    .Take(6)
                    .Select(item => $"{item.Id} {item.Value:+0.0;-0.0} pp"));

                points.Add(new HoldConfidencePoint(
                    row.Date,
                    value,
                    GroupState(row, weights, "T", "E"),
                    GroupState(row, weights, "B"),
                    GroupState(row, weights, "R", "C"),
                    GroupState(row, weights, "P"),
                    GroupState(row, weights, "V"),
                    GroupState(row, weights, "Z"),
                    NegativeState(row, weights, "I"),
                    NegativeState(row, weights, "T", "E", "P", "V", "C"),
                    $"{profileName}: score {value:F1}%; support {support}; risk {risk}.",
                    profileName,
                    support,
                    risk,
                    active));
            }
            if (points.Count == 0)
                throw new InvalidOperationException("The research model produced no usable dates after its 252-bar warm-up.");
            series = new HoldConfidenceSeries(points);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            series = new HoldConfidenceSeries([]);
            error = ex.Message;
            return false;
        }
    }

    private sealed record Contribution(string Id, string Name, double Value);

    private static string DescribeContribution(Contribution? item, bool support)
    {
        if (item is null)
            return support ? "no positive event" : "no negative event";
        return $"{item.Id} {item.Name} ({item.Value:+0.0;-0.0} pp)";
    }

    private static double GroupState(M.Row row, double[] weights, params string[] prefixes)
    {
        double contribution = 0, scale = 0;
        for (int i = 0; i < M.Features.Length; i++)
        {
            if (!prefixes.Any(prefix => M.Features[i].Id.StartsWith(prefix, StringComparison.Ordinal)))
                continue;
            contribution += weights[i] * row.Get(M.Features[i].Id);
            scale += Math.Abs(weights[i]);
        }
        return scale == 0 ? 0.5 : Math.Clamp(0.5 + contribution / (2 * scale), 0, 1);
    }

    private static double NegativeState(M.Row row, double[] weights, params string[] prefixes)
    {
        double negative = 0, scale = 0;
        for (int i = 0; i < M.Features.Length; i++)
        {
            if (!prefixes.Any(prefix => M.Features[i].Id.StartsWith(prefix, StringComparison.Ordinal)))
                continue;
            double contribution = weights[i] * row.Get(M.Features[i].Id);
            if (contribution < 0) negative -= contribution;
            scale += Math.Abs(weights[i]);
        }
        return scale == 0 ? 0 : Math.Clamp(negative / scale, 0, 1);
    }
}
