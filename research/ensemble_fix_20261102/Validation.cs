using System.IO;
using System.Text.Json;
using WealthLab.Core;
using BreezeVL;

internal static partial class Program
{
    static void Require(bool valid,string detail) { if(!valid)throw new Exception(detail); }

    static void ValidatePackageRoute(object engine,BarHistory bars,string key,string root,string dir,string ext,List<Row> rows)
    {
        int method=key.Contains("ZZ2")?2:1;
        string path=Path.Combine(root,dir);
        TradingEnsembleSeries series=ext=="jbin"
            ? (TradingEnsembleSeries)Call(engine,"CalculateJbinPackage",bars,path,key,method,0,CancellationToken.None,true)
            : (TradingEnsembleSeries)Call(engine,ext=="eg"?"CalculateEncogPackage":"CalculateTorchPackage",
                bars,path,key,method,0,CancellationToken.None);
        Require(series.ModelCount==rows.Count && series.Warning=="",key+" missing models: "+series.Warning);
        Require(Math.Abs(series.Last125BenchmarkPercent-rows.Average(r=>r.Sscore))<1e-10,key+" Str% differs from source");
        Require(rows.All(r=>r.S.SequenceEqual(r.D)),key+" per-model classification differs");
        Require(rows.All(r=>r.Bench.Equals(r.Dbench)),key+" Bench parsing differs");
        var sorted=rows.ToList(); sorted.Sort((a,b)=>a.Bench.CompareTo(b.Bench));
        var groups=Enumerable.Range(0,10).Select(_=>new List<Row>()).ToArray();
        for(int i=0;i<sorted.Count;i++) groups[(int)(i*10.0/sorted.Count)].Add(sorted[i]);
        for(int bar=0;bar<bars.Count;bar++)
        {
            var point=series.Points[bar];
            double active=rows.Sum(r=>r.S[bar]);
            Require(point.Date==bars.DateTimes[bar].Date,key+" date mismatch");
            Require(Math.Abs(point.EnsemblePercent-100.0*active/rows.Count)<1e-12,key+" ensemble mismatch");
            for(int group=0;group<10;group++)
                Require(Math.Abs(point.Deciles[group]-groups[group].Sum(r=>r.S[bar])/groups[group].Count*100)<1e-12,
                    $"{key} decile {group} at {bar}");
        }
        File.WriteAllLines(key+"-validated-series.csv",new[]{"Date,EnsemblePercent,D1,D2,D3,D4,D5,D6,D7,D8,D9,D10"}
            .Concat(series.Points.Select(p=>$"{p.Date:yyyy-MM-dd},{p.EnsemblePercent:R},"+string.Join(',',p.Deciles.Select(v=>v.ToString("R"))))));
        Console.WriteLine($"PASS {key}: actual package route, {rows.Count} models, every class/ensemble/decile/date, Str={series.Last125BenchmarkPercent:R}");
    }

    static void CheckGpu(BarHistory bars)
    {
        var oldEngine=new GpuBaselineInference();
        var newEngine=new TradingEnsembleInference();
        var result=new List<object>();
        for(int method=1;method<=3;method++)
        {
            string key="GPU-ZZ"+method;
            string path=Path.Combine(@"D:\TorchSharp4GPU","trading_package_Jordan_ZZ"+method);
            var before=(TradingEnsembleSeries)Call(oldEngine,"CalculateJbinPackage",bars,path,key,method,1,CancellationToken.None);
            var after=(TradingEnsembleSeries)Call(newEngine,"CalculateJbinPackage",bars,path,key,method,1,CancellationToken.None,false);
            Require(before.ModelCount==after.ModelCount && before.Warning==after.Warning,key+" package changed");
            Require(before.Last125BenchmarkPercent.Equals(after.Last125BenchmarkPercent),key+" Str changed");
            Require(before.Points.Count==after.Points.Count,key+" length changed");
            for(int i=0;i<before.Points.Count;i++)
                Require(before.Points[i].Date==after.Points[i].Date && before.Points[i].EnsemblePercent.Equals(after.Points[i].EnsemblePercent)
                    && before.Points[i].Deciles.SequenceEqual(after.Points[i].Deciles),key+" historical output changed");
            Require(before.Forecasts.Count==after.Forecasts.Count,key+" forecast count changed");
            for(int i=0;i<before.Forecasts.Count;i++)
                Require(before.Forecasts[i].Date==after.Forecasts[i].Date && before.Forecasts[i].ScenarioPercentages.SequenceEqual(after.Forecasts[i].ScenarioPercentages),key+" forecast changed");
            File.WriteAllLines(key+"-validated-series.csv",
                new[]{"Date,EnsemblePercent,D1,D2,D3,D4,D5,D6,D7,D8,D9,D10"}
                    .Concat(after.Points.Select(p=>$"{p.Date:yyyy-MM-dd},{p.EnsemblePercent:R},"+
                        string.Join(',',p.Deciles.Select(v=>v.ToString("R"))))));
            result.Add(new {key,after.ModelCount,after.Last125BenchmarkPercent,HistoricalBars=after.Points.Count,Forecasts=after.Forecasts.Count,Identical=true});
            Console.WriteLine($"PASS {key}: history, deciles, Str% and forecast scenarios unchanged ({after.ModelCount} models)");
        }
        File.WriteAllText("gpu-regression.json",JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
    }
}
