using System.Globalization;
using BreezeRules2.Research;
using BreezeVL;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string root=Path.GetFullPath(args.ElementAtOrDefault(0) ?? ".");
int checks=0;
void Check(bool condition,string message){ checks++;if(!condition)throw new Exception(message); }
var inputs=ResearchData.Load(Path.Combine(root,"research/artifacts/event_hypotheses_xhigh_v1/baseline/data/Breeze"),
    Path.Combine(root,"research/rules2_vix_term_structure/vix_term_structure_rules2.csv")).Inputs;
var features=Rules2HoldConfidenceCalculator.CalculateFeatures(inputs);
var inputByDate=inputs.ToDictionary(row=>row.Date);
var reference=File.ReadAllLines(Path.Combine(root,"research/model_comparison_20261028/rules2_common_inputs.csv")).Skip(1)
    .Select(line=>line.Split(',')).ToDictionary(c=>DateTime.Parse(c[0]),c=>string.IsNullOrEmpty(c[1])?double.NaN:double.Parse(c[1]));
var weights=Rules2HoldConfidenceCalculator.Weights;
foreach(int profile in Enumerable.Range(0,3))
{
    var points=Rules2HoldConfidenceCalculator.Calculate(features,profile).Points;
    var (entry,exit)=Rules2HoldConfidenceCalculator.Thresholds(profile);
    bool holding=false;int unknown=0;int correctedRolls=0;
    foreach(var p in points)
    {
        double frozenRaw=reference[p.Date];
        InputRow input=inputByDate[p.Date];
        bool correctedRoll=input.VixRollFlag==1 && double.IsFinite(input.AdjustedCurveRegimeScore);
        double raw=correctedRoll?p.RawValue:frozenRaw;
        if(correctedRoll)
        {
            correctedRolls++;
            Check(double.IsFinite(p.RawValue),"Corrected VIX roll must keep Rules2 continuous");
        }
        else Check(double.IsNaN(frozenRaw)==double.IsNaN(p.RawValue),"Availability mismatch");
        if(double.IsFinite(raw))
        {
            if(!correctedRoll)Check(Math.Abs(raw-p.RawValue)<1e-10,"Raw score drift");
            double normalized=100*(raw-weights.MinimumScore)/(weights.MaximumScore-weights.MinimumScore);
            Check(Math.Abs(p.Value-normalized)<1e-10,"Normalization drift");
            // Independent state replay in display coordinates, including equality.
            if(holding){if(normalized<=exit)holding=false;}else if(normalized>=entry)holding=true;
        }
        else unknown++;
        Check(ModelPositionSizing.TargetContracts(p)==(holding?10:0),"Position/state drift");
        Check(p.Model=="Rules2" && p.EntryThreshold==entry && p.ExitThreshold==exit,"Profile metadata");
    }
    if(correctedRolls>0)
    {
        Check(correctedRolls>100,"Corrected roll-day coverage unexpectedly small");
        Check(unknown>0 && unknown<100,"Only genuine warm-up/missing inputs may remain unknown");
    }
    else Check(unknown>100,"Legacy fixture without corrected curve scores must preserve its unknown rolls");
    Console.WriteLine($"Rules2 profile {profile}: {points.Count} dates, {correctedRolls} corrected rolls in fixture, {unknown} missing; all frozen non-roll scores/positions match.");
}
var liveInputs=ResearchData.Load(@"D:\fear_greed_close\Breeze",@"D:\fear_greed_close\vix_term_structure_rules2.csv").Inputs;
var liveFeatures=Rules2HoldConfidenceCalculator.CalculateFeatures(liveInputs);
var livePoints=Rules2HoldConfidenceCalculator.Calculate(liveFeatures,1).Points.ToDictionary(point=>point.Date);
var knownLiveRolls=liveInputs.Where(row=>row.Date>=new DateTime(2015,1,1) && row.VixRollFlag==1
    && double.IsFinite(row.AdjustedCurveRegimeScore)).ToArray();
Check(knownLiveRolls.Length>100,"Live corrected roll-day coverage unexpectedly small");
foreach(InputRow row in knownLiveRolls)
    Check(double.IsFinite(livePoints[row.Date].RawValue),$"Rules2 is discontinuous on corrected roll {row.Date:yyyy-MM-dd}");
var liveUnknown=livePoints.Values.Where(point=>!double.IsFinite(point.RawValue)).ToArray();
Check(liveUnknown.All(point=>point.Date<new DateTime(2015,1,1)),"Rules2 still has an unexpected post-warm-up gap");
Console.WriteLine($"Rules2 live continuity: {knownLiveRolls.Length} corrected VIX rolls have finite scores; {liveUnknown.Length} initial warm-up values remain unknown.");
foreach(var (value,band) in new (double,int)[]{(0,0),(29.99,0),(30,1),(44.99,1),(45,2),(50,2),(55,2),(55.01,3),(69.99,3),(70,4),(100,4),(double.NaN,-1)})
    Check(ScorePalette.Band(value)==band,"Palette boundary");
Check(Math.Abs(Rules2HoldConfidenceCalculator.Normalize(50)-64)<1e-10,"Zero-feature bias is 64 normalized points");
Check(ScorePalette.Label(29.9)=="Very low" && ScorePalette.Label(30)=="Low" &&
      ScorePalette.Label(55)=="Neutral" && ScorePalette.Label(70)=="Very high", "Export/display band labels");
var availableFixture=new HoldConfidenceSeries([new HoldConfidencePoint(new DateTime(2026,1,1),50,0,0,0,0,0,0,0,0,"fixture")]);
Check(ModelSeriesState.Unavailable(null,"missing")==null,"Unavailable model must not fabricate a replacement series");
var staleFixture=ModelSeriesState.Unavailable(availableFixture,"new snapshot missing");
Check(staleFixture is { IsStale:true,Status:"new snapshot missing" } && staleFixture.LastDate==new DateTime(2026,1,1),
    "Last valid model series must be explicitly stale");
Check(RefreshCyclePolicy.CanStartExport(null) && RefreshCyclePolicy.CanStartExport(Task.CompletedTask),
    "A completed VIX export must not block the next Recalculate");
var pendingExport=new TaskCompletionSource();
Check(!RefreshCyclePolicy.CanStartExport(pendingExport.Task),"A running VIX export must not be duplicated");
var packageDefinitions=Enumerable.Range(1,3)
    .Select(method=>new TradingPackageDefinition("ZZ"+method,"ZZ"+method,"package"+method,
        method,TradingModelEngine.TorchSharp))
    .ToArray();
var packageFixture=TradingPackageCollector.Calculate(packageDefinitions,package=>
{
    if(package.CalculationMethod==2)throw new InvalidOperationException("package absent");
    string name=package.Key;
    return new(name,[new TradingEnsemblePoint(new DateTime(2026,1,2),50,[50,50,50,50,50,50,50,50,50,50])],[],12);
},null,CancellationToken.None);
Check(packageFixture.Series.Count==2 && packageFixture.Statuses.Count==3 &&
      packageFixture.Statuses.Single(s=>s.PackageName=="ZZ2") is { Available:false },
    "One missing ZZ package must not suppress the other packages");
var synthetic=new[]{new FeatureRow(new(2026,1,1),0,0,0,0),new FeatureRow(new(2026,1,2),double.NaN,0,0,0),
    new FeatureRow(new(2026,1,3),0,1,1,0)};
var sequence=Rules2HoldConfidenceCalculator.Calculate(synthetic,1).Points;
Check(sequence[0].TargetPosition==10 && sequence[1].TargetPosition==10 && sequence[2].TargetPosition==0,"Missing score carries previous position");
double[] paneWeights=[.52,.11,.28,.09];
for(int mask=0;mask<16;mask++)
foreach(double available in new[]{170.0,500.0,1000.0})
{
    bool[] collapsed=Enumerable.Range(0,4).Select(i=>(mask&(1<<i))!=0).ToArray();
    double[] heights=TradingPaneLayout.Calculate(available,paneWeights,collapsed);
    Check(heights.All(h=>h>0 && double.IsFinite(h)),"Valid pane heights");
    Check(Math.Abs(heights.Sum()-(mask==15?104:available))<1e-8,"No lost/overflowing height");
    for(int i=0;i<4;i++)if(collapsed[i])Check(heights[i]==26,"Collapsed pane is exactly one header");
    if(available==1000 && mask!=15)
    {
        var open=Enumerable.Range(0,4).Where(i=>!collapsed[i]).ToArray();
        foreach(int i in open)Check(Math.Abs(heights[i]/heights[open[0]]-paneWeights[i]/paneWeights[open[0]])<1e-8,"Expanded panes preserve proportions");
    }
}
string artifacts=Path.Combine(root,"research/artifacts/rules2_dll_requirements_20261029/implementation");
Directory.CreateDirectory(artifacts);
string vixOutput=Path.Combine(artifacts,"vix_runtime_check.csv");
HashSet<DateTime> spyTradingDates = File.ReadLines(@"D:\fear_greed_close\Breeze\SPY.csv")
    .Skip(1).Select(line => DateTime.Parse(line.Split(',', 2)[0]).Date).ToHashSet();
VixTermStructureStore vixStore = VixTermStructureStore.BuildIndex(@"D:\fear_greed_close\VIX", CancellationToken.None);
vixStore.SetTradingCalendar(spyTradingDates);
VixCurveStateSeries vixStates = vixStore.BuildStateSeries(CancellationToken.None);
Rules2VixExport.Export(@"D:\fear_greed_close\VIX", vixOutput, vixStates, CancellationToken.None);
string[] runtimeVix = File.ReadAllLines(vixOutput);
string[] referenceVix = File.ReadAllLines(Path.Combine(artifacts,"vix_reference.csv"));
var referenceByDate = referenceVix.Skip(1).ToDictionary(line => line.Split(',', 2)[0]);
DateTime lastReferenceDate=referenceByDate.Keys.Select(DateTime.Parse).Max();
string[] referenceHeaders = referenceVix[0].Split(',');
int rollFlagColumn = Array.IndexOf(referenceHeaders, "RollFlag");
bool correctedHolidayRoll = false;
Check(runtimeVix.Length == vixStates.Points.Count + 1,
    "The runtime VIX exporter must contain one row per accepted SPY trading date");
for (int index = 1; index < runtimeVix.Length; index++)
{
    string date = runtimeVix[index].Split(',', 2)[0];
    Check(DateTime.TryParse(date, out DateTime parsedDate) && spyTradingDates.Contains(parsedDate.Date),
        $"The runtime VIX exporter included non-SPY date {date}");
    if(!referenceByDate.TryGetValue(date, out string? referenceLine))
    {
        Check(parsedDate.Date>lastReferenceDate,$"Missing historical reference row for {date}");
        continue;
    }
    string[] runtimeFields = runtimeVix[index].Split(',');
    string[] referenceFields = referenceLine!.Split(',');
    Check(runtimeFields.Length == referenceFields.Length + 1, $"Unexpected field count for {date}");
    for (int column = 0; column < referenceFields.Length; column++)
    {
        if (column == rollFlagColumn)
        {
            correctedHolidayRoll |= runtimeFields[column] != referenceFields[column];
            continue;
        }
        Check(runtimeFields[column] == referenceFields[column],
            $"The runtime VIX exporter changed frozen field {referenceHeaders[column]} for {date}");
    }
}
Check(correctedHolidayRoll, "Roll flags were not recalculated across actual SPY sessions");
Check(runtimeVix[0].EndsWith(",AdjustedCurveRegimeScore", StringComparison.Ordinal),
    "The corrected contango field is missing from the Rules2 export");
Console.WriteLine($"RulesSwitch passed: {checks:N0} checks, all 16 pane collapse states; frozen VIX fields preserved and corrected score appended.");
