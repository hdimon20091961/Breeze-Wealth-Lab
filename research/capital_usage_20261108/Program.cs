using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using WealthLab.Core;

internal static class Program
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static object Call(object target, string method, params object[] args)
    {
        Type type = target is Type t ? t : target.GetType();
        try { return type.GetMethod(method, Flags)!.Invoke(target is Type ? null : target, args)!; }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }

    static object Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target)!;
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);

    static void Main()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(AuditHistory.Install, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        SetDllDirectory(Path.Combine(AuditHistory.Install, "runtimes", "win-x64", "native"));
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var bars = new BarHistory("SPY", HistoryScale.Daily);
        foreach (string line in File.ReadLines(@"D:\fear_greed_close\Breeze\SPY.csv").Skip(1))
        {
            string[] cells = line.Split(',');
            DateTime date = DateTime.Parse(cells[0], CultureInfo.InvariantCulture);
            if (date < new DateTime(2017, 10, 31) || date > new DateTime(2026, 10, 30)) continue;
            double[] values = cells.Skip(1).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            bars.Add(date, values[0], values[1], values[2], values[3], values[4]);
        }

        const int firstExecutionIndex = 176;
        const double chandelierPercent = 3.75;
        const int reentryDelay = 1;
        var strategy = new WealthScript1.MyStrategy2779();
        object cache = Call(strategy, "PrecomputeBaseIndicators", bars);
        object[] mapArgs = [bars, cache, 0];
        var featureMap = (Dictionary<string, TimeSeries>)Call(strategy, "BuildFeatureMapFromCache", mapArgs);
        int modelStart = (int)mapArgs[2];

        var packages = new[]
        {
            (Name: "ZZ1", Root: @"D:\TorchSharp", Dir: "trading_package", Ext: "pt"),
            (Name: "ZZ2", Root: @"D:\TorchSharp", Dir: "trading_package_zz2", Ext: "pt"),
            (Name: "ZZ1-J", Root: @"D:\TorchSharp", Dir: "trading_package_J", Ext: "jbin"),
            (Name: "ZZ2-J", Root: @"D:\TorchSharp", Dir: "trading_package_zz2_J", Ext: "jbin"),
            (Name: "Encog-ZZ1", Root: @"D:\model_ensemble_Encog", Dir: "trading_package", Ext: "eg"),
            (Name: "Encog-ZZ2", Root: @"D:\model_ensemble_Encog", Dir: "trading_package_zz2", Ext: "eg")
        };

        var results = new List<object>();
        foreach (var package in packages)
        {
            Console.WriteLine($"PACKAGE {package.Name}");
            string[] files = Directory.GetFiles(Path.Combine(package.Root, package.Dir), "*." + package.Ext);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            var models = new List<Model>();
            foreach (string file in files)
            {
                string ini = Path.ChangeExtension(file, ".ini");
                TimeSeries raw;
                double level;
                if (package.Ext == "eg")
                {
                    object parameters = Call(strategy, "LoadEncogParameters", ini);
                    object[] args = [bars, cache, parameters, 0];
                    object map = Call(strategy, "BuildEncogFeatureMap", args);
                    raw = (TimeSeries)Call(strategy, "RunEncogInMemory", file, bars, map, (int)args[3], Field(parameters, "Features"));
                    level = (double)Field(parameters, "LevelClass");
                }
                else
                {
                    Set(strategy, "ActiveFeatureNames", featureMap.Keys.ToList());
                    object parameters = Call(strategy, "LoadIndividualModelParameters", ini, Path.GetFileName(file));
                    raw = (TimeSeries)Call(strategy, package.Ext == "pt" ? "RunTorchSharpInMemory" : "RunJordanJbinInMemory", file, bars, featureMap, modelStart, parameters);
                    level = (double)Field(parameters, "level_class");
                }

                var match = Regex.Match(Path.GetFileName(file), @"Bench=?(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                double bench = match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
                double[] binary = Enumerable.Range(0, bars.Count).Select(i => raw[i] >= level ? 1.0 : 0.0).ToArray();
                models.Add(new Model(bench, binary));
                if (models.Count % 200 == 0) Console.WriteLine($"  {models.Count}/{files.Length}");
            }

            models.Sort((a, b) => a.Bench.CompareTo(b.Bench));
            int[] groupSizes = new int[10];
            double[] total = new double[bars.Count];
            double[][] deciles = Enumerable.Range(0, 10).Select(_ => new double[bars.Count]).ToArray();
            for (int i = 0; i < models.Count; i++)
            {
                int group = (int)(i * 10.0 / models.Count);
                groupSizes[group]++;
                for (int bar = 0; bar < bars.Count; bar++)
                {
                    total[bar] += models[i].Binary[bar];
                    deciles[group][bar] += models[i].Binary[bar];
                }
            }

            int[] proportional = Enumerable.Range(0, bars.Count)
                .Select(i => (int)Math.Round(total[i] / models.Count * 10.0, MidpointRounding.AwayFromZero)).ToArray();
            int[] independentDeciles = Enumerable.Range(0, bars.Count)
                .Select(i => Enumerable.Range(0, 10).Count(g => deciles[g][i] / groupSizes[g] * 100.0 >= 50.0)).ToArray();
            int[] allOrNothing = Enumerable.Range(0, bars.Count)
                .Select(i => total[i] / models.Count * 100.0 >= 50.0 ? 10 : 0).ToArray();

            var row = new
            {
                Package = package.Name,
                Models = models.Count,
                Bars = bars.Count - firstExecutionIndex,
                Proportional = Simulate(bars, proportional, firstExecutionIndex, chandelierPercent, reentryDelay),
                Deciles = Simulate(bars, independentDeciles, firstExecutionIndex, chandelierPercent, reentryDelay),
                AllOrNothing = Simulate(bars, allOrNothing, firstExecutionIndex, chandelierPercent, reentryDelay),
                DesiredLotBars = new
                {
                    Proportional = proportional.Skip(firstExecutionIndex).Sum(),
                    Deciles = independentDeciles.Skip(firstExecutionIndex).Sum(),
                    AllOrNothing = allOrNothing.Skip(firstExecutionIndex).Sum()
                }
            };
            results.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }

        File.WriteAllText("capital-usage.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    static object Simulate(BarHistory bars, int[] desired, int start, double chandelierPercent, int reentryDelay)
    {
        var positions = new List<Lot>();
        int? pendingTarget = null;
        int lastStopBar = int.MinValue;
        double lotBars = 0;
        for (int bar = start; bar < bars.Count; bar++)
        {
            if (pendingTarget.HasValue)
            {
                int target = Math.Clamp(pendingTarget.Value, 0, 10);
                while (positions.Count < target) positions.Add(new Lot(bar) { HighestHigh = bars.High[bar] });
                while (positions.Count > target) positions.RemoveAt(0);
                pendingTarget = null;
            }

            bool stopExecuted = false;
            foreach (Lot lot in positions.ToArray())
            {
                if (!lot.HasStop) continue;
                if (bars.Open[bar] <= lot.Stop || bars.Low[bar] <= lot.Stop)
                {
                    positions.Remove(lot);
                    stopExecuted = true;
                }
            }
            if (stopExecuted) lastStopBar = bar;

            int actual = positions.Count;
            lotBars += actual;
            bool reentryBlocked = desired[bar] > actual && lastStopBar != int.MinValue && bar - lastStopBar < reentryDelay;
            pendingTarget = reentryBlocked ? actual : desired[bar];

            bool placeStops = actual <= desired[bar];
            foreach (Lot lot in positions)
            {
                lot.HighestHigh = Math.Max(lot.HighestHigh, bars.High[bar]);
                if (placeStops)
                {
                    lot.Stop = lot.HighestHigh * (1.0 - chandelierPercent / 100.0);
                    lot.HasStop = true;
                }
                else lot.HasStop = false;
            }
        }

        double maximum = (bars.Count - start) * 10.0;
        return new { LotBars = lotBars, Percent = lotBars / maximum * 100.0 };
    }

    sealed record Model(double Bench, double[] Binary);
    sealed class Lot
    {
        public Lot(int entryBar) { EntryBar = entryBar; HighestHigh = double.NegativeInfinity; }
        public int EntryBar { get; }
        public double HighestHigh { get; set; }
        public bool HasStop { get; set; }
        public double Stop { get; set; }
    }
}
