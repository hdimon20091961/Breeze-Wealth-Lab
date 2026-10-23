using System.IO;
using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;

namespace BreezeVL;

internal enum ResearchCalculationFailure
{
    None,
    SnapshotNotReady,
    Unexpected
}

/// <summary>
/// Adapter between the autonomous 75-event research model and the Breeze UI.
/// The calculation itself is compiled from the same source as the standalone
/// Wealth-Lab strategy, so the DLL cannot silently drift to a different rule.
/// </summary>
internal static class ResearchHoldConfidenceCalculator
{
    private static readonly string DataDirectory = @"D:\fear_greed_close\Breeze";
    private static readonly string CurvePath = @"D:\fear_greed_close\vix_term_structure.csv";
    private const double BiasPct = 50.0;

    public static bool TryCalculate(int profile, out HoldConfidenceSeries series, out string error,
        out ResearchCalculationFailure failure)
    {
        try
        {
            IReadOnlyList<M.Row> rows = M.LoadCached(DataDirectory, CurvePath);
            double[] weights = BreezeRuleCatalog.Current.BreezeWeights(profile);
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
                string active = string.Join("; ", contributions
                    .OrderByDescending(item => Math.Abs(item.Value))
                    .Take(6)
                    .Select(item => HumanLanguage.Contribution(item.Id, item.Value)));
                string topContributions = string.Join(Environment.NewLine, contributions
                    .OrderByDescending(item => Math.Abs(item.Value))
                    .Take(10)
                    .Select(item => "• " + HumanLanguage.Contribution(item.Id, item.Value) + "."));
                if (string.IsNullOrWhiteSpace(topContributions))
                    topContributions = "• Ненулевых вкладов в оценку модели нет.";

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
                    $"{HumanLanguage.Profile(profileName)} модель: оценка {value:F1} балла из 100; " +
                    $"положительный фактор — {support}; отрицательный фактор — {risk}.",
                    profileName,
                    support,
                    risk,
                    active,
                    topContributions));
            }
            if (points.Count == 0)
                throw new InvalidOperationException("The research model produced no usable dates after its 252-bar warm-up.");
            series = new HoldConfidenceSeries(points);
            error = string.Empty;
            failure = ResearchCalculationFailure.None;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException ||
                                   ex is InvalidOperationException &&
                                   ex.Message.Contains("no usable dates", StringComparison.OrdinalIgnoreCase))
        {
            series = new HoldConfidenceSeries([]);
            error = ex.Message;
            failure = ResearchCalculationFailure.SnapshotNotReady;
            return false;
        }
        catch (Exception ex)
        {
            series = new HoldConfidenceSeries([]);
            error = ex.Message;
            failure = ResearchCalculationFailure.Unexpected;
            return false;
        }
    }

    private sealed record Contribution(string Id, string Name, double Value);

    private static string DescribeContribution(Contribution? item, bool support)
    {
        if (item is null)
            return support ? "заметной поддержки нет" : "заметного давления нет";
        return HumanLanguage.Contribution(item.Id, item.Value);
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
