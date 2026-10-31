using BreezeVL;
using System.IO;
using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;
using WealthLab.Core;
using WealthLab.AdvancedSmoothers;
using WealthLab.Indicators;
using WealthLab.TASC;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
void Equal(double actual, double expected, string message, double tolerance = 1e-8) =>
    Check(double.IsFinite(actual) && Math.Abs(actual - expected) < tolerance,
        $"{message}: actual {actual}, expected {expected}");

var spy = new BarHistory("SPY", HistoryScale.Daily);
var external = new BarHistory("EXTERNAL", HistoryScale.Daily);
var highs = new BarHistory("HIGHS", HistoryScale.Daily);
var lows = new BarHistory("LOWS", HistoryScale.Daily);
DateTime firstDate = new(2020, 1, 1);
for (int index = 0; index < 900; index++)
{
    DateTime date = firstDate.AddDays(index);
    double close = 150 + index * 0.07 + 15 * Math.Sin(index * 0.12) + 4 * Math.Cos(index * 0.041);
    spy.Add(date, close - 0.5, close + 2, close - 2, close, 1_000_000 + index * 100);
    external.Add(date, 50, 50, 50, 50, 1);
    double high = 20 + index % 31, low = 10 + index % 17;
    highs.Add(date, high, high, high, high, 1);
    lows.Add(date, low, low, low, low, 1);
}
SentimentSeries series = SentimentCalculator.Calculate(spy, external, external, external,
    external, external, highs, lows, [spy], firstDate, firstDate.AddDays(899));
Check(series.Points.Count == spy.Count, "All indicator series must retain master dates");
Check(SentimentIndicatorCatalog.All.Length == 20, "Exactly twenty agreed indicator series");
Check(!SentimentIndicatorCatalog.All.Any(item => SentimentIndicatorCatalog.DisplayName(item).Contains("14,7")),
    "Market Setup must not expose the ensemble-only MyRSI 14,7");
var up = new AroonUp(spy.Close, 40);
var down = new AroonDown(spy.Close, 48);
var minus = new DIMinus(spy, 7);
var plus = new DIPlus(spy, 14);
var angle = new CorrelationAngle_rep(spy.Close, 28);
var trend = new CorrelationTrend(spy.Close, 35);
var vwma = new VWMA(spy.Close, 64, spy);
double minusPeak = 1e-7, plusPeak = 1e-7, trendPeak = 1e-7;
for (int index = 0; index < spy.Count; index++)
{
    double minusRaw = (minus[index] - 30) / 30, plusRaw = (plus[index] - 25) / 30;
    if (!double.IsNaN(minusRaw)) minusPeak = Math.Max(minusPeak * 0.991, Math.Abs(minusRaw));
    if (!double.IsNaN(plusRaw)) plusPeak = Math.Max(plusPeak * 0.991, Math.Abs(plusRaw));
    if (index >= trend.FirstValidIndex && !double.IsNaN(trend[index]))
        trendPeak = Math.Max(trendPeak * 0.991, Math.Abs(trend[index]));
    if (index < 200) continue;
    SentimentPoint point = series.Points[index];
    Equal(point.AroonUp, (up[index] - 50) / 50, "Aroon Up 40");
    Equal(point.AroonDown, (down[index] - 50) / 50, "Aroon Down 48");
    Equal(point.DiMinus, minusRaw / minusPeak, "DI- 7 with AGC");
    Equal(point.DiPlus, plusRaw / plusPeak, "DI+ 14 with AGC");
    Equal(point.CorrelationAngle, angle[index] / 180, "Correlation Angle 28");
    Equal(point.CorrelationTrend, trend[index] / trendPeak, "Correlation Trend 35 with AGC");
    Equal(point.MarketBreadth, spy.Close[index] > vwma[index] ? 0.5 : -0.5,
        "ZZ1/ZZ2 Market Breadth uses VWMA64 with EMA1");
    Check(point.MyRsi is >= -1 and <= 1 && point.MyRsiFast is >= -1 and <= 1, "Both MyRSI series in range");
}
double[] jockerRaw = Enumerable.Range(0, spy.Count).Select(index =>
{
    int start = Math.Max(0, index - 64);
    double high = Enumerable.Range(start, index - start + 1).Sum(i => highs.Close[i]);
    double low = Enumerable.Range(start, index - start + 1).Sum(i => lows.Close[i]);
    return (high - low) / (high + low);
}).ToArray();
double jocker = jockerRaw[0];
for (int index = 1; index < spy.Count; index++)
{
    jocker = 0.5 * jockerRaw[index] + 0.5 * jocker;
    Equal(series.Points[index].Jocker, jocker, "ZZ1/ZZ2 Jocker mode 0, window 65, EMA3");
}
Check(series.Points.Skip(200).Any(point => Math.Abs(point.MyRsi - point.MyRsiFast) > 0.1),
    "MyRSI 14,8 and 10,8 must be distinct");
foreach (int period in new[] { 14, 10 })
{
    var replica = new MyRSI_rep(spy.Close, period, 8);
    double peak = 1e-7;
    double decay = Math.Pow(0.5, 1.0 / 77);
    for (int index = Math.Max(8, period); index < spy.Count; index++)
    {
        peak = Math.Max(peak * decay, Math.Abs(replica[index]));
        if (index < 500) continue;
        double actual = period == 14 ? series.Points[index].MyRsi : series.Points[index].MyRsiFast;
        Equal(actual, Math.Clamp(replica[index] / peak, -1, 1), $"MyRSI_rep({period},8) + AGC(77)", 1e-6);
    }
}
Console.WriteLine("Indicator checks passed: Aroon, DI/AGC, correlations, both MyRSI, ZZ1/ZZ2 Jocker and Market Breadth.");
ChartRenderingCheck.Run(series, Path.GetFullPath(args.ElementAtOrDefault(0) ?? "tests/outputs/market-setup"));

Check(MarketSetupDataLoader.TryLoad("Rules1", out MarketSetupSeries setups, out string error), error);
Check(BreezeRuleCatalog.Current.Rules.Count == 75, "All seventy-five rules must remain available");
BreezeRuleEntry firstRule = BreezeRuleCatalog.Current.Rules[0];
Check(!(firstRule with { SoftWeight = 0, StandardWeight = 0, HardWeight = 0 }).IsActiveInBreeze,
    "Zero-weight rules must be excluded by the Active filter");
Check((firstRule with { SoftWeight = -5, StandardWeight = 0, HardWeight = 0 }).IsActiveInBreeze &&
      (firstRule with { SoftWeight = 0, StandardWeight = 10, HardWeight = 0 }).IsActiveInBreeze &&
      (firstRule with { SoftWeight = 0, StandardWeight = 0, HardWeight = -5 }).IsActiveInBreeze,
    "The Active filter must accept any nonzero profile, including negative weights");
string titanicDescription = MarketSetupDescription.Build(BreezeRuleCatalog.Current.ById("I01"));
Check(titanicDescription.StartsWith("Soft: -5; Standard: -5; Hard: -5.\n") &&
      !titanicDescription.Contains("Веса Breeze") && !titanicDescription.Contains("Мягкий") &&
      !titanicDescription.Contains("Правило №"),
    "Weights must replace the removed rule-number/type line, with Phase Map profile names and no duplicate section");
Console.WriteLine($"Active rules: {BreezeRuleCatalog.Current.Rules.Count(rule => rule.IsActiveInBreeze)} / 75; profile labels and weight placement verified.");
BreezeMarketCatalogSet catalog = BreezeMarketCatalog.Current;
Check(catalog.Entries.Count == 149 && catalog.RuntimeCount == 86 && catalog.RuntimeActive == 32 &&
      catalog.AcceptedSelected == 77 && catalog.AcceptedNonzero == 34,
    "The UI must distinguish all research entries, runtime rules and the accepted research model");
foreach (string id in new[] { "VB01", "VB08", "PH01", "PH06", "PX01", "PX02", "ST01", "ST24", "EN01", "S1-G01" })
{
    BreezeMarketEntry entry = catalog.Entries.Single(item => item.Id == id);
    Check(entry.RuntimeRule is null && !entry.IsActiveInBreeze,
        $"Research-only entry {id} must not acquire fabricated runtime weights");
    Check(!string.IsNullOrWhiteSpace(entry.Definition) && entry.ResearchRecord.ValueKind == System.Text.Json.JsonValueKind.Object,
        $"Definition and full saved research record must be preserved for {id}");
    Check(BreezeMarketCatalog.Describe(entry).Contains("рабоч"), $"Research/runtime status missing for {id}");
}
Check(catalog.Entries.Single(item => item.Id == "PH01").AcceptedNonzero &&
      catalog.Entries.Single(item => item.Id == "S1-G01").AcceptedNonzero,
    "The two accepted research overlays must remain visible as accepted, separately from runtime weights");
foreach (string id in Enumerable.Range(9, 8).Select(number => $"V{number:00}"))
{
    BreezeMarketEntry entry = catalog.Entries.Single(item => item.Id == id);
    Check(entry.Family == "VIX и форма срочной кривой" && !string.IsNullOrWhiteSpace(entry.Definition),
        $"Contango 2 catalogue event {id} must retain its family and definition");
}
Console.WriteLine("Complete catalogue passed: 149 entries, including V09-V16; 86 runtime, 75 frozen scoring features, 77 accepted/34 nonzero.");
Check(BreezeRuleCatalog.Current.AdditionalStates.Count==11 && BreezeRuleCatalog.Current.AllRules.Count==86,"All eleven EMA display states must be enabled");
var modelRows=M.LoadCached(@"D:\fear_greed_close\Breeze",@"D:\fear_greed_close\vix_term_structure.csv");
int bearAge=0;
for(int i=1;i<modelRows.Count;i++)
{
    var row=modelRows[i];var previous=modelRows[i-1];
    bool finite=double.IsFinite(row.Get("T01"));
    bool bearOrder=Enumerable.Range(0,3).All(k=>row.E[k]<row.E[k+1]);
    bool falling=Enumerable.Range(0,4).All(k=>row.E[k]<previous.E[k]);
    bool bear=finite && bearOrder && falling && row.Close<row.E[0] && row.B.All(v=>v<=50) && row.Atr<=2;
    bearAge=bear ? bearAge+1 : 0;
    if(!row.Ready)continue;
    var point=setups.ByDate[row.Date];
    Equal(point.Value("T01B"),bearOrder ? 1 : 0,"Pure bear order");
    Equal(point.Value("T02B"),falling ? 1 : 0,"All four EMA falling");
    Equal(point.Value("T03B"),Math.Min(bearAge/5.0,1),"Causal bear strength");
    Equal(point.Value("T03_1"),row.Get("T03")==1 ? 1 : 0,"Confirmed bull control");
    Equal(point.Value("T03_1B"),bearAge>=5 ? 1 : 0,"Confirmed bear control");
    Equal(point.Value("T04_1")+point.Value("T04_3"),row.Get("T04"),"Original T04 union");
    foreach(var rule in BreezeRuleCatalog.Current.AdditionalStates)
    {
        Check(double.IsFinite(point.Value(rule.Id)),"New daily state must have a calculated dated value");
        Check(!row.Values.ContainsKey(rule.Id),"The frozen score vector must not be mutated");
    }
}
int[][] patterns=[[-1,1,1,1],[1,-1,1,1],[-1,-1,1,1],[1,-1,-1,-1],[-1,1,-1,-1],[1,1,-1,-1]];
foreach(var pattern in patterns.Select((signs,index)=>(signs,index)))
{
    var before=new M.Row { E=[10,40,20,30] }; before.Values["T01"]=0;
    var after=new M.Row { E=before.E.Select((v,k)=>v+pattern.signs[k]).ToArray() }; after.Values["T01"]=0;
    var calculated=EmaAdditionalStates.Calculate([before,after],BreezeRuleCatalog.Current.AdditionalStates);
    for(int k=0;k<6;k++)Equal(calculated[1].Values[$"T04_{k+1}"],k==pattern.index ? 1 : 0,"Exclusive daily T04 pattern, independent of level order");
}
var prefixStates=EmaAdditionalStates.Calculate(modelRows.Take(500).ToArray(),BreezeRuleCatalog.Current.AdditionalStates);
var fullStates=EmaAdditionalStates.Calculate(modelRows,BreezeRuleCatalog.Current.AdditionalStates);
for(int i=0;i<prefixStates.Count;i++)foreach(var rule in BreezeRuleCatalog.Current.AdditionalStates)
    Check(prefixStates[i].Values[rule.Id].Equals(fullStates[i].Values[rule.Id]),"Appending future bars must not change earlier EMA state/age");
Console.WriteLine("Additional EMA states passed: eleven daily detectors, 0/1 states, causal integer ages, all six patterns, no future leakage, unchanged score vector.");
foreach (BreezeRuleEntry rule in BreezeRuleCatalog.Current.Rules)
{
    Check(rule.ImplementedFormulaRu.Any(character => character is >= 'А' and <= 'я'), $"Russian detector missing for {rule.Id}");
    string description = MarketSetupDescription.Build(rule);
    Check(!description.Contains("not_applicable") && !description.Contains("disabled") &&
          !description.Contains("Fraction"), $"Raw English implementation text for {rule.Id}");
}
MarketSetupPoint example = setups.ByDate[new DateTime(2026, 3, 12)];
Equal(example.Value("T02"), 0.25, "User video T02 example must preserve actual fractional detector");
string explanation = MarketSetupDescription.AtCursor(BreezeRuleCatalog.Current.ById("T02"), example);
Check(explanation.Contains("Значение правила: 0,250") && !explanation.Contains("EMA"), "Cursor description must contain only date and rule value");
foreach (var rule in BreezeRuleCatalog.Current.AllRules)
{
    string cursorText=MarketSetupDescription.AtCursor(rule,example);
    Check(cursorText.Trim().Split('\n').Length==2, "No duplicated numeric diagnostics in description: "+rule.Id);
}
foreach (MarketSetupPoint point in setups.Points.Where(point => point.Value("U01") > 0))
{
    DateTime previous = DateTime.FromOADate(point.Detail("U01.previous_exit_date"));
    Check(setups.ByDate[previous].Value("U01.first_exit") > 0, "Repeated-release first event must be visible");
    Check(point.Detail("U01.previous_exit_age") is >= 5 and <= 15 && point.Detail("U01.renewed_saturation") > 0,
        "U01 explanation must match causal detector conditions");
}
Console.WriteLine($"Rule checks passed: {setups.Points.Count} dates, 75 Russian descriptions, T02 video reference, U01 sequence marks.");
Check(Rules2MarketCatalog.Current.Entries.Count==11 && Rules2MarketCatalog.Current.RuntimeActive==9,
    "Rules2 Market Setup must expose all 11 real factors and 9 nonzero-weight factors");
Check(MarketSetupDataLoader.TryLoad("Rules2",out MarketSetupSeries rules2Setups,out string rules2Error),rules2Error);
var rules2Inputs=BreezeRules2.Research.ResearchData.Load(Rules2HoldConfidenceCalculator.DataDirectory,
    Rules2HoldConfidenceCalculator.CurvePath).Inputs;
var rules2Features=Rules2HoldConfidenceCalculator.CalculateFeatures(rules2Inputs);
var expectedRules2=Rules2MarketCatalog.Values(rules2Features[^1]);
var actualRules2=rules2Setups.ByDate[rules2Features[^1].Date];
foreach(var pair in expectedRules2)
    Check(double.IsNaN(pair.Value)?double.IsNaN(actualRules2.Value(pair.Key)):
        Math.Abs(pair.Value-actualRules2.Value(pair.Key))<1e-12,"Rules2 Market Setup drift: "+pair.Key);
Check(Rules2MarketCatalog.Describe(Rules2MarketCatalog.Current.Entries.Single(e=>e.Id=="H02"))
    .Contains("Вес Rules2: -4,5"),"Rules2 human description/weight missing");
Console.WriteLine("Rules2 Market Setup passed: 11 exact factors, model-specific descriptions and dated values.");
