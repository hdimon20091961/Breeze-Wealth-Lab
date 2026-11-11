using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using WealthLab.Core;

internal static partial class Program
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object Call(object target, string method, params object[] args)
    {
        Type t = target is Type type ? type : target.GetType();
        try { return t.GetMethod(method, Flags)!.Invoke(target is Type ? null : target, args)!; }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }
    static object Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target)!;
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    static object Prop(object target, string name) => target.GetType().GetProperty(name, Flags)!.GetValue(target)!;

    static void Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, n) => {
            string p = Path.Combine(AuditHistory.Install, n.Name + ".dll");
            return File.Exists(p) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(p) : null;
        };
        SetDllDirectory(Path.Combine(AuditHistory.Install, "runtimes", "win-x64", "native"));
        Run(args);
    }
    static void Run(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var bars = new BarHistory("SPY", HistoryScale.Daily);
        DateTime first = args.Contains("short") ? new(2017,10,31) : DateTime.MinValue;
        foreach (string line in File.ReadLines(@"D:\fear_greed_close\Breeze\SPY.csv").Skip(1))
        {
            var c = line.Split(','); var d = DateTime.Parse(c[0], CultureInfo.InvariantCulture);
            if (d < first) continue;
            var v = c.Skip(1).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            bars.Add(d, v[0], v[1], v[2], v[3], v[4]);
        }
        Console.WriteLine($"BARS {bars.Count} {bars.DateTimes[0]:yyyy-MM-dd}..{bars.DateTimes[^1]:yyyy-MM-dd}; Bench125={bars.Close[^1]-bars.Close[bars.Count-126]:F8}");
        if (args.Contains("gpu")) { CheckGpu(bars); return; }
        var source = new WealthScript1.MyStrategy2779();
        object cache = Call(source, "PrecomputeBaseIndicators", bars);
        object[] mapArgs = [bars, cache, 0];
        var sourceMap = (Dictionary<string,TimeSeries>)Call(source, "BuildFeatureMapFromCache", mapArgs);
        int start = (int)mapArgs[2];
        var engine = new BreezeVL.TradingEnsembleInference();
        Type dllType = engine.GetType();
        var specs = new[] {
            (Key:"Encog-ZZ1",Root:@"D:\model_ensemble_Encog",Dir:"trading_package",Ext:"eg"),
            (Key:"Encog-ZZ2",Root:@"D:\model_ensemble_Encog",Dir:"trading_package_zz2",Ext:"eg"),
            (Key:"Torch-ZZ1",Root:@"D:\TorchSharp",Dir:"trading_package",Ext:"pt"),
            (Key:"Torch-ZZ2",Root:@"D:\TorchSharp",Dir:"trading_package_zz2",Ext:"pt"),
            (Key:"Torch-ZZ1-J",Root:@"D:\TorchSharp",Dir:"trading_package_J",Ext:"jbin"),
            (Key:"Torch-ZZ2-J",Root:@"D:\TorchSharp",Dir:"trading_package_zz2_J",Ext:"jbin") };
        var summaries = new List<object>();
        foreach (var spec in specs.Where(s => args.Length == 0 || args.Contains("all") || args.Contains(s.Key)))
        {
            Console.WriteLine("PACKAGE " + spec.Key);
            var files = Directory.GetFiles(Path.Combine(spec.Root,spec.Dir),"*."+spec.Ext).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
            var rows = new List<Row>();
            Dictionary<string,object> featureDiff = new();
            foreach (string file in files)
            {
                string ini = Path.ChangeExtension(file,".ini");
                object dp = Call(dllType,"LoadLegacyParameters",ini,Path.Combine(spec.Root,"system_param.ini"),spec.Ext=="eg");
                object df = Call(engine,"GetFeatures",bars,dp);
                var dmap = (Dictionary<string,TimeSeries>)Prop(df,"Map");
                if (featureDiff.Count == 0)
                    foreach (var p in sourceMap)
                    {
                        var differences = Enumerable.Range(Math.Max(start,(int)Prop(df,"StartIndex")), bars.Count-Math.Max(start,(int)Prop(df,"StartIndex")))
                            .Select(i=>Math.Abs(p.Value[i]-dmap[p.Key][i])).Where(double.IsFinite).ToArray();
                        featureDiff[p.Key] = new {Max=differences.Max(),Count=differences.Count(x=>x>1e-12),SourceLast=p.Value[^1],DllLast=dmap[p.Key][^1]};
                    }
                TimeSeries raw;
                double level;
                if (spec.Ext=="eg")
                {
                    object sp = Call(source,"LoadEncogParameters",ini);
                    object[] a = [bars,cache,sp,0];
                    var smap = Call(source,"BuildEncogFeatureMap",a);
                    raw = (TimeSeries)Call(source,"RunEncogInMemory",file,bars,smap,(int)a[3],Field(sp,"Features"));
                    level=(double)Field(sp,"LevelClass");
                }
                else
                {
                    Set(source,"ActiveFeatureNames",sourceMap.Keys.ToList());
                    object sp=Call(source,"LoadIndividualModelParameters",ini,Path.GetFileName(file));
                    raw=(TimeSeries)Call(source,spec.Ext=="pt"?"RunTorchSharpInMemory":"RunJordanJbinInMemory",file,bars,sourceMap,start,sp);
                    level=(double)Field(sp,"level_class");
                }
                var sv=Enumerable.Range(0,bars.Count).Select(i=>raw[i]>=level?1.0:0.0).ToArray();
                string runner=spec.Ext switch {"eg"=>"RunEncogModel","pt"=>"RunTorchModel",_=>"RunModel"};
                double[] dv=(double[])Call(dllType,runner,file,bars.Count,df,dp,null,bars.Count,null,CancellationToken.None);
                // Counterfactual: keep the DLL model loader, supply the exact
                // source features. This isolates preprocessing from inference.
                object cf=Activator.CreateInstance(df.GetType(),Flags,null,new object[]{sourceMap,start},CultureInfo.InvariantCulture)!;
                double[] cv=(double[])Call(dllType,runner,file,bars.Count,cf,dp,null,bars.Count,null,CancellationToken.None);
                var match=Regex.Match(Path.GetFileName(file),@"Bench=?(-?\d+(?:\.\d+)?)",RegexOptions.IgnoreCase);
                double bench=match.Success?double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture):double.NaN;
                double dllBench=(double)Call(dllType,"ParseBench",Path.GetFileName(file));
                rows.Add(new Row(Path.GetFileName(file),bench,dllBench,sv,dv,raw[^1],Score(bars,sv),Score(bars,dv),Score(bars,cv),Enumerable.Range(0,bars.Count).Count(i=>sv[i]!=cv[i])));
                if(rows.Count%200==0) Console.WriteLine($"{spec.Key} {rows.Count}/{files.Length}");
            }
            ValidatePackageRoute(engine,bars,spec.Key,spec.Root,spec.Dir,spec.Ext,rows);
            int differing125=rows.Sum(r=>Enumerable.Range(bars.Count-126,125).Count(i=>r.S[i]!=r.D[i]));
            var summary=new {spec.Key,Count=rows.Count,SourceMeanForecast=rows.Average(r=>r.Raw),SourceStr=rows.Average(r=>r.Sscore),DllStr=rows.Average(r=>r.Dscore),
                DifferentBinary125=differing125,CompatibleDllStr=rows.Average(r=>r.Cscore),CompatibleDifferentBinaryAll=rows.Sum(r=>r.Cdiff),DllMissingBench=rows.Count(r=>double.IsNaN(r.Dbench)),FeatureDiff=featureDiff,
                SourceProfit=Profits(bars,rows,false),DllProfit=Profits(bars,rows,true)};
            summaries.Add(summary);
            Console.WriteLine(JsonSerializer.Serialize(summary,new JsonSerializerOptions{NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
            File.WriteAllText($"result-{(args.Contains("short")?"short":"long")}.json",JsonSerializer.Serialize(summaries,new JsonSerializerOptions{WriteIndented=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
            File.WriteAllLines(spec.Key+"-models.csv",new[]{"File,SourceBench,DllBench,SourceLastRaw,SourceStr,DllStr,Different125,CompatibleStr,CompatibleDifferentAll"}.Concat(rows.Select(r=>$"\"{r.Name}\",{r.Bench:R},{r.Dbench:R},{r.Raw:R},{r.Sscore:R},{r.Dscore:R},{Enumerable.Range(bars.Count-126,125).Count(i=>r.S[i]!=r.D[i])},{r.Cscore:R},{r.Cdiff}")));
        }
    }
    record Row(string Name,double Bench,double Dbench,double[] S,double[] D,double Raw,double Sscore,double Dscore,double Cscore,int Cdiff);
    static double Score(BarHistory b,double[] v)
    {
        double sum=0; int last=b.Count-1;
        for(int i=last-124;i<=last;i++) if(v[i-1]>=.5) sum+=b.Close[i]-b.Close[i-1];
        return sum/(b.Close[last]-b.Close[last-125])*100;
    }
    static object Profits(BarHistory b,List<Row> rows,bool dll)
    {
        var sorted=rows.ToList();
        sorted.Sort((a,c)=>(dll?a.Dbench:a.Bench).CompareTo(dll?c.Dbench:c.Bench));
        var totals=new double[b.Count]; var dec=Enumerable.Range(0,10).Select(_=>new double[b.Count]).ToArray(); int[] sizes=new int[10];
        for(int i=0;i<sorted.Count;i++) { int g=(int)(i*10.0/sorted.Count); sizes[g]++; var vals=dll?sorted[i].D:sorted[i].S;
            for(int j=0;j<b.Count;j++){totals[j]+=vals[j];dec[g][j]+=vals[j];}}
        object Profit(Func<int,int> qty) {
            double cash=0; int held=0, units=0; DateTime? firstTrade=null;
            for(int i=0;i<b.Count-1;i++) {if(b.DateTimes[i]<new DateTime(2018,7,15))continue; int desired=qty(i);int delta=desired-held;
                if(delta!=0){units+=Math.Abs(delta);firstTrade??=b.DateTimes[i+1];}cash-=delta*b.Open[i+1];held=desired;}
            return new {GrossPercent=(cash+held*b.Close[^1])/3000*100,ExecutedUnits=units,FirstTrade=firstTrade,FinalQuantity=held};
        }
        return new {Proportional=Profit(i=>(int)Math.Round(totals[i]/sorted.Count*10,MidpointRounding.AwayFromZero)),
            Deciles=Profit(i=>Enumerable.Range(0,10).Count(g=>dec[g][i]/sizes[g]*100>=50)),AllOrNothing=Profit(i=>totals[i]/sorted.Count>=.5?10:0)};
    }
}
