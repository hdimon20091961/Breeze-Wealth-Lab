using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using WealthLab.Core;
using WealthScript6;

class Verify
{
    static void Main()
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        AssemblyLoadContext.Default.Resolving+=(_,name)=> { var p=Path.Combine(@"C:\Program Files\Quantacula, LLC\WealthLab 8",name.Name+".dll");return File.Exists(p)?AssemblyLoadContext.Default.LoadFromAssemblyPath(p):null; };
        Run();
    }
    static void Run()
    {
        string root=@"D:\CODEX_PRG\Breadth\research\artifacts\disbalance_rules_20261030";
        var bars=new BarHistory("SPY",HistoryScale.Daily);
        foreach(var c in File.ReadLines(Path.Combine(root,"data/prices.csv")).Skip(1).Select(l=>l.Split(',')))
            bars.Add(DateTime.Parse(c[0]),double.Parse(c[1]),double.Parse(c[2]),double.Parse(c[3]),double.Parse(c[4]),double.Parse(c[5]));
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        void Set(object o,string name,object v)=>o.GetType().GetField(name,flags).SetValue(o,v);
        object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,flags).Invoke(o,args);
        TimeSeries Load(string path,int column)
        {
            var map=new Dictionary<DateTime,double>();
            foreach(string l in File.ReadLines(path).Skip(1))
            {var c=l.Split(',');if(c.Length>column && DateTime.TryParseExact(c[0],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var d)&&double.TryParse(c[column],out double v))map[d]=v;}
            var ts=new TimeSeries(bars.DateTimes,double.NaN);
            for(int i=0;i<bars.Count;i++)if(map.TryGetValue(bars.DateTimes[i],out double v))ts[i]=v;
            return ts;
        }
        var r1=Load(Path.Combine(root,"data/rules1_score.csv"),1);
        var r2=Load(Path.Combine(root,"data/rules2_features.csv"),1);
        var expected=File.ReadLines(Path.Combine(root,"daily_paths.csv")).Skip(1).Select(l=>l.Split(','))
            .ToDictionary(c=>c[1]+"|"+c[2]+"|"+c[3]+"|"+c[0],c=>int.Parse(c[4]));
        foreach(var c in File.ReadLines(Path.Combine(root,"ema50_handoff/paths.csv")).Skip(1).Select(l=>l.Split(',')))
            expected[c[1]+"|"+c[2]+"|"+c[3]+"|"+c[0]]=int.Parse(c[4]);
        string[] variants={"baseline","r1_delegate","r2_delegate","latch_breadth_velocity_2","latch_ema50_2","regulate_r1","immediate","recovery"};
        int checks=0;
        for(int package=1;package<=3;package++)
        {
            var dec=Enumerable.Range(0,10).Select(g=>Load(Path.Combine(root,$"data/deciles_ZZ{package}.csv"),g+2)).ToArray();
            for(int tolerance=0;tolerance<=5;tolerance++)for(int mode=0;mode<=7;mode++)
            {
                if(tolerance>2 && mode<6)continue;
                var obj=new TorchSharpTradingRulesFuturesStrategy();
                Set(obj,"decileSeries",dec);Set(obj,"decileThreshold",50);Set(obj,"disbalanceLevel",tolerance);
                Set(obj,"rules1ScoreSeries",r1);Set(obj,"rules2ScoreSeries",r2);Set(obj,"controlEnabled",1);Set(obj,"controlScheme",mode);
                Call(obj,"PreparePositionControl",bars);
                int qty=0;
                for(int i=175;i<bars.Count;i++)
                {
                    int neural=dec.Count(ts=>ts[i]>=50);
                    qty=(int)Call(obj,"ApplyPositionControl",bars,i,qty,neural);
                    int reference=expected[$"ZZ{package}|{tolerance}|{variants[mode]}|{bars.DateTimes[i]:yyyy-MM-dd}"];
                    if(qty!=reference)throw new Exception($"Mismatch ZZ{package} tolerance={tolerance} mode={mode} date={bars.DateTimes[i]:yyyy-MM-dd}: {qty}/{reference}");
                    checks++;
                }
                Set(obj,"controlEnabled",0);
                for(int i=175;i<bars.Count;i++)
                {int neural=dec.Count(ts=>ts[i]>=50);if((int)Call(obj,"ApplyPositionControl",bars,i,5,neural)!=neural)throw new Exception("Disabled mode changed target");checks++;}
            }
        }
        Console.WriteLine($"Position-control replay passed: {checks:N0} decisions against research and disabled-mode checks.");
    }
}
