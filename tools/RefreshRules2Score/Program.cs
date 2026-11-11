using System.Globalization;
using System.Text;
using BreezeVL;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string dataDirectory = Path.GetFullPath(args.ElementAtOrDefault(0)
    ?? @"D:\fear_greed_close\Breeze");
string curvePath = Path.GetFullPath(args.ElementAtOrDefault(1)
    ?? @"D:\fear_greed_close\vix_term_structure_rules2.csv");
string outputPath = Path.GetFullPath(args.ElementAtOrDefault(2)
    ?? Path.Combine(Environment.CurrentDirectory, "rules2_score.corrected.csv"));

var inputs = BreezeRules2.Research.ResearchData.Load(dataDirectory, curvePath).Inputs;
var features = Rules2HoldConfidenceCalculator.CalculateFeatures(inputs);
HoldConfidenceSeries series = Rules2HoldConfidenceCalculator.Calculate(features, 1);

static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';
var csv = new StringBuilder();
csv.AppendLine("Date,HoldConfidencePct,Band,Trend,Breadth,Phase,VolatilityCalm,Contango,ZweigSupport,TitanicPenalty,ShockPenalty,Rationale,Profile,MainSupport,MainRisk,ActiveEvents,Model,RawScore,EntryThreshold,ExitThreshold,TargetContracts,ScoreKnown,DataState,Status");
foreach (HoldConfidencePoint point in series.Points)
{
    bool known = double.IsFinite(point.Value);
    csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
        .Append(known ? point.Value.ToString("0.########", CultureInfo.InvariantCulture) : string.Empty).Append(',')
        .Append(known ? ScorePalette.Label(point.Value) : "Unknown").Append(',')
        .Append(point.TrendComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
        .Append(point.BreadthComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
        .Append(point.PhaseComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
        .Append(point.VolatilityComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
        .Append(point.ContangoComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
        .Append(point.ZweigComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
        .Append(point.TitanicPenalty.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
        .Append(point.ShockPenalty.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
        .Append(Quote(point.Rationale)).Append(',')
        .Append(Quote(point.Profile)).Append(',')
        .Append(Quote(point.MainSupport)).Append(',')
        .Append(Quote(point.MainRisk)).Append(',')
        .Append(Quote(point.ActiveEvents)).Append(',')
        .Append(point.Model).Append(',')
        .Append(known && double.IsFinite(point.RawValue)
            ? point.RawValue.ToString("0.########", CultureInfo.InvariantCulture) : string.Empty).Append(',')
        .Append(point.EntryThreshold.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
        .Append(point.ExitThreshold.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
        .Append(ModelPositionSizing.TargetContracts(point)).Append(',')
        .Append(known ? "1" : "0").Append(',')
        .Append("Current,").AppendLine(Quote(string.Empty));
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
    ?? throw new InvalidOperationException("Output path has no directory."));
File.WriteAllText(outputPath, csv.ToString(), new UTF8Encoding(false));

int unknown = series.Points.Count(point => !double.IsFinite(point.Value));
int postWarmupUnknown = series.Points.Count(point => point.Date >= new DateTime(2015, 1, 1)
    && !double.IsFinite(point.Value));
Console.WriteLine($"Wrote {series.Points.Count:N0} rows to {outputPath}");
Console.WriteLine($"Unknown={unknown:N0}; unknown since 2015-01-01={postWarmupUnknown:N0}");
