using System.Globalization;
using BreezeVL;

string source = args.ElementAtOrDefault(0) ?? @"D:\fear_greed_close\Breeze";

PriceSeries spy = new("SPY", ReadPrice(Path.Combine(source, "SPY.csv")));
BreadthSeries sp50 = new("sp500_sma50", 50, ReadBreadth(Path.Combine(source, "sp500_sma50.csv")), true);
BreadthSeries sp100 = new("sp500_sma100", 100, ReadBreadth(Path.Combine(source, "sp500_sma100.csv")), true);
BreadthSeries sp200 = new("sp500_sma200", 200, ReadBreadth(Path.Combine(source, "sp500_sma200.csv")), true);
BreadthSeries nasdaq = new("nasdaq100_sma100", 100, ReadBreadth(Path.Combine(source, "nasdaq100_sma100.csv")), true);

TechnicalIndicatorSeries indicators = TechnicalIndicatorCalculator.Calculate(spy);
FuzzyMarketStateSeries result = FuzzyMarketStateCalculator.Calculate(spy, sp50, sp100, sp200, nasdaq, indicators);
if (result.Points.Count == 0)
    throw new InvalidOperationException("No fuzzy-state points were calculated.");
if (result.Points.Any(point => Values(point).Any(value => double.IsNaN(value) || value < 0 || value > 1)))
    throw new InvalidOperationException("A fuzzy degree is outside [0,1] or NaN.");
if (result.Points.Any(point => point.MyRsiRep < -1.0000001 || point.MyRsiRep > 1.0000001))
    throw new InvalidOperationException("The fuzzy map contains an invalid MyRSI value.");
if (result.Points.Any(point => point.BullishEmaOrder < 0 || point.BullishEmaOrder > 1 ||
                               point.BreadthTurnUp < 0 || point.BreadthTurnUp > 1))
    throw new InvalidOperationException("A new fuzzy input membership is outside [0,1].");

HoldConfidenceSeries holdConfidence = HoldConfidenceCalculator.Calculate(
    result, indicators, null, null, null);
if (holdConfidence.Points.Count != result.Points.Count)
    throw new InvalidOperationException("Hold-confidence series is not aligned with the phase map.");
if (holdConfidence.Points.Any(point => !double.IsFinite(point.Value) || point.Value is < 0.0 or > 100.0))
    throw new InvalidOperationException("Hold confidence is outside [0,100].");
Console.WriteLine($"Hold confidence: min={holdConfidence.Points.Min(point => point.Value):F1}% " +
                  $"avg={holdConfidence.Points.Average(point => point.Value):F1}% " +
                  $"max={holdConfidence.Points.Max(point => point.Value):F1}%");

(double Hold, int Target)[] sizingCases =
[
    (0.0, 0), (0.01, 0), (10.0, 0), (49.99, 0),
    (50.0, 10), (50.01, 10), (89.99, 10), (90.0, 10), (100.0, 10)
];
foreach ((double hold, int target) in sizingCases)
{
    int actual = ModelPositionSizing.TargetContracts(hold);
    if (actual != target)
        throw new InvalidOperationException(
            $"Hold sizing failed: {hold:F2}% produced {actual}, expected {target}.");
}
Console.WriteLine("Model position sizing: fixed 50% level, 0/10 contracts verified.");

var researchProfiles = new List<HoldConfidenceSeries>();
for (int profile = 0; profile < 3; profile++)
{
    if (!ResearchHoldConfidenceCalculator.TryCalculate(profile, out HoldConfidenceSeries researchSeries, out string researchError))
        throw new InvalidOperationException($"Research profile {profile} failed: {researchError}");
    if (researchSeries.Points.Count == 0 || researchSeries.Points.Any(point => point.Profile == "Legacy" || point.Value is < 0 or > 100))
        throw new InvalidOperationException($"Research profile {profile} is invalid.");
    researchProfiles.Add(researchSeries);
    Console.WriteLine($"Research {researchSeries.Points[^1].Profile}: {researchSeries.Points.Count} usable dates; last={researchSeries.Points[^1].Value:F1}%.");
}
if (!researchProfiles[0].Points.Zip(researchProfiles[1].Points)
        .Any(pair => Math.Abs(pair.First.Value - pair.Second.Value) > 0.000001) ||
    !researchProfiles[1].Points.Zip(researchProfiles[2].Points)
        .Any(pair => Math.Abs(pair.First.Value - pair.Second.Value) > 0.000001))
    throw new InvalidOperationException("Soft, Standard and Strong profiles do not produce distinct histories.");
string researchHelp = BreezeResearchHelp.BuildHtml();
if (!researchHelp.Contains("Полный каталог 74 состояний", StringComparison.Ordinal) ||
    !researchHelp.Contains("T09", StringComparison.Ordinal) ||
    !researchHelp.Contains("0 — сохранён", StringComparison.Ordinal) ||
    !researchHelp.Contains("Бычий порядок EMA", StringComparison.Ordinal) ||
    !researchHelp.Contains("Предварительное определение", StringComparison.Ordinal))
    throw new InvalidOperationException("Embedded research Help is incomplete.");
Console.WriteLine($"Research Help generated: {researchHelp.Length:N0} characters.");
string? generatedHelpPath = args.ElementAtOrDefault(1);
if (!string.IsNullOrWhiteSpace(generatedHelpPath))
{
    string templatePath = Path.Combine(Directory.GetCurrentDirectory(), "Breeze", "BreezeHelp.html");
    string completeHelp = File.ReadAllText(templatePath).Replace(
        "<!-- RESEARCH-MODEL-HELP -->", researchHelp, StringComparison.Ordinal);
    File.WriteAllText(generatedHelpPath, completeHelp);
    Console.WriteLine($"Complete Help snapshot: {generatedHelpPath}");
}

foreach (var group in result.Points.GroupBy(point => point.DominantZone).OrderBy(group => group.Key))
    Console.WriteLine($"{group.Key,-12} {group.Count(),5}  {100.0 * group.Count() / result.Points.Count,6:F2}%");

foreach (MarketZone zone in new[] { MarketZone.Buy, MarketZone.ReduceRisk })
{
    string dates = string.Join(", ", result.Points
        .Where(point => point.DominantZone == zone)
        .Select(point => point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    Console.WriteLine($"{zone} dates: {dates}");
}

foreach (string dateText in new[] { "2018-12-24", "2020-03-23", "2020-03-24", "2022-06-16", "2022-10-18", "2023-01-06", "2023-10-06", "2023-10-09", "2024-04-23", "2025-03-17", "2026-03-30", "2026-03-31", "2026-04-01", "2026-04-02", "2026-10-08" })
{
    DateTime date = DateTime.ParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    FuzzyMarketStatePoint? point = result.Points.FirstOrDefault(item => item.Date == date);
    if (point is not null)
        Console.WriteLine($"{dateText} {point.DominantZone,-12} conf={point.Confidence:F3} score={point.Score:F3} " +
                          $"buy={point.Buy:F3} prepBuy={point.PrepareBuy:F3} caution={point.Caution:F3} " +
                          $"prepSell={point.PrepareSell:F3} risk={point.ReduceRisk:F3} " +
                          $"rsi={point.MyRsiRep:F3} emaBull={point.BullishEmaOrder:F3} breadthTurn={point.BreadthTurnUp:F3} " +
                          $"deepFall={point.DeepFallContext:F3} release={point.MyRsiReleaseFromLow:F3}");
}

FuzzyMarketStatePoint covidLow = result.Points.Single(point => point.Date == new DateTime(2020, 3, 23));
if (covidLow.Buy > 0)
    throw new InvalidOperationException("MyRSI saturation without a reversal incorrectly activated Buy.");
FuzzyMarketStatePoint covidRelease = result.Points.Single(point => point.Date == new DateTime(2020, 3, 26));
if (covidRelease.Buy <= 0)
    throw new InvalidOperationException("The post-washout reversal did not activate Buy.");
FuzzyMarketStatePoint lead2026 = result.Points.Single(point => point.Date == new DateTime(2026, 3, 31));
FuzzyMarketStatePoint release2026 = result.Points.Single(point => point.Date == new DateTime(2026, 4, 1));
if (lead2026.DominantZone != MarketZone.Buy || release2026.DominantZone != MarketZone.Buy)
    throw new InvalidOperationException("The breadth-led and MyRSI-release 2026 examples are not Buy.");

if (indicators.Points.Count != spy.Points.Count)
    throw new InvalidOperationException("Technical-indicator series is not aligned with SPY.");
if (indicators.Points.Skip(TechnicalIndicatorCalculator.MyRsiPeriod)
    .Any(point => !double.IsNaN(point.MyRsiRep) && (point.MyRsiRep < -1.0000001 || point.MyRsiRep > 1.0000001)))
    throw new InvalidOperationException("MyRSI_rep is outside [-1,1].");
if (indicators.Points.Skip(TechnicalIndicatorCalculator.FastMyRsiPeriod)
    .Any(point => !double.IsNaN(point.MyRsiRep10) && (point.MyRsiRep10 < -1.0000001 || point.MyRsiRep10 > 1.0000001)))
    throw new InvalidOperationException("MyRSI_rep 10,8 is outside [-1,1].");
if (indicators.Points.Any(point => !double.IsFinite(point.Atrp1) || point.Atrp1 < 0.0))
    throw new InvalidOperationException("ATRP(1) is invalid.");
TechnicalIndicatorPoint lastIndicator = indicators.Points[^1];
Console.WriteLine($"Indicators {lastIndicator.Date:yyyy-MM-dd}: EMA20={lastIndicator.Ema20:F2} " +
                  $"EMA50={lastIndicator.Ema50:F2} EMA100={lastIndicator.Ema100:F2} " +
                  $"EMA200={lastIndicator.Ema200:F2} MyRSI_rep+AGC(14,8,77)={lastIndicator.MyRsiRep:F4} " +
                  $"ATRP(1)={lastIndicator.Atrp1:F4}%");

DateTime syntheticStart = new(2020, 1, 1);
var syntheticQqq = new PriceSeries("QQQ", Enumerable.Range(0, 280)
    .Select(i => new PricePoint(syntheticStart.AddDays(i), 100 + i, 100 + i, 100 + i, 100 + i, 1)).ToList());
var syntheticHighs = new PriceSeries("NH", Enumerable.Range(0, 280)
    .Select(i => new PricePoint(syntheticStart.AddDays(i), 100, 100, 100, 100, 1)).ToList());
var syntheticLows = new PriceSeries("NL", Enumerable.Range(0, 280)
    .Select(i => new PricePoint(syntheticStart.AddDays(i), i >= 272 ? 150 : 50, i >= 272 ? 150 : 50,
        i >= 272 ? 150 : 50, i >= 272 ? 150 : 50, 1)).ToList());
var syntheticBreadth = new BreadthSeries("Nasdaq SMA50", 50, Enumerable.Range(0, 280)
    .Select(i => new BreadthPoint(syntheticStart.AddDays(i), 60, 60, 60, 60, 100)).ToList(), true);
TitanicSeries titanic = TitanicSyndromeCalculator.Calculate(
    syntheticQqq, syntheticHighs, syntheticLows, syntheticBreadth,
    syntheticStart, syntheticStart.AddDays(279));
IReadOnlyList<TitanicPoint> activeTitanic = titanic.Points.Where(point => point.Signal).ToList();
if (activeTitanic.Count != 8 || activeTitanic[0].RunLength != 1 || activeTitanic[0].Persistent ||
    activeTitanic[6].RunLength != 7 || !activeTitanic[6].Persistent || titanic.UseBreadthFilter ||
    Math.Abs(titanic.HighTolerancePercent - 3.0) > 0.000001)
    throw new InvalidOperationException("SentimenTrader Titanic defaults or persistence logic are incorrect.");
Console.WriteLine($"Titanic synthetic check: {activeTitanic.Count} active days; persistent from day {activeTitanic[6].RunLength}; MA filter={titanic.UseBreadthFilter}.");
TitanicSeries adjustedTitanic = TitanicSyndromeCalculator.WithPersistentDays(titanic, 5);
IReadOnlyList<TitanicPoint> adjustedActive = adjustedTitanic.Points.Where(point => point.Signal).ToList();
if (adjustedTitanic.PersistentDays != 5 || adjustedActive[3].Persistent || !adjustedActive[4].Persistent)
    throw new InvalidOperationException("Interactive Titanic persistence threshold is incorrect.");

PriceSeries highsWithGap = syntheticHighs with
{
    Points = syntheticHighs.Points.Where(point => point.Date != syntheticStart.AddDays(275)).ToList()
};
TitanicSeries titanicWithGap = TitanicSyndromeCalculator.Calculate(
    syntheticQqq, highsWithGap, syntheticLows, syntheticBreadth,
    syntheticStart, syntheticStart.AddDays(279));
TitanicPoint firstAfterGap = titanicWithGap.Points.Single(point => point.Date == syntheticStart.AddDays(276));
if (firstAfterGap.RunLength != 1 || titanicWithGap.Points.Any(point => point.Persistent))
    throw new InvalidOperationException("Titanic persistence incorrectly crosses a missing market-data date.");

var zweigAdvancing = new PriceSeries("ADV", Enumerable.Range(0, 120)
    .Select(i => new PricePoint(syntheticStart.AddDays(i), i < 20 ? 50 : 70,
        i < 20 ? 50 : 70, i < 20 ? 50 : 70, i < 20 ? 50 : 70, 1)).ToList());
var zweigDeclining = new PriceSeries("DEC", Enumerable.Range(0, 120)
    .Select(i => new PricePoint(syntheticStart.AddDays(i), i < 20 ? 50 : 30,
        i < 20 ? 50 : 30, i < 20 ? 50 : 30, i < 20 ? 50 : 30, 1)).ToList());
ZweigSeries strongZweig = ZweigBreadthCalculator.Calculate(
    zweigAdvancing, zweigDeclining, ZweigMode.Strong, syntheticStart, syntheticStart.AddDays(119));
ZweigSeries workingZweig = ZweigBreadthCalculator.Calculate(
    zweigAdvancing, zweigDeclining, ZweigMode.Working, syntheticStart, syntheticStart.AddDays(119));
if (strongZweig.TriggerLevel != 60 || strongZweig.TransitionDays != 11 || strongZweig.MinimumMove != 21 ||
    workingZweig.TriggerLevel != 60 || workingZweig.TransitionDays != 13 || workingZweig.MinimumMove != 15)
    throw new InvalidOperationException("Fixed Zweig modes changed unexpectedly.");
if (strongZweig.Points.Any(point => point.Signal) || workingZweig.Points.Count(point => point.Signal) != 1)
    throw new InvalidOperationException("Strong and Working Zweig modes are not separated by their fixed minimum move.");
ZweigPoint workingSignal = workingZweig.Points.Single(point => point.Signal);
if (workingSignal.Density != 30 ||
    workingZweig.Points.Single(point => point.BarsSinceSignal == 51).Density != 15 ||
    workingZweig.Points.Single(point => point.BarsSinceSignal == 66).Density != 5 ||
    workingZweig.Points.Single(point => point.BarsSinceSignal == 86).Density != 0)
    throw new InvalidOperationException("Zweig density must remain 30/15/5/0.");
Console.WriteLine($"Zweig synthetic check: Working signal {workingSignal.Date:yyyy-MM-dd}; density 30/15/5/0 verified.");

static IEnumerable<double> Values(FuzzyMarketStatePoint point) =>
    [point.Buy, point.PrepareBuy, point.Neutral, point.Caution, point.PrepareSell, point.ReduceRisk, point.Confidence];

static List<PricePoint> ReadPrice(string path) => ReadRows(path)
    .Select(row => new PricePoint(row.Date, row.Open, row.High, row.Low, row.Close, row.Volume))
    .ToList();

static List<BreadthPoint> ReadBreadth(string path) => ReadRows(path)
    .Select(row => new BreadthPoint(row.Date, row.Open, row.High, row.Low, row.Close, row.EligibleCount))
    .ToList();

static IEnumerable<(DateTime Date, double Open, double High, double Low, double Close, double Volume, int EligibleCount)> ReadRows(string path)
{
    foreach (string line in File.ReadLines(path).Skip(1))
    {
        string[] cells = line.Split(',');
        yield return (
            DateTime.ParseExact(cells[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            double.Parse(cells[1], CultureInfo.InvariantCulture),
            double.Parse(cells[2], CultureInfo.InvariantCulture),
            double.Parse(cells[3], CultureInfo.InvariantCulture),
            double.Parse(cells[4], CultureInfo.InvariantCulture),
            double.Parse(cells[5], CultureInfo.InvariantCulture),
            cells.Length > 6 ? int.Parse(cells[6], CultureInfo.InvariantCulture) : 500);
    }
}

namespace BreezeVL
{
    internal sealed record BreadthPoint(
        DateTime Date,
        double Open,
        double High,
        double Low,
        double Close,
        int EligibleCount)
    {
        public double Value => Close;
    }

    internal sealed record BreadthSeries(
        string Name,
        int Period,
        IReadOnlyList<BreadthPoint> Points,
        bool HasOhlc);

    internal sealed record PricePoint(
        DateTime Date,
        double Open,
        double High,
        double Low,
        double Close,
        double Volume);

    internal sealed record PriceSeries(string Name, IReadOnlyList<PricePoint> Points);
}
