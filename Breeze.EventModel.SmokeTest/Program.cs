using System.Text.Json;
using BreezeHoldPosition.Strategies;
using M = BreezeHoldPosition.Strategies.BreezeEventModelNew;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/event-model-new-validation");
Directory.CreateDirectory(output);
int checks = 0;
void Check(bool valid, string name) { checks++; if (!valid) throw new Exception("FAILED: " + name); }
void Equal(double a, double b, string name) => Check((double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a-b) <= 1e-10, name);
var rows = M.Load(@"D:\fear_greed_close\Breeze", @"D:\fear_greed_close\vix_term_structure.csv");
M.Build(rows);
var weights = M.Features.Select(f => f.Weight).ToArray();
var researchWeights = M.Features.Select(f => M.ResearchWeight(f.Id)).ToArray();
var searchWeights = M.SearchIndexes.Select(i=>M.ResearchWeight(M.Features[i].Id)).ToArray();
var scores = M.Score(rows, weights, 50, 1);
var researchScores = M.Score(rows, researchWeights, 50, 1);
Check(M.Features.Length == 74, "74 market cards");
Check(M.Features.Select(x => x.Id).Distinct().Count() == 74, "unique IDs");
Check(M.SearchIndexes.Length == 35, "35 non-zero jointly optimized weights");
Check(M.SearchIndexes.All(i=>M.ResearchWeight(M.Features[i].Id)!=0), "joint search contains only non-zero balanced weights");
Check(M.Features.Where(f=>!M.IsSearch(f.Id)).All(f=>M.ResearchWeight(f.Id)==0), "all zero weights remain fixed outside optimizer");
var softWeights=M.PresetWeights(0); var standardWeights=M.PresetWeights(1); var strongWeights=M.PresetWeights(2);
Check(new[]{softWeights,standardWeights,strongWeights}.All(x=>x.Length==M.Features.Length),"three complete preset vectors");
Check(M.PresetName(0)=="Soft"&&M.PresetName(1)=="Standard"&&M.PresetName(2)=="Strong","preset names");
Check(standardWeights[Array.FindIndex(M.Features,f=>f.Id=="E02")]==5,"Standard is the selected 251.19% run");
Check(strongWeights[Array.FindIndex(M.Features,f=>f.Id=="E02")]==10,"Strong is the 251.51% run");
Check(softWeights[Array.FindIndex(M.Features,f=>f.Id=="C01")]==15&&softWeights[Array.FindIndex(M.Features,f=>f.Id=="V03")]==0,"Soft is the minimum-drawdown candidate");
Check(new[]{softWeights,standardWeights,strongWeights}.All(w=>M.Features.Select((f,i)=>(f,i)).Where(x=>!M.IsSearch(x.f.Id)).All(x=>w[x.i]==0)),"zero-weight catalog features stay inactive in every preset");
double ProxyProfit(double[] profile)
{
    double cash=3000; int held=0, next=0;
    foreach(var r in rows.Where(r=>r.Date>=new DateTime(2017,10,12)&&r.Date<=new DateTime(2026,10,9)))
    {
        cash-=(next-held)*r.Open; held=next;
        if(r.Ready) next=M.TradingContracts(M.ScoreRow(r,profile,50),1,50,10);
    }
    var last=rows.Last(r=>r.Date<=new DateTime(2026,10,9));
    return cash+held*last.Close-3000;
}
double standardProxyProfit=ProxyProfit(standardWeights), strongProxyProfit=ProxyProfit(strongWeights);
Check(Math.Abs(standardProxyProfit-7541.0)<0.02,"Standard gross next-open proxy reproduces supplied run before Wealth-Lab costs");
Check(strongProxyProfit>standardProxyProfit,"Strong proxy exceeds Standard profit");
Check(rows.Any(x => x.Ready), "historical usable rows");
var pickup=rows.Single(r=>r.Date==new DateTime(2024,9,11));
Check(pickup.Get("C06")==1 && pickup.Get("B04")==1,"2024-09-11 deep pickup, four breadth rises");
Check(Math.Abs(pickup.Atr-2.77767757)<1e-7,"2024-09-11 ATRP1 numerical reference");
var breakdown=rows.Single(r=>r.Date==new DateTime(2025,2,21));
Check(breakdown.Get("C08")==1 && breakdown.Get("R08")==1,"2025-02-21 joint RSI release and red EMA20 break");
Check(Math.Abs(breakdown.R[0]-0.68831035)<1e-7 && Math.Abs(breakdown.R[1]-0.63935204)<1e-7,"2025-02-21 MyRSI reference");
// Hand-calculated score: 50 + T01(10)*1 + V01(10)*(-1) + C08(-10)*1 = 40.
var hand=new M.Row { Ready=true };
foreach(var f in M.Features)hand.Values[f.Id]=0;
hand.Values["T01"]=1; hand.Values["V01"]=-1; hand.Values["C08"]=1;
Equal(M.Score(new[]{hand},weights,50,1)[0],40,"independent hand score");
var catalog = JsonDocument.Parse(File.ReadAllText("docs/event-catalog-v1/catalog_source.json"));
var ids = catalog.RootElement.GetProperty("events").EnumerateArray().Where(x=>x[2].GetString()!="execution").Select(x=>x[0].GetString()).ToHashSet();
Check(ids.SetEquals(M.Features.Select(f=>f.Id)), "catalog coverage, excluding execution X01-03");
foreach (int size in new[] { 1, 10, 20 })
{
    Check(M.Contracts(0,size)==0 && M.Contracts(100,size)==size,"endpoint quantities");
    for (int p=0;p<=100;p++) Check(M.Contracts(p,size)>=0 && M.Contracts(p,size)<=size,"quantity bounds");
}
Check(M.Contracts(49,10)==5 && M.Contracts(90,10)==9,"rounding not old 90=>10");
Check(M.TradingContracts(49,0,50)==5 && M.TradingContracts(50,0,50)==5,"gradual engine");
Check(M.TradingContracts(49,1,50)==0 && M.TradingContracts(50,1,50)==10,"level engine boundary");
var flat = M.Score(rows, new double[74], 37, 1);
foreach (int i in Enumerable.Range(0,rows.Count).Where(i=>rows[i].Ready))
{
    Equal(flat[i],37,"zero weights => bias");
    Check(scores[i]>=0 && scores[i]<=100,"score bounds");
    Equal(M.ScoreRow(rows[i],weights,50),scores[i],"Execute-cycle score equals vector score");
    Equal(M.ScoreRowFast(rows[i],searchWeights,50),researchScores[i],"35-weight fast Execute score equals research full score");
    foreach (var f in M.Features) Check(double.IsFinite(rows[i].Get(f.Id)),"all features finite");
}
// Append-future invariance: recompute from independently loaded prefixes.
foreach (int length in new[] { 21, 30, 50, 100, 200, 250, 270, 650, 1250, rows.Count-30 })
{
    var prefix = M.Load(@"D:\fear_greed_close\Breeze", @"D:\fear_greed_close\vix_term_structure.csv").Take(length).ToList();
    M.Build(prefix);
    var ps = M.Score(prefix, weights, 50, 0.6);
    var fs = M.Score(rows,weights,50,0.6);
    for (int i=0;i<length;i++)
    {
        foreach(var f in M.Features)Equal(prefix[i].Get(f.Id),rows[i].Get(f.Id),"prefix "+f.Id);
        Equal(ps[i],fs[i],"prefix score");
    }
}
// Corrupted/missing input never becomes a false all-clear, and never creates orders.
var gap = M.Load(@"D:\fear_greed_close\Breeze", @"D:\fear_greed_close\vix_term_structure.csv");
gap[1000].Complete=false; M.Build(gap);
var gs=M.Score(gap,weights,50,1);
Check(double.IsNaN(gs[1000]) && double.IsNaN(gs[1020]),"missing data and context unknown");
for(int i=0;i<1000;i++)Equal(gs[i],scores[i],"gap cannot change past");
// The old Hold curve is display-only, never part of the score.
foreach(var row in gap)row.OldHold=999;
var unaffected=M.Score(gap,weights,50,1);
for(int i=0;i<gap.Count;i++)Equal(gs[i],unaffected[i],"old hold excluded");
var active=rows.Where(r=>r.Ready).ToArray();
var report=new {
    version="0.10.0-alpha.13", status="frozen_profiles_and_factor_stress", checks,
    rows=rows.Count, usable=active.Length, start=rows[0].Date, end=rows[^1].Date,
    usableStart=active[0].Date, unavailable=rows.Count-active.Length,
    scoreMin=scores.Where(double.IsFinite).Min(), scoreMax=scores.Where(double.IsFinite).Max(),
    notValidated="No Wealth-Lab backtest or economic out-of-sample validation. Formalized variants need visual review.",
    coverage=M.Features.Select(f=>new {f.Id,f.Name,f.Weight,Search=M.IsSearch(f.Id),f.Rule,
        positive=active.Count(r=>r.Get(f.Id)>0),negative=active.Count(r=>r.Get(f.Id)<0),
        first=active.FirstOrDefault(r=>r.Get(f.Id)!=0)?.Date}),
    examples=rows.Where(r=>new[]{new DateTime(2019,12,4),new DateTime(2024,9,11),new DateTime(2025,2,21)}.Contains(r.Date)).Select(r=>new {
        r.Date,r.Close,r.Open,r.Ready,RSI=r.R,EMA=r.E,r.Atr,r.Front,
        features=r.Values.ToDictionary(x=>x.Key,x=>double.IsFinite(x.Value)?(double?)x.Value:null)})
};
File.WriteAllText(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
var description=new System.Text.StringBuilder("# Параметры признаков Breeze Hold Position_new\n\n");
description.AppendLine("Версия 0.10.0-alpha.13. В стратегии заморожены три профиля Soft, Standard и Strong, выбранные из пяти выборочных прогонов. Standard — указанная пользователем кривая 251.19%, Strong — максимум доходности 251.51%, Soft — кандидат с минимальной просадкой 9.16%. Все 74 признака продолжают ежедневно рассчитываться. Отдельный режим выполняет однофакторный стресс-тест ненулевых весов Strong. Признак нормирован в 0..1 или -1..1; вес измеряется в процентных пунктах целевой позиции.\n");
description.AppendLine("| ID | Признак | Вес | Min | Max | Step | Ненулевых дат после прогрева | Реализованное правило |\n|---|---|---:|---:|---:|---:|---:|---|");
foreach(var f in M.Features)
{
    double current=M.ResearchWeight(f.Id), lo=current, hi=current, step=0;
    if(M.IsSearch(f.Id)){var range=M.ResearchRange(f.Id);lo=range.Min;hi=range.Max;step=range.Step;}
    description.AppendLine($"| {f.Id} | {f.Name} | {current} | {lo} | {hi} | {step} | {active.Count(r=>r.Get(f.Id)!=0)} | {f.Rule} |");
}
description.AppendLine("\nX01–X03 описывают альтернативные лимитные исполнения и намеренно не являются рыночными весами. В первой стратегии исполнение Market на следующем открытии. U01 не сработал на текущей истории при выбранных допусках; вес 0, оптимизация этого признака пока не имеет эмпирического основания.\n");
File.WriteAllText(Path.Combine(output,"FEATURES.md"),description.ToString());
// JSONL is a research matrix, not a training label or an executable trading signal file.
using(var writer=new StreamWriter(Path.Combine(output,"daily_features.jsonl")))
for(int i=0;i<rows.Count;i++)writer.WriteLine(JsonSerializer.Serialize(new {
    date=rows[i].Date.ToString("yyyy-MM-dd"),ready=rows[i].Ready,
    initialTarget=double.IsFinite(scores[i])?(double?)scores[i]:null,
    features=rows[i].Values.ToDictionary(x=>x.Key,x=>double.IsFinite(x.Value)?(double?)x.Value:null),
    details=rows[i].Details.ToDictionary(x=>x.Key,x=>double.IsFinite(x.Value)?(double?)x.Value:null)
}));
Console.WriteLine($"PASS: {checks} checks; {active.Length}/{rows.Count} usable rows; 74 features; Soft/Standard/Strong alpha.13 presets validated. Report: {output}");
