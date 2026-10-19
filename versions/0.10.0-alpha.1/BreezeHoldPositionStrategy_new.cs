// Breeze event research 0.10.0-alpha.1. Paste THIS WHOLE FILE into WL8.
// Standalone: no reference to Breeze.dll, no Phase/Hold scores as model inputs.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
#if !EVENT_MODEL_TEST
using WealthLab.Backtest;
using WealthLab.Core;
#endif

namespace BreezeHoldPosition.Strategies;

#if !EVENT_MODEL_TEST
public sealed class BreezeHoldPositionStrategy_new : UserStrategyBase
{
    public const string DefaultDataDirectory = @"D:\fear_greed_close\Breeze";
    public const string CurvePath = @"D:\fear_greed_close\vix_term_structure.csv";
    public override string StrategyName => "Breeze Hold Position_new";
    public override string Description => "0.10.0-alpha.1: independent event scoring, target 0-100%, next-open rebalancing. Initial weights are NOT optimized.";
    private TimeSeries _target = null!;
    private TimeSeries _contracts = null!;
    private TimeSeries _actual = null!;
    private int _fullContracts;

    public BreezeHoldPositionStrategy_new()
    {
        StartIndex = 0;
        AddParameter("Bias pct", ParameterType.Double, 50, 50, 50, 5);
        AddParameter("Full position contracts", ParameterType.Int32, 10, 10, 10, 1);
        // Keep smoothing fixed initially; 1 = the exact daily score, no inertia.
        AddParameter("Target alpha", ParameterType.Double, 1, 1, 1, 0.1);
        foreach (var f in BreezeEventModelNew.Features)
        {
            double lo = f.Weight, hi = f.Weight;
            if (f.Search)
            {
                lo = f.Weight < 0 ? -20 : 0;
                hi = f.Weight < 0 ? 0 : 20;
            }
            AddParameter("W " + f.Id + " " + f.Name, ParameterType.Double, f.Weight, lo, hi, 10);
        }
    }

    public override void Initialize(BarHistory bars)
    {
        if (!bars.Symbol.Equals("SPY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Breeze Hold Position_new: use SPY, Daily. SVXY has a separate strategy.");
        for (int i = 1; i < bars.Count; i++)
            if (bars.DateTimes[i].Date <= bars.DateTimes[i - 1].Date)
                throw new InvalidOperationException("Daily bars with unique increasing dates are required.");
        var model = BreezeEventModelNew.LoadCached(DefaultDataDirectory, CurvePath);
        var weights = BreezeEventModelNew.Features.Select((f, i) => Parameters[i + 3].AsDouble).ToArray();
        _fullContracts = Parameters[1].AsInt;
        var scores = BreezeEventModelNew.Score(model, weights, Parameters[0].AsDouble, Parameters[2].AsDouble);
        var baseline = BreezeEventModelNew.Score(model, BreezeEventModelNew.Features.Select(f => f.Weight).ToArray(), 50, 1);
        var dates = model.Select((r, i) => (r.Date, i)).ToDictionary(x => x.Date, x => x.i);
        _target = new TimeSeries(bars.DateTimes, false);
        _contracts = new TimeSeries(bars.DateTimes, false);
        _actual = new TimeSeries(bars.DateTimes, false);
        var frozen = new TimeSeries(bars.DateTimes, false);
        var old = new TimeSeries(bars.DateTimes, false);
        var availability = new TimeSeries(bars.DateTimes, false);
        int valid = 0, missing = 0, previousSourceIndex = -1;
        for (int i = 0; i < bars.Count; i++)
        {
            double value = double.NaN, fixedValue = double.NaN, oldValue = double.NaN;
            if (dates.TryGetValue(bars.DateTimes[i].Date, out int j))
            {
                var r = model[j];
                if (previousSourceIndex >= 0 && j != previousSourceIndex + 1)
                    throw new InvalidOperationException("Daily SPY required: chart skips source trading dates (weekly/monthly bars are unsupported).");
                previousSourceIndex = j;
                // Prevent trading a different scale/adjustment of the source series.
                if (Math.Abs(bars.Close[i] / r.Close - 1) > 0.001)
                    throw new InvalidOperationException($"SPY price differs from source on {r.Date:yyyy-MM-dd}. Use the same daily price adjustment/export.");
                value = scores[j]; fixedValue = baseline[j]; oldValue = r.OldHold;
            }
            if (double.IsFinite(value)) valid++; else missing++;
            _target.Add(value); frozen.Add(fixedValue); old.Add(oldValue);
            _contracts.Add(double.IsFinite(value) ? BreezeEventModelNew.Contracts(value, _fullContracts) : double.NaN);
            _actual.Add(double.NaN);
            availability.Add(double.IsFinite(value) ? 1 : 0);
        }
        if (valid == 0) throw new InvalidOperationException("No usable dates: check daily SPY range, source files and warm-up (252 bars).");
        const string pane = "Breeze_new position percent";
        PlotTimeSeries(_target, "Target position %", pane, WLColor.Blue, PlotStyle.Line);
        PlotTimeSeries(frozen, "Event baseline % (frozen)", pane, WLColor.Gray, PlotStyle.Line);
        SetPaneMinMax(pane, 0, 100); SetPaneDrawingOptions(pane, 36);
        DrawHorzLine(0, WLColor.Gray, 1, LineStyle.Dotted, pane, false);
        DrawHorzLine(100, WLColor.Gray, 1, LineStyle.Dotted, pane, false);
        PlotTimeSeries(_contracts, "Trading target contracts", "Breeze_new contracts", WLColor.Orange, PlotStyle.Line);
        PlotTimeSeries(_actual, "Actual contracts before next-open orders", "Breeze_new contracts", WLColor.Blue, PlotStyle.Line);
        SetPaneMinMax("Breeze_new contracts", 0, _fullContracts);
        SetPaneDrawingOptions("Breeze_new contracts", 20);
        Ribbon(old, "Old Hold (frozen)");
        Ribbon(frozen, "Event baseline (frozen)");
        Ribbon(_target, "Event optimized");
        PlotTimeSeries(availability, "Inputs usable 0/1", "Breeze_new availability", WLColor.Gray, PlotStyle.Line);
        SetPaneMinMax("Breeze_new availability", 0, 1); SetPaneDrawingOptions("Breeze_new availability", 10);
        WriteToDebugLog($"Breeze_new 0.10.0-alpha.1: {valid} usable bars; {missing} warm-up/missing bars (no orders). " +
            "74 event features, initial weights NOT trained. 8 variable weights = 6561 combinations. " +
            "Orders: one share/contract each; next open. 100% means Full position contracts, not 100% equity. " +
            "Baseline is fixed. Old Hold is display-only and optional.");
    }

    public override void Execute(BarHistory bars, int idx)
    {
        _actual[idx] = OpenQuantity;
        if (!double.IsFinite(_contracts[idx])) return;
        int target = (int)_contracts[idx];
        double actual = OpenQuantity;
        if (actual < 0 || Math.Abs(actual - Math.Round(actual)) > 0.000001)
            throw new InvalidOperationException("This strategy requires integer long quantities.");
        int current = (int)Math.Round(actual);
        while (current < target)
        {
            var order = PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 0, $"Breeze_new {_target[idx]:F1}% -> {target}/{_fullContracts}");
            order.Quantity = 1;
            current++;
        }
        // Bind every exit to a DIFFERENT open position. Unbound sell loops can
        // otherwise select the same position repeatedly before next-open fills.
        foreach (var position in OpenPositions.ToArray())
        {
            if (current <= target) break;
            if (Math.Abs(position.Quantity - 1) > 0.000001)
                throw new InvalidOperationException("Expected separate one-contract positions; rerun backtest with this strategy only.");
            ClosePosition(position, OrderType.Market, 0, $"Breeze_new {_target[idx]:F1}% -> {target}/{_fullContracts}");
            current--;
        }
    }

    private void Ribbon(TimeSeries percent, string pane)
    {
        var ribbon = new TimeSeries(percent.DateTimes, false);
        for (int i = 0; i < percent.Count; i++) ribbon.Add(double.IsFinite(percent[i]) ? 1 : double.NaN);
        PlotTimeSeries(ribbon, pane, pane, WLColor.Gray, PlotStyle.Histogram);
        for (int i = 0; i < percent.Count; i++)
        {
            if (!double.IsFinite(percent[i])) continue;
            double x = Math.Clamp(percent[i], 0, 100), t = x <= 50 ? x / 50 : (x - 50) / 50;
            byte Mix(int a, int b) => (byte)Math.Round(a + (b - a) * t);
            SetSeriesBarColor(ribbon, i, x <= 50 ? WLColor.FromRgb(Mix(196,242), Mix(55,201), Mix(55,76)) : WLColor.FromRgb(Mix(242,24), Mix(201,128), Mix(76,64)));
        }
        SetPaneMinMax(pane, 0, 1); SetPaneDrawingOptions(pane, 18);
    }
}
#endif

// All model code lives in this file to keep the WL C# strategy self-contained.
public static class BreezeEventModelNew
{
    public sealed record Feature(string Id, string Name, double Weight, bool Search, string Rule);
    public static readonly Feature[] Features =
    {
        new("T01","EMA order",10,true,"1 if E20>E50>E100>E200; price above E20 recorded separately"),
        new("T02","EMA rising",10,true,"Fraction of four one-bar EMA slopes >0"),
        new("T03","Stable growth",0,false,"Order + rising slopes + C>E20 + all breadth>=50 + ATRP1<=2, observed streak/5 capped at 1"),
        new("T04","Fast EMA falling",0,false,"E20 falling with E50/100/200 rising OR E20/50 falling with E100/200 rising"),
        new("T05","EMA fan",0,false,"Positive EMA adjacent gaps + positive C-E20, divided by C and 10%, clipped to 1"),
        new("T06","Compressed fan",0,false,"Bull order and (E20-E200)/C<=2%"),
        new("T07","Below all EMA",-10,false,"C below all four EMA"),
        new("T08","Drawdown depth",0,false,"Drawdown from trailing 252-close peak /20%, clipped to 1"),
        new("T09","Down waves",0,false,"Down-leg starts with >=1% drop in last 20 bars /3; new leg after >=1% rebound"),
        new("T10","Loaded-history drawdown",0,false,"Drawdown from loaded-history close peak /20%; NOT asserted true all-time high"),
        new("E01","EMA breaks down",-5,false,"Fraction of four close-vs-own-day-EMA downward crosses"),
        new("E02","EMA wick support",0,false,"Fraction of EMA with L<EMA and C>=EMA"),
        new("E03","Double EMA break",0,false,"C yesterday>=E20/E50 yesterday, today below both"),
        new("E04","EMA50 grace",0,false,"EMA20 break episode age<=4, no close below EMA50; resets on C>=EMA20"),
        new("E05","EMA reclaim",5,false,"Fraction of four upward close/EMA crosses"),
        new("E06","EMA pair crosses",0,false,"(up crosses-down crosses)/4 for 20/50,50/100,100/200,50/200"),
        new("E07","Failed EMA100 rebound",0,false,"Down cross E100 after C between E100 and E50 and prior C<E100 within 20 bars"),
        new("R01","RSI lower saturation",0,false,"Fraction of RSI 14/8 and 10/8 <=-0.999; NOT automatic buy"),
        new("R02","RSI upper saturation",5,false,"Fraction >=0.999; NOT automatic sell"),
        new("R03","RSI lower release",10,true,"Fraction crossing up from <=-0.999 to >-0.999"),
        new("R04","RSI upper release",0,false,"Fraction crossing down from >=0.999 to <0.999"),
        new("R05","RSI zero crosses",0,false,"(up zero crosses-down zero crosses)/2"),
        new("R06","Fast RSI leads",0,false,"Fast RSI>0 and slow RSI<=0; observed zero-cross lag recorded"),
        new("R07","RSI joint saturation",0,false,"+1 both upper, -1 both lower, 0 otherwise"),
        new("R08","Both RSI leave upper",0,false,"Both yesterday>=0.999 and both today<0.999"),
        new("B01","Breadth level",10,false,"Mean of four breadth closes /100; individual values also retained"),
        new("B02","Breadth washout",0,false,"Mean clipped low memberships: B50 3..8, B100 7..12, B200 10..20"),
        new("B03","B200 extreme",0,false,"clip((15-B200)/10,0,1)"),
        new("B04","Breadth rising",10,true,"Fraction of four breadth closes greater than previous close"),
        new("B05","Breadth green candles",0,false,"Fraction of authentic breadth Close>Open"),
        new("B06","Late breadth confirmation",0,false,"Yesterday RSI lower release or green recovery after falling bar; today >=2 breadth rising and C>=yesterday C"),
        new("B07","Top thinning",0,false,"New 20-bar close high; fraction of breadth lower than on previous 20-bar price peak"),
        new("B08","Bullish breadth divergence",0,false,"C tests/breaks prior close low (0.5% tolerance); prior low 3..20 bars ago and intervening rebound>=1%; fraction breadth higher"),
        new("B09","Breadth saucer",0,false,"Fraction: decline t-10 to t-5, nonnegative t-5 to t slope, no new low for last 3 bars"),
        new("B10","Fast weak slow strong",0,false,"B50<50, B200>=50, B200 rising"),
        new("B11","Broad breadth impulse",0,false,"Mean clipped positive daily breadth changes /10pp"),
        new("B12","Nasdaq breadth leads",0,false,"BN rises while fewer than 2 SP breadths rise; current observation only"),
        new("B13","High B200 plateau",0,false,"B200>=60; last 10-bar range<=5pp and price rises over 10 bars"),
        new("B14","Weaker participation analogue",0,false,"Current drawdown>=3%; compare to nearest-depth completed 20-bar drawdown episode in past 252 bars (within 2pp); positive mean breadth deterioration/20pp"),
        new("P01","Candle body",0,false,"Signed (C-O)/O in % divided by 2, clipped -1..1"),
        new("P02","Green lower wick",0,false,"C>O, L<previous L, lower wick>=40% of H-L"),
        new("P03","ATR shock",-5,false,"clip(ATRP1 / median(previous 20 ATRP1) -1,0,1); unsigned volatility, not candle body"),
        new("P04","Full upward gap",0,false,"L>max High of preceding 5 bars"),
        new("P05","Multi-bar impulse",0,false,"2- or 3-bar same-color run; signed total C/first O move /3%, clipped -1..1"),
        new("P06","Low-breadth doji",0,false,"Body<=20% range, lower wick>=40%, B50<20 and rising; flat bar =>0 (no pattern)"),
        new("V01","Front contango",10,true,"tanh(100*(F2/F1-1)/5); actual contracts, not 30/60 interpolation"),
        new("V02","Whole VIX curve",0,false,"(rising pairs-falling pairs)/pair count for actual contracts through 180 DTE"),
        new("V03","Mixed curve",0,false,"Front spread<0 and average of pairs after the first>=-0.5%"),
        new("V04","Backwardation starts",0,false,"Yesterday front>=0 and today<0, excluding front-expiry roll"),
        new("V05","Contango returns",0,false,"Yesterday front<=0 and today>0, excluding front-expiry roll"),
        new("V06","Spot above futures",0,false,"clip((VIX/F1-1)/0.2,-1,1)"),
        new("V07","Front expiry near",0,false,"Front DTE<=3; context only"),
        new("V08","VIX stress easing",0,false,"Yesterday VIX>=30 and today VIX falls; raw VIX retained"),
        new("Z01","Zweig starts",0,false,"Fraction of Working, Strong and Classic triggers today"),
        new("Z02","Zweig density",5,false,"Working density /30; also keep Strong density and both ages"),
        new("Z03","Zweig modes overlap",0,false,"Working and Strong densities both>0"),
        new("Z04","Damage during Zweig",0,false,"Working density>0, >=2 breadth falling, EMA close break and broken bull order"),
        new("I01","Titanic starts",-5,false,"Titanic today true and yesterday false"),
        new("I02","Titanic persistent",0,false,"Observed Titanic run>=7 (boundary choice explicitly 7)"),
        new("I03","Titanic recently ended",0,false,"No signal today, last ended signal within 20 bars"),
        new("C01","Strong RSI and trend",0,false,"At least one upper RSI (or upper release) + bull order + all slopes rising + C>=E20"),
        new("C02","Recovery before RSI",0,false,"Drawdown>=5%, green price, >=2 breadth rising, both RSI lower"),
        new("C03","Confirmed RSI release",0,false,"At least one RSI lower release or yesterday release; green price and >=2 breadth rising"),
        new("C04","EMA100 bounce no saturation",0,false,"Recent EMA20/50 break; closes hold E100 within 5 bars, touched within 1%, price and slow RSI rising, no lower saturation"),
        new("C05","Shallow pickup",10,true,"Green after 1..3 falling E20 bars and prior close below E20; held E50 throughout episode; bull order before episode; first green only"),
        new("C06","Deep pickup",10,true,"Up cross E20 green, past 10 bars include close below E50, all close>=E100 and low approached E100 within 1%"),
        new("C07","Repeated pickup",0,false,"New C05/C06 and preceding pickup 8..10 bars ago"),
        new("C08","Double RSI red break",-10,true,"Yesterday bull order and price above E20, both RSI upper; today both release + E20 down cross + red body >2x median prior 20 bodies"),
        new("C09","Fall despite contango",0,false,"C<E20, positive front spread, VIX rising"),
        new("C10","Recovery before contango",0,false,"C02/C03 in prior 10 bars, price up, VIX down, front<0 but improving (no roll)"),
        new("C11","Gap recovery complex",0,false,"Full gap, at least one EMA reclaim, >=3 breadth rising and contango return"),
        new("C12","Breadth bottom in stress",0,false,"B08>0, drawdown>=5%, both RSI lower and front<0; green trigger kept separately"),
        new("U01","Repeated RSI release",0,false,"Current slow RSI lower release, previous release 5..15 bars ago with renewed saturation between; green body>=past median; signed B200 difference retained"),
        new("U02","Healthy to double break",0,false,"Yesterday bull order above all EMA, front>0 and Working density=30; today double EMA20/50 break and backwardation start")
    };

    public sealed class Row
    {
        public DateTime Date;
        public double Open, High, Low, Close;
        public double[] E = new double[4], R = new double[2], B = new double[4], BO = new double[4];
        public double Atr, Body, Front, Curve, FarCurve, Spot, F1, Dte, Zw, Zs, ZwAge, ZsAge, ZwSignal, ZsSignal, Classic, Titanic, TitanicRun;
        public double OldHold = double.NaN;
        public bool Complete, Ready;
        public Dictionary<string, double> Values = new(StringComparer.Ordinal);
        public Dictionary<string, double> Details = new(StringComparer.Ordinal);
        public double Get(string id) => Values.TryGetValue(id, out double x) ? x : double.NaN;
    }
    private static readonly object Gate = new();
    private static string _cacheKey = "";
    private static List<Row>? _cache;
    private static readonly string[] RequiredFiles = { "SPY.csv", "sp500_sma50.csv", "sp500_sma100.csv", "sp500_sma200.csv", "nasdaq100_sma100.csv", "zweig_breadth_thrust.csv", "titanic_sentiment.csv" };
    public static IReadOnlyList<Row> LoadCached(string directory, string curvePath)
    {
        lock (Gate)
        {
            var files = RequiredFiles.Select(f => Path.Combine(directory, f)).Append(curvePath).ToList();
            string old = Path.Combine(directory, "hold_confidence.csv");
            if (File.Exists(old)) files.Add(old);
            string key = string.Join(";", files.Select(p => { var f = new FileInfo(p); return f.FullName + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks; }));
            if (_cache != null && _cacheKey == key) return _cache;
            var rows = Load(directory, curvePath); Build(rows);
            _cacheKey = key; _cache = rows;
            return rows;
        }
    }

    // Header-based CSV with quoted fields and explicit missing values. No ordinal-column assumptions.
    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required source export not found", path);
        var result = new List<Dictionary<string, string>>();
        string[]? header = null;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = new List<string>(); var b = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { b.Append('"'); i++; } else quoted = !quoted; }
                else if (c == ',' && !quoted) { fields.Add(b.ToString().Trim()); b.Clear(); }
                else b.Append(c);
            }
            if (quoted) throw new FormatException("Unclosed CSV quote: " + path);
            fields.Add(b.ToString().Trim());
            if (header == null) { header = fields.Select(x => x.Trim('\uFEFF')).ToArray(); continue; }
            if (fields.Count != header.Length) throw new FormatException("CSV column count mismatch: " + path);
            result.Add(header.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => fields[x.i], StringComparer.Ordinal));
        }
        if (result.Count == 0) throw new InvalidDataException("No source rows: " + path);
        return result;
    }
    private static DateTime Date(Dictionary<string,string> r) => DateTime.ParseExact(r["Date"], "yyyy-MM-dd", CultureInfo.InvariantCulture).Date;
    private static double Num(Dictionary<string,string>? r, string key)
    {
        if (r == null || !r.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s)) return double.NaN;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.IsFinite(x))
            throw new FormatException("Invalid number in " + key + ": " + s);
        return x;
    }
    private static Dictionary<DateTime, Dictionary<string,string>> Dated(string path) => ReadCsv(path).ToDictionary(Date);
    public static List<Row> Load(string directory, string curvePath)
    {
        var spy = Dated(Path.Combine(directory, "SPY.csv"));
        string[] names = { "sp500_sma50.csv", "sp500_sma100.csv", "sp500_sma200.csv", "nasdaq100_sma100.csv" };
        var breadth = names.Select(f => Dated(Path.Combine(directory,f))).ToArray();
        var z = ReadCsv(Path.Combine(directory,"zweig_breadth_thrust.csv")).ToDictionary(r => (Date(r), r["Mode"]));
        var ti = Dated(Path.Combine(directory,"titanic_sentiment.csv"));
        var curves = Dated(curvePath);
        string oldPath = Path.Combine(directory,"hold_confidence.csv");
        var old = File.Exists(oldPath) ? Dated(oldPath) : new Dictionary<DateTime,Dictionary<string,string>>();
        var rows = new List<Row>();
        foreach (var entry in spy.OrderBy(x => x.Key))
        {
            DateTime date = entry.Key; var data = entry.Value;
            var r = new Row { Date = date, Open = Num(data,"Open"), High = Num(data,"High"), Low = Num(data,"Low"), Close = Num(data,"Close") };
            if (!new[]{r.Open,r.High,r.Low,r.Close}.All(x => double.IsFinite(x) && x>0) || r.Low>Math.Min(r.Open,r.Close) || r.High<Math.Max(r.Open,r.Close))
                throw new InvalidDataException("Invalid SPY OHLC " + date.ToString("yyyy-MM-dd"));
            for (int j = 0; j < 4; j++)
            {
                breadth[j].TryGetValue(date, out var br); r.B[j] = Num(br,"Close"); r.BO[j] = Num(br,"Open");
                if (new[]{r.B[j],r.BO[j]}.Any(x => double.IsFinite(x) && (x<0 || x>100))) throw new InvalidDataException("Breadth outside 0..100");
            }
            z.TryGetValue((date,"Working"), out var zw); z.TryGetValue((date,"Strong"), out var zs);
            r.Zw = Num(zw,"ZweigDensity"); r.Zs = Num(zs,"ZweigDensity");
            r.ZwAge = Num(zw,"BarsSinceSignal"); r.ZsAge = Num(zs,"BarsSinceSignal");
            r.ZwSignal = Num(zw,"Signal"); r.ZsSignal = Num(zs,"Signal"); r.Classic = Num(zw,"ClassicSignal");
            ti.TryGetValue(date,out var t); r.Titanic=Num(t,"Signal"); r.TitanicRun=Num(t,"RunLength");
            old.TryGetValue(date,out var oh); r.OldHold=Num(oh,"HoldConfidencePct");
            curves.TryGetValue(date, out var v); r.Spot=Num(v,"M0_VIX");
            var contracts = new List<(double Dte,double Price)>();
            for (int k = 1; k <= 12; k++)
            {
                double days = Num(v,$"M{k}_Days"), price=Num(v,$"M{k}_Price");
                if (double.IsFinite(days) && double.IsFinite(price) && days>0 && days<=180 && price>0) contracts.Add((days,price));
            }
            contracts.Sort((a,b)=>a.Dte.CompareTo(b.Dte));
            r.Front=r.Curve=r.FarCurve=r.F1=r.Dte=double.NaN;
            if (contracts.Count>=2)
            {
                r.F1=contracts[0].Price; r.Dte=contracts[0].Dte; r.Front=100*(contracts[1].Price/r.F1-1);
                var spreads=Enumerable.Range(1,contracts.Count-1).Select(k=>100*(contracts[k].Price/contracts[k-1].Price-1)).ToArray();
                r.Curve=spreads.Select(x=>(double)Math.Sign(x)).Average();
                if(spreads.Length>1) r.FarCurve=spreads.Skip(1).Average();
            }
            r.Complete=r.B.Concat(r.BO).Concat(new[]{r.Front,r.Curve,r.FarCurve,r.Spot,r.F1,r.Dte,r.Zw,r.Zs,r.ZwSignal,r.ZsSignal,r.Classic,r.Titanic,r.TitanicRun}).All(double.IsFinite);
            rows.Add(r);
        }
        return rows;
    }

    private static double Clip(double x) => Math.Clamp(x,0,1);
    private static double Flag(bool x) => x ? 1 : 0;
    private static bool Up(double p,double pl,double c,double cl) => p<=pl && c>cl;
    private static bool Down(double p,double pl,double c,double cl) => p>=pl && c<cl;
    private static double Median(IEnumerable<double> values) { var a=values.OrderBy(x=>x).ToArray(); return a.Length%2==0 ? (a[a.Length/2-1]+a[a.Length/2])/2 : a[a.Length/2]; }
    private static double[] Ema(double[] c,int n) { var a=new double[c.Length]; if(c.Length==0)return a; a[0]=c[0]; double k=2.0/(n+1); for(int i=1;i<c.Length;i++)a[i]=k*c[i]+(1-k)*a[i-1]; return a; }
    public static double[] MyRsi(double[] c,int period)
    {
        var s=new double[c.Length]; var r=Enumerable.Repeat(double.NaN,c.Length).ToArray();
        if(c.Length==0)return r; s[0]=c[0]; if(c.Length>1)s[1]=c[1];
        double a=Math.Exp(-Math.Sqrt(2)*Math.PI/8), a1=-2*a*Math.Cos(Math.Sqrt(2)*Math.PI/8), a2=a*a,b=(1+a1+a2)/2;
        for(int i=2;i<c.Length;i++)s[i]=b*(c[i]+c[i-1])-a1*s[i-1]-a2*s[i-2];
        double peak=1e-7, decay=Math.Pow(0.5,1.0/77);
        for(int i=Math.Max(8,period);i<c.Length;i++)
        {
            double up=0,down=0;
            for(int j=0;j<period;j++){double delta=s[i-j]-s[i-j-1]; if(delta>0)up+=delta; else down-=delta;}
            double raw=up+down==0 ? 0 : (up-down)/(up+down); peak=Math.Max(peak*decay,Math.Abs(raw)); r[i]=Math.Clamp(raw/peak,-1,1);
        }
        return r;
    }

    public static void Build(List<Row> rows)
    {
        double[] closes=rows.Select(r=>r.Close).ToArray();
        var emas=new[]{20,50,100,200}.Select(n=>Ema(closes,n)).ToArray();
        var slow=MyRsi(closes,14); var fast=MyRsi(closes,10);
        int stable=0, break20=-1, lastPickup=-1, lastRelease=-1, titanicEnd=-1, lastFastZero=-1, lastSlowZero=-1;
        double allPeak=0, legPeak=0, legTrough=0; int legDirection=0; var downLegs=new Queue<int>();
        var completedCorrections=new List<(int End,double Depth,double[] Breadth)>();
        int correctionLow=-1; double correctionDepth=0;
        for(int i=0;i<rows.Count;i++)
        {
            var r=rows[i]; r.Values.Clear(); r.Details.Clear(); r.Ready=false;
            for(int k=0;k<4;k++)r.E[k]=emas[k][i]; r.R[0]=slow[i]; r.R[1]=fast[i];
            r.Body=100*(r.Close/r.Open-1); double pc=i==0?r.Close:rows[i-1].Close;
            r.Atr=100*Math.Max(r.High-r.Low,Math.Max(Math.Abs(r.High-pc),Math.Abs(r.Low-pc)))/r.Close;
            allPeak=Math.Max(allPeak,r.Close);
            if(i<20 || !r.Complete || !rows.Skip(i-20).Take(20).All(x=>x.Complete))
            {
                stable=0; break20=lastPickup=lastRelease=titanicEnd=lastFastZero=lastSlowZero=-1;
                legDirection=0; downLegs.Clear(); correctionLow=-1; correctionDepth=0; completedCorrections.Clear();
                foreach(var f in Features)r.Values[f.Id]=double.NaN;
                continue;
            }
            var p=rows[i-1]; var past=rows.Skip(i-20).Take(20).ToArray();
            var prev5=past.Skip(15).ToArray(); var prev10=past.Skip(10).ToArray();
            bool order=r.E[0]>r.E[1]&&r.E[1]>r.E[2]&&r.E[2]>r.E[3];
            var slopes=Enumerable.Range(0,4).Select(k=>r.E[k]-p.E[k]).ToArray();
            var down=Enumerable.Range(0,4).Select(k=>Down(p.Close,p.E[k],r.Close,r.E[k])).ToArray();
            var up=Enumerable.Range(0,4).Select(k=>Up(p.Close,p.E[k],r.Close,r.E[k])).ToArray();
            double[] bd=Enumerable.Range(0,4).Select(k=>r.B[k]-p.B[k]).ToArray();
            int rising=bd.Count(x=>x>0), falling=bd.Count(x=>x<0);
            bool green=r.Close>r.Open, red=r.Close<r.Open;
            double peak252=rows.Skip(Math.Max(0,i-251)).Take(Math.Min(252,i+1)).Max(x=>x.Close), dd=100*(1-r.Close/peak252);
            double bodyMedian=Median(past.Select(x=>Math.Abs(x.Body))), atrMedian=Median(past.Select(x=>x.Atr));
            double range=r.High-r.Low, wick=Math.Min(r.Open,r.Close)-r.Low;
            bool roll=r.Date.AddDays(r.Dte).Date!=p.Date.AddDays(p.Dte).Date;
            void Set(string id,double value)=>r.Values[id]=value;
            void Yes(string id,bool value)=>Set(id,Flag(value));
            Set("T01",Flag(order)); Set("T02",slopes.Count(x=>x>0)/4.0);
            stable=order&&slopes.All(x=>x>0)&&r.Close>r.E[0]&&r.B.All(x=>x>=50)&&r.Atr<=2 ? stable+1:0;
            Set("T03",Clip(stable/5.0)); Yes("T04",slopes[0]<0&&slopes.Skip(1).All(x=>x>0)||slopes[0]<0&&slopes[1]<0&&slopes[2]>0&&slopes[3]>0);
            Set("T05",Clip((Math.Max(0,r.Close-r.E[0])+Enumerable.Range(0,3).Sum(k=>Math.Max(0,r.E[k]-r.E[k+1])))/r.Close/0.10));
            Yes("T06",order&&(r.E[0]-r.E[3])/r.Close<=0.02); Yes("T07",r.E.All(e=>r.Close<e)); Set("T08",Clip(dd/20));
            if(legDirection==0){legPeak=legTrough=r.Close;legDirection=1;}
            if(legDirection==1){legPeak=Math.Max(legPeak,r.Close);if(r.Close<=legPeak*0.99){downLegs.Enqueue(i);legDirection=-1;legTrough=r.Close;}}
            else{legTrough=Math.Min(legTrough,r.Close);if(r.Close>=legTrough*1.01){legDirection=1;legPeak=r.Close;}}
            while(downLegs.Count>0&&downLegs.Peek()<i-19)downLegs.Dequeue();
            Set("T09",Clip(downLegs.Count/3.0)); Set("T10",Clip((1-r.Close/allPeak)/0.20));
            Set("E01",down.Count(x=>x)/4.0); Set("E02",r.E.Count(e=>r.Low<e&&r.Close>=e)/4.0); Yes("E03",down[0]&&down[1]);
            if(down[0])break20=i; if(r.Close<r.E[1]||r.Close>=r.E[0])break20=-1;
            Yes("E04",break20>=0&&i-break20<=4); Set("E05",up.Count(x=>x)/4.0);
            int[] a={0,1,2,1}, b={1,2,3,3}; double pairCross=0;
            for(int k=0;k<4;k++){if(Up(p.E[a[k]],p.E[b[k]],r.E[a[k]],r.E[b[k]]))pairCross++;if(Down(p.E[a[k]],p.E[b[k]],r.E[a[k]],r.E[b[k]]))pairCross--;}
            Set("E06",pairCross/4); Yes("E07",down[2]&&p.Close>p.E[2]&&p.Close<p.E[1]&&past.Take(19).Any(x=>x.Close<x.E[2]));
            bool[] lower=r.R.Select(x=>x<=-0.999).ToArray(), upper=r.R.Select(x=>x>=0.999).ToArray();
            bool[] release=Enumerable.Range(0,2).Select(k=>Up(p.R[k],-0.999,r.R[k],-0.999)).ToArray();
            bool[] leave=Enumerable.Range(0,2).Select(k=>Down(p.R[k],0.999,r.R[k],0.999)).ToArray();
            Set("R01",lower.Count(x=>x)/2.0); Set("R02",upper.Count(x=>x)/2.0); Set("R03",release.Count(x=>x)/2.0); Set("R04",leave.Count(x=>x)/2.0);
            Set("R05",Enumerable.Range(0,2).Sum(k=>Flag(Up(p.R[k],0,r.R[k],0))-Flag(Down(p.R[k],0,r.R[k],0)))/2);
            if(Up(p.R[1],0,r.R[1],0))lastFastZero=i; if(Up(p.R[0],0,r.R[0],0))lastSlowZero=i;
            Yes("R06",r.R[1]>0&&r.R[0]<=0); Set("R07",upper.All(x=>x)?1:lower.All(x=>x)?-1:0); Yes("R08",leave.All(x=>x));
            Set("B01",r.B.Average()/100); Set("B02",(Clip((8-r.B[0])/5)+Clip((12-r.B[1])/5)+Clip((20-r.B[2])/10))/3); Set("B03",Clip((15-r.B[2])/10));
            Set("B04",rising/4.0); Set("B05",Enumerable.Range(0,4).Count(k=>r.B[k]>r.BO[k])/4.0);
            Yes("B06",(p.Get("R03")>0||p.Close>p.Open&&rows[i-2].Close<rows[i-2].Open)&&rising>=2&&r.Close>=p.Close);
            var peakPast=past.OrderByDescending(x=>x.Close).First(); Set("B07",r.Close>peakPast.Close?Enumerable.Range(0,4).Count(k=>r.B[k]<peakPast.B[k])/4.0:0);
            int lowIndex=Enumerable.Range(i-20,18).OrderBy(k=>rows[k].Close).First(); var low=rows[lowIndex];
            bool retest=r.Close<=low.Close*1.005 && rows.Skip(lowIndex+1).Take(i-lowIndex-1).Any(x=>x.Close>=low.Close*1.01);
            Set("B08",retest?Enumerable.Range(0,4).Count(k=>r.B[k]>low.B[k])/4.0:0);
            Set("B09",Enumerable.Range(0,4).Count(k=>rows[i-5].B[k]<rows[i-10].B[k]&&r.B[k]>=rows[i-5].B[k]&&rows.Skip(i-2).Take(3).All(x=>x.B[k]>=rows.Skip(i-10).Take(8).Min(y=>y.B[k])))/4.0);
            Yes("B10",r.B[0]<50&&r.B[2]>=50&&bd[2]>0); Set("B11",bd.Select(x=>Clip(x/10)).Average()); Yes("B12",bd[3]>0&&bd.Take(3).Count(x=>x>0)<2);
            var recent10=rows.Skip(i-9).Take(10).ToArray(); Yes("B13",r.B[2]>=60&&recent10.Max(x=>x.B[2])-recent10.Min(x=>x.B[2])<=5&&r.Close>rows[i-10].Close);
            double localDd=100*(1-r.Close/rows.Skip(i-19).Take(20).Max(x=>x.Close));
            var analogues=completedCorrections.Where(x=>x.End>=i-252&&Math.Abs(x.Depth-localDd)<=2).OrderBy(x=>Math.Abs(x.Depth-localDd)).ThenByDescending(x=>x.End).ToArray();
            Set("B14",localDd>=3&&analogues.Length>0?Clip((analogues[0].Breadth.Average()-r.B.Average())/20):0);
            if(localDd>=3&&(correctionLow<0||localDd>correctionDepth)){correctionLow=i;correctionDepth=localDd;}
            if(correctionLow>=0&&r.Close>rows[correctionLow].Close*1.02){completedCorrections.Add((i,correctionDepth,(double[])rows[correctionLow].B.Clone()));correctionLow=-1;correctionDepth=0;}
            Set("P01",Math.Clamp(r.Body/2,-1,1)); Yes("P02",green&&r.Low<p.Low&&range>0&&wick/range>=0.4); Set("P03",atrMedian>0?Clip(r.Atr/atrMedian-1):0);
            Yes("P04",r.Low>prev5.Max(x=>x.High));
            int run=1;while(run<3&&Math.Sign(rows[i-run].Body)==Math.Sign(r.Body)&&r.Body!=0)run++;
            Set("P05",run>=2?Math.Clamp(100*(r.Close/rows[i-run+1].Open-1)/3,-1,1):0);
            Yes("P06",range>0&&Math.Abs(r.Close-r.Open)/range<=0.2&&wick/range>=0.4&&r.B[0]<20&&bd[0]>0);
            Set("V01",Math.Tanh(r.Front/5)); Set("V02",r.Curve); Yes("V03",r.Front<0&&r.FarCurve>=-0.5);
            Yes("V04",!roll&&p.Front>=0&&r.Front<0); Yes("V05",!roll&&p.Front<=0&&r.Front>0);
            Set("V06",Math.Clamp((r.Spot/r.F1-1)/0.2,-1,1)); Yes("V07",r.Dte<=3); Yes("V08",p.Spot>=30&&r.Spot<p.Spot);
            Set("Z01",(r.ZwSignal+r.ZsSignal+r.Classic)/3); Set("Z02",r.Zw/30); Yes("Z03",r.Zw>0&&r.Zs>0); Yes("Z04",r.Zw>0&&falling>=2&&down.Any(x=>x)&&!order);
            Yes("I01",r.Titanic==1&&p.Titanic==0); Yes("I02",r.Titanic==1&&r.TitanicRun>=7); if(p.Titanic==1&&r.Titanic==0)titanicEnd=i;
            Yes("I03",r.Titanic==0&&titanicEnd>=0&&i-titanicEnd<=20);
            Yes("C01",(upper.Any(x=>x)||leave.Any(x=>x))&&order&&slopes.All(x=>x>0)&&r.Close>=r.E[0]);
            Yes("C02",dd>=5&&green&&rising>=2&&lower.All(x=>x)); Yes("C03",(release.Any(x=>x)||p.Get("R03")>0)&&green&&rising>=2);
            Yes("C04",prev5.Any(x=>x.Get("E01")>0&&x.Close<x.E[0])&&prev5.Append(r).All(x=>x.Close>=x.E[2])&&prev5.Append(r).Any(x=>x.Low<=x.E[2]*1.01)&&r.Close>p.Close&&r.R[0]>p.R[0]&&r.R[0]>-0.999);
            int falling20=0;for(int k=i-1;k>=i-4&&rows[k].E[0]<rows[k-1].E[0];k--)falling20++;
            int episodeStart=i-falling20;
            bool shallow=falling20>=1&&falling20<=3&&green&&p.Close<=p.Open&&p.Close<p.E[0]&&rows.Skip(episodeStart).Take(falling20+1).All(x=>x.Close>=x.E[1])&&rows[episodeStart-1].Get("T01")==1;
            Yes("C05",shallow); Yes("C06",up[0]&&green&&prev10.Any(x=>x.Close<x.E[1])&&prev10.Append(r).All(x=>x.Close>=x.E[2])&&prev10.Append(r).Any(x=>x.Low<=x.E[2]*1.01));
            bool pickup=r.Get("C05")>0||r.Get("C06")>0; Yes("C07",pickup&&lastPickup>=0&&i-lastPickup>=8&&i-lastPickup<=10); if(pickup)lastPickup=i;
            Yes("C08",p.Get("T01")==1&&p.Close>p.E[0]&&leave.All(x=>x)&&down[0]&&red&&Math.Abs(r.Body)>2*bodyMedian);
            Yes("C09",r.Close<r.E[0]&&r.Front>0&&r.Spot>p.Spot);
            Yes("C10",prev10.Any(x=>x.Get("C02")>0||x.Get("C03")>0)&&r.Close>p.Close&&r.Spot<p.Spot&&r.Front<0&&r.Front>p.Front&&!roll);
            Yes("C11",r.Get("P04")>0&&up.Any(x=>x)&&rising>=3&&r.Get("V05")>0); Yes("C12",r.Get("B08")>0&&dd>=5&&lower.All(x=>x)&&r.Front<0);
            bool repeat=release[0]&&lastRelease>=0&&i-lastRelease>=5&&i-lastRelease<=15&&rows.Skip(lastRelease+1).Take(i-lastRelease-1).Any(x=>x.R[0]<=-0.999);
            Yes("U01",repeat&&green&&r.Body>=bodyMedian);
            r.Details["U01.B200_delta_pp"]=repeat?r.B[2]-rows[lastRelease].B[2]:double.NaN; if(release[0])lastRelease=i;
            Yes("U02",p.Get("T01")==1&&p.E.All(e=>p.Close>e)&&p.Front>0&&p.Zw==30&&r.Get("E03")>0&&r.Get("V04")>0);
            r.Details["ATRP1"]=r.Atr; r.Details["Drawdown252Pct"]=dd; r.Details["FrontSpreadPct"]=r.Front; r.Details["FrontRoll"]=Flag(roll);
            r.Details["EMA20GraceAge"]=break20<0?double.NaN:i-break20;
            r.Details["EMA20GraceCompleted"]=Flag(break20>=0&&i-break20>=3);
            r.Details["StableDays"]=stable; r.Details["FastZeroAge"]=lastFastZero<0?double.NaN:i-lastFastZero; r.Details["SlowZeroAge"]=lastSlowZero<0?double.NaN:i-lastSlowZero;
            for(int k=0;k<4;k++){r.Details[$"Breadth{k}.close"]=r.B[k];r.Details[$"Breadth{k}.change_pp"]=bd[k];r.Details[$"EMA{new[]{20,50,100,200}[k]}.slope"]=slopes[k];}
            foreach(var f in Features)
                if(!r.Values.TryGetValue(f.Id,out double value)||!double.IsFinite(value)||value < -1.000001||value>1.000001)throw new InvalidOperationException("Invalid feature "+f.Id+" "+r.Date);
            // Compute history states during warm-up, but do not trade them.
            r.Ready=i>=252;
        }
    }
    public static double[] Score(IReadOnlyList<Row> rows,double[] weights,double bias,double alpha)
    {
        if(weights.Length!=Features.Length||weights.Any(x=>!double.IsFinite(x))||!double.IsFinite(bias)||!double.IsFinite(alpha)||alpha<=0||alpha>1)throw new ArgumentException("Invalid score settings");
        double previous=double.NaN; var result=new double[rows.Count];
        for(int i=0;i<rows.Count;i++)
        {
            var r=rows[i]; if(!r.Ready){result[i]=double.NaN;previous=double.NaN;continue;}
            double value=bias;
            for(int k=0;k<Features.Length;k++)value+=weights[k]*r.Get(Features[k].Id);
            value=Math.Clamp(value,0,100);
            if(double.IsFinite(previous))value=alpha*value+(1-alpha)*previous;
            result[i]=value; previous=value;
        }
        return result;
    }
    public static int Contracts(double percent,int maximum)
    {
        if(!double.IsFinite(percent)||maximum<1)throw new ArgumentException("Invalid target/size");
        return (int)Math.Round(Math.Clamp(percent,0,100)*maximum/100,MidpointRounding.AwayFromZero);
    }
}
