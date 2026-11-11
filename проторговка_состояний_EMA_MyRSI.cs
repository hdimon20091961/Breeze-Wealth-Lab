
// Wealth-Lab8: 0=EMA, 1=MyRSI staged, 2=MyRSI Open-Close, 3=EMA pullback pickup.
// Load12 years of SPY Daily for warmup; trade only the final10 calendar years.
using System;
using System.Collections.Generic;
using System.Linq;
using WealthLab.Backtest;
using WealthLab.Core;

namespace BreezeHoldPosition.Strategies.StateTrading
{
    public sealed class BreezeStateTradingStrategy : UserStrategyBase
    {
        private NativeStatePlan _plan = null!;
        private int _mode;
        private int _myRsiMaxBars = 10;
        private int _emaPickupSize = 1;
        private int _emaMaxPickups = 3;
        private TimeSeries _actual = null!;
        private const int FullContracts = 10;
        private const string PositionsPane = "Positions 0-10";
        private const string MyRsiPane = "Breeze MyRSI";
        public override string StrategyName => "Breeze EMA - MyRSI (10 lots)";
        public override string Description =>
            "Strategy: 0=EMA20/50/100/200 order; 1=MyRSI_rep(14,8)+AGC77 release from lower saturation. " +
            "Mode2 uses the same RSI entry, buys next Open and sells that session's Close; both orders are scheduled from the signal day. " +
            "Mode3 starts with the EMA position and adds1-10 units after a fast recovery from an EMA20 pullback; " +
            "one pickup per pullback and1-6 pickups per T01 cycle. " +
            "Initial10 units, partial exits3/3/4, next-open Market orders. Load12 years of SPY Daily; " +
            "only the final10 years trade. NL-style alignment with ten explicitly sized one-unit positions. " +
            "MyRSI holding limit:1-20 bars (default10). " +
            "Use $10000 or more, no leverage and allow10 positions per symbol.";

        public BreezeStateTradingStrategy()
        {
            StartIndex=0;
            var selector=AddParameter("Strategy (0=EMA, 1=MyRSI, 2=MyRSI Open-Close, 3=EMA Pullback)",ParameterType.Int32,0,0,3,1);
            selector.MakeOptimizable=false;
            AddParameter("MyRSI: срок удержания, баров",ParameterType.Int32,10,1,20,1);
            AddParameter("EMA Pullback: размер добавки",ParameterType.Int32,1,1,10,1);
            AddParameter("EMA Pullback: максимум добавок",ParameterType.Int32,3,1,6,1);
        }

        public override void Initialize(BarHistory bars)
        {
            _mode=Parameters[0].AsInt;
            _myRsiMaxBars=Parameters[1].AsInt;
            _emaPickupSize=Parameters[2].AsInt;
            _emaMaxPickups=Parameters[3].AsInt;
            if(!bars.Symbol.Equals("SPY",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выберите SPY, дневные бары. Размер10 означает10 единиц SPY.");
            _plan=BuildPlan(bars,_mode,_myRsiMaxBars,_emaPickupSize,_emaMaxPickups);
            StartIndex=_plan.FirstTradingIndex;
            // Like the NL reference: numeric initialization creates writable plotted values.
            // The bool-false overload leaves this WL8 build's registered plots entirely NaN.
            _actual=new TimeSeries(bars.DateTimes,0.0);
            var target=new TimeSeries(bars.DateTimes,0.0);
            var intradayLots=new TimeSeries(bars.DateTimes,0.0);
            var rsi=new TimeSeries(bars.DateTimes,double.NaN);
            var anchor=new TimeSeries(bars.DateTimes,double.NaN);
            var ema=Enumerable.Range(0,4).Select(_=>new TimeSeries(bars.DateTimes,double.NaN)).ToArray();
            for(int i=0;i<bars.Count;i++)
            {
                var o=_plan.Observations[i];
                _actual[i]=_plan.Held[i];
                target[i]=_plan.Decisions[i].Target;
                if(_mode==2 && i>0)intradayLots[i]=_plan.Decisions[i-1].Target;
                rsi[i]=i<14?double.NaN:o.R14;
                anchor[i]=_plan.Decisions[i].Anchor ?? double.NaN;
                ema[0][i]=o.E20;ema[1][i]=o.E50;ema[2][i]=o.E100;ema[3][i]=o.E200;
            }
            PlotTimeSeries(target,"Target lots at next Open",PositionsPane,WLColor.DarkBlue,PlotStyle.Line);
            if(_mode==2)
                PlotTimeSeries(intradayLots,"Planned lots within Open-Close session",PositionsPane,WLColor.Orange,PlotStyle.Line);
            else
                PlotTimeSeries(_actual,"Actual lots",PositionsPane,WLColor.Orange,PlotStyle.Line);
            int maxLots=_mode==3?FullContracts+_emaPickupSize*_emaMaxPickups:FullContracts;
            SetPaneMinMax(PositionsPane,0.0,maxLots);
            SetPaneDrawingOptions(PositionsPane,25);
            if(_mode==0 || _mode==3)
            {
                var colors=new[]{WLColor.Blue,WLColor.Orange,WLColor.Purple,WLColor.Green};
                var periods=new[]{20,50,100,200};
                for(int k=0;k<4;k++)PlotTimeSeries(ema[k],"EMA"+periods[k],"Price",colors[k],PlotStyle.Line);
            }
            else
            {
                PlotTimeSeries(rsi,"MyRSI 14,8 + AGC(77)",MyRsiPane,WLColor.DarkBlue,PlotStyle.Line);
                foreach(double level in (_mode==2?new[]{-.999,0}:new[]{-.999,0,.5,.999}))
                    DrawHorzLine(level,WLColor.Gray,1,LineStyle.Dotted,MyRsiPane,false);
                SetPaneMinMax(MyRsiPane,-1.0,1.0);
                SetPaneDrawingOptions(MyRsiPane,50);
                if(_mode==1)
                    PlotTimeSeries(anchor,"RSI episode support (Close exit)","Price",WLColor.Orange,PlotStyle.Line);
            }
            for(int i=_plan.FirstTradingIndex;i<bars.Count;i++)
                if(_plan.Decisions[i].Target>_plan.Held[i])
                    SetBackgroundColorAllPanes(bars,i,WLColor.FromArgb(24,WLColor.Green));
        }

        public override void Execute(BarHistory bars,int idx)
        {
            _actual[idx]=OpenQuantity;
            // Preserve the marked-to-market last position; no fill beyond the tested window.
            if(idx<_plan.FirstTradingIndex || idx==bars.Count-1)return;
            if(_mode==2)
            {
                ExecuteIntradayRsi(bars,idx);
                return;
            }
            int currentQty=(int)Math.Round(OpenQuantity);
            if(Math.Abs(OpenQuantity-currentQty)>1e-6 || currentQty<0)
                throw new InvalidOperationException("Требуются целые длинные позиции по1 единице SPY.");
            var d=_plan.Decisions[idx];int desiredQty=d.Target;
            string family=(_mode==0 || _mode==3)?"EMA":"MyRSI";
            if(currentQty<desiredQty)
            {
                // Same target-vs-OpenQuantity loop as TorchSharp NL, but pin unit sizing in code.
                while(currentQty<desiredQty)
                {
                    string reason=_mode==3 && d.Reasons.Length>0?" "+SignalText(d.Reasons):"";
                    var order=PlaceTrade(bars,TransactionType.Buy,OrderType.Market,0,
                        family+reason+" BUY1 -> "+desiredQty+"/"+(_mode==3?FullContracts+_emaPickupSize*_emaMaxPickups:FullContracts));
                    order.Quantity=1;
                    currentQty++;
                }
            }
            else if(currentQty>desiredQty)
            {
                // Bind each unit exit to a different real position in WL8's next-open queue.
                var positions=OpenPositions.Where(p=>!p.NSF);
                if(_mode==3)positions=positions.OrderByDescending(p=>p.EntryBar);
                foreach(var position in positions.ToArray())
                {
                    if(currentQty<=desiredQty)break;
                    if(Math.Abs(position.Quantity-1.0)>1e-6)
                        throw new InvalidOperationException("Запустите стратегию заново: каждая позиция должна содержать1 единицу SPY.");
                    ClosePosition(position,OrderType.Market,0,family+" "+SignalText(d.Reasons)+" -> "+desiredQty+"/"+FullContracts);
                    currentQty--;
                }
                if(currentQty!=desiredQty)throw new InvalidOperationException("Не найдены единичные позиции для выравнивания объёма.");
            }
        }

        private void ExecuteIntradayRsi(BarHistory bars,int idx)
        {
            if(_plan.Decisions[idx].Target==0)return;
            // WL8 queues Market for the next Open and MarketClose for the next Close.
            // Paired orders share a unit tag, so each exit matches its own future entry.
            // No idx+1 price is read and no after-Close decision fills retrospectively.
            for(int tag=1;tag<=FullContracts;tag++)
            {
                var entry=PlaceTrade(bars,TransactionType.Buy,OrderType.Market,0,tag,"MyRSI intraday BUY1 next Open");
                entry.Quantity=1;
                var exit=PlaceTrade(bars,TransactionType.Sell,OrderType.MarketClose,0,tag,"MyRSI intraday SELL1 same session Close");
                exit.Quantity=1;
            }
        }

        internal static NativeStatePlan BuildPlan(BarHistory bars,int mode,int myRsiMaxBars=10,
            int emaPickupSize=1,int emaMaxPickups=3)
        {
            if(mode<0 || mode>3)throw new ArgumentOutOfRangeException(nameof(mode));
            if(myRsiMaxBars<1 || myRsiMaxBars>20)throw new ArgumentOutOfRangeException(nameof(myRsiMaxBars),"MyRSI: от1 до20 баров.");
            if(emaPickupSize<1 || emaPickupSize>10)throw new ArgumentOutOfRangeException(nameof(emaPickupSize),"Размер добавки: от1 до10 единиц.");
            if(emaMaxPickups<1 || emaMaxPickups>6)throw new ArgumentOutOfRangeException(nameof(emaMaxPickups),"Максимум добавок: от1 до6.");
            if(bars.Count<2)throw new InvalidOperationException("Недостаточно дневной истории SPY.");
            for(int i=1;i<bars.Count;i++)
                if(bars.DateTimes[i].Date<=bars.DateTimes[i-1].Date)
                    throw new InvalidOperationException("Требуются дневные бары с уникальными возрастающими датами.");
            DateTime first=bars.DateTimes[bars.Count-1].Date.AddYears(-10);
            int firstIndex=0;
            while(firstIndex<bars.Count && bars.DateTimes[firstIndex].Date<first)firstIndex++;
            if(firstIndex<200)
                throw new InvalidOperationException("Для прогрева EMA200 загрузите12 лет дневных данных SPY. " +
                    "Стратегия сама ограничит сделки последними10 годами.");
            var prices=Enumerable.Range(0,bars.Count).Select(i=>new PricePoint(bars.DateTimes[i].Date,
                bars.Open[i],bars.High[i],bars.Low[i],bars.Close[i],bars.Volume[i])).ToArray();
            var calculated=TechnicalIndicatorCalculator.Calculate(new PriceSeries("SPY",prices)).Points;
            var observations=new Observation[bars.Count];
            var decisions=new Decision[bars.Count];var held=new int[bars.Count];
            var policy=new PilotPolicy(mode==0?"T01":"RSI",true,14,myRsiMaxBars);
            var pullbackPolicy=new EmaPullbackPolicy(emaPickupSize,emaMaxPickups);int quantity=0;
            for(int i=0;i<bars.Count;i++)
            {
                var p=prices[i];var t=calculated[i];
                observations[i]=new Observation(p.Date,p.Open,p.High,p.Low,p.Close,
                    t.Ema20,t.Ema50,t.Ema100,t.Ema200,t.MyRsiRep,t.MyRsiRep10);
                if(mode==2)
                {
                    // End-of-session holdings are always zero; this target is for the next session only.
                    bool release=i>0 && observations[i-1].R14<=-.999 && observations[i].R14>-.999;
                    bool entry=i>=firstIndex && release && observations[i].R14<0;
                    held[i]=0;
                    decisions[i]=new Decision(entry?FullContracts:0,entry?"RSI_INTRADAY_OPEN_CLOSE":"",release?"RELEASE":"",null);
                }
                else if(mode==3)
                {
                    held[i]=quantity;
                    decisions[i]=pullbackPolicy.OnClose(i,observations[i],i==0?null:observations[i-1],quantity,i>=firstIndex);
                    quantity=decisions[i].Target;
                }
                else
                {
                    held[i]=quantity;
                    decisions[i]=policy.OnClose(i,observations[i],i==0?null:observations[i-1],quantity,i>=firstIndex);
                    quantity=decisions[i].Target;
                }
            }
            return new NativeStatePlan(observations,decisions,held,firstIndex);
        }

        private string SignalText(string reasons) => reasons
            .Replace("EMA20_NOT_RISING","EMA20: sell3")
            .Replace("EMA50_NOT_RISING","EMA50: sell3")
            .Replace("EMA_ORDER_BROKEN","EMA order broken: exit")
            .Replace("EMA_PULLBACK_ABOVE20","pickup above EMA20")
            .Replace("EMA_PULLBACK_ZONE","pickup EMA20-EMA50")
            .Replace("RSI_NEUTRAL_3","RSI >=0: sell3")
            .Replace("RSI_POSITIVE_HALF_3","RSI >=0.5: sell3")
            .Replace("RSI_UPPER_RELEASE_REMAINDER","RSI leaves +1: exit")
            .Replace("RSI_FAILED","RSI returns -1: exit")
            .Replace("SUPPORT_BROKEN","Price support broken: exit")
            .Replace("TIMEOUT",_myRsiMaxBars+" bars: exit");
    }

    internal sealed record NativeStatePlan(Observation[] Observations,Decision[] Decisions,int[] Held,int FirstTradingIndex);

internal sealed record PricePoint(DateTime Date, double Open, double High, double Low, double Close, double Volume);
internal sealed record PriceSeries(string Name, IReadOnlyList<PricePoint> Points);
internal sealed record TechnicalIndicatorPoint(
    DateTime Date,
    double Ema20,
    double Ema50,
    double Ema100,
    double Ema200,
    double MyRsiRep,
    double MyRsiRep10,
    double Atrp1);

internal sealed record TechnicalIndicatorSeries(
    int MyRsiPeriod,
    int MyRsiSmoothPeriod,
    int AgcHalfLife,
    IReadOnlyList<TechnicalIndicatorPoint> Points);

internal static class TechnicalIndicatorCalculator
{
    public const int MyRsiPeriod = 14;
    public const int FastMyRsiPeriod = 10;
    public const int MyRsiSmoothPeriod = 8;
    public const int AgcHalfLife = 77;

    public static TechnicalIndicatorSeries Calculate(PriceSeries prices)
    {
        if (prices.Points.Count == 0)
            return new(MyRsiPeriod, MyRsiSmoothPeriod, AgcHalfLife, Array.Empty<TechnicalIndicatorPoint>());

        double[] closes = prices.Points.Select(point => point.Close).ToArray();
        double[] ema20 = Ema(closes, 20);
        double[] ema50 = Ema(closes, 50);
        double[] ema100 = Ema(closes, 100);
        double[] ema200 = Ema(closes, 200);
        double[] myRsi = MyRsiRepAgc(closes, MyRsiPeriod, MyRsiSmoothPeriod, AgcHalfLife);
        double[] myRsi10 = MyRsiRepAgc(closes, FastMyRsiPeriod, MyRsiSmoothPeriod, AgcHalfLife);
        double[] atrp1 = Atrp1(prices.Points);

        var points = new List<TechnicalIndicatorPoint>(prices.Points.Count);
        for (int index = 0; index < prices.Points.Count; index++)
        {
            points.Add(new TechnicalIndicatorPoint(
                prices.Points[index].Date,
                ema20[index], ema50[index], ema100[index], ema200[index],
                myRsi[index], myRsi10[index], atrp1[index]));
        }

        return new(MyRsiPeriod, MyRsiSmoothPeriod, AgcHalfLife, points);
    }

    private static double[] Atrp1(IReadOnlyList<PricePoint> prices)
    {
        var result = new double[prices.Count];
        for (int index = 0; index < prices.Count; index++)
        {
            PricePoint point = prices[index];
            double previousClose = index == 0 ? point.Close : prices[index - 1].Close;
            double trueRange = Math.Max(
                point.High - point.Low,
                Math.Max(Math.Abs(point.High - previousClose), Math.Abs(point.Low - previousClose)));
            result[index] = point.Close == 0.0 ? 0.0 : 100.0 * trueRange / point.Close;
        }
        return result;
    }

    private static double[] Ema(IReadOnlyList<double> source, int period)
    {
        var result = new double[source.Count];
        if (source.Count == 0)
            return result;

        double alpha = 2.0 / (period + 1.0);
        result[0] = source[0];
        for (int index = 1; index < source.Count; index++)
            result[index] = alpha * source[index] + (1.0 - alpha) * result[index - 1];
        return result;
    }

    // Exact stand-alone equivalent of MyRSI_AGC_gpt(Close, 14, 8, 77):
    // TorchSharp4GPU's MyRSI_rep followed by the user's half-life AGC normalization.
    private static double[] MyRsiRepAgc(
        IReadOnlyList<double> source,
        int rsiPeriod,
        int smoothPeriod,
        int agcHalfLife)
    {
        double[] filtered = SuperSmootherRep(source, smoothPeriod);
        var result = new double[source.Count];
        int firstValid = Math.Max(smoothPeriod, rsiPeriod);
        double decay = Math.Pow(0.5, 1.0 / agcHalfLife);
        double peak = 0.0000001;

        for (int bar = firstValid; bar < source.Count; bar++)
        {
            double up = 0.0;
            double down = 0.0;
            for (int offset = 0; offset < rsiPeriod; offset++)
            {
                double difference = filtered[bar - offset] - filtered[bar - offset - 1];
                if (difference > 0.0)
                    up += difference;
                else if (difference < 0.0)
                    down -= difference;
            }

            double total = up + down;
            double rawValue = total == 0.0 ? 0.0 : (up - down) / total;
            peak *= decay;
            peak = Math.Max(peak, Math.Abs(rawValue));
            result[bar] = Math.Clamp(rawValue / peak, -1.0, 1.0);
        }

        return result;
    }

    private static double[] SuperSmootherRep(IReadOnlyList<double> source, int period)
    {
        var result = new double[source.Count];
        if (source.Count == 0)
            return result;

        result[0] = source[0];
        if (source.Count == 1)
            return result;
        result[1] = source[1];

        double a = Math.Exp(-Math.Sqrt(2.0) * Math.PI / period);
        double a1 = -2.0 * a * Math.Cos(Math.Sqrt(2.0) * Math.PI / period);
        double a2 = a * a;
        double b0 = (1.0 + a1 + a2) / 2.0;

        for (int index = 2; index < source.Count; index++)
        {
            result[index] = b0 * source[index] + b0 * source[index - 1]
                - a1 * result[index - 1] - a2 * result[index - 2];
        }

        return result;
    }
}
internal sealed record Observation(DateTime Date, double Open, double High, double Low, double Close,
    double E20, double E50, double E100, double E200, double R14, double R10)
{
    public bool Ordered => E20 > E50 && E50 > E100 && E100 > E200;
    public double Rsi(int period) => period == 14 ? R14 : R10;
}

internal sealed record Decision(int Target, string Reasons, string Event, double? Anchor);

// EMA mode with controlled scale-ins after a completed-bar pullback and recovery.
internal sealed class EmaPullbackPolicy
{
    private readonly int _pickupSize;
    private readonly int _maxPickups;
    private bool _firstSold, _secondSold;
    private int _pickups;
    private int _pullbackIndex = -1;

    public EmaPullbackPolicy(int pickupSize,int maxPickups)
    { _pickupSize=pickupSize;_maxPickups=maxPickups; }

    public Decision OnClose(int index,Observation current,Observation? previous,int held,bool eligible)
    {
        bool onset=current.Ordered && previous is { Ordered:false };
        string ev=onset?"ONSET":"";
        if(!eligible)return new(held,"","",null);
        if(held==0)
        {
            if(onset)
            {
                _firstSold=_secondSold=false;
                _pickups=0;
                _pullbackIndex=-1;
                return new(FullContractsForPolicy,"T01_START",ev,null);
            }
            return new(0,"",ev,null);
        }
        if(!current.Ordered)
        {
            _pullbackIndex=-1;
            return new(0,"EMA_ORDER_BROKEN",ev,null);
        }

        int target=held;
        var reasons=new List<string>();
        bool reduced=false;
        if(previous is not null)
        {
            if(!_firstSold && current.E20<=previous.E20)
            { target=Math.Max(0,target-3);_firstSold=true;reduced=true;reasons.Add("EMA20_NOT_RISING"); }
            if(!_secondSold && current.E50<=previous.E50)
            { target=Math.Max(0,target-3);_secondSold=true;reduced=true;reasons.Add("EMA50_NOT_RISING"); }

            // A fresh completed-bar cross arms one three-bar recovery window.
            if(previous.Close>=previous.E20 && current.Close<current.E20 && _pickups<_maxPickups)
                _pullbackIndex=index;

            if(_pullbackIndex>=0)
            {
                int age=index-_pullbackIndex;
                if(age>3)_pullbackIndex=-1;
                else if(age>=1 && !reduced && _pickups<_maxPickups &&
                    current.Close>current.E50 && current.Close>previous.Close)
                {
                    string pickup=current.Close>=current.E20?"EMA_PULLBACK_ABOVE20":"EMA_PULLBACK_ZONE";
                    target+=_pickupSize;
                    _pickups++;
                    _pullbackIndex=-1;
                    reasons.Add(pickup);
                    ev=pickup;
                }
            }
        }
        return new(Math.Min(FullContractsForPolicy+_pickupSize*_maxPickups,target),string.Join("|",reasons),ev,null);
    }

    private const int FullContractsForPolicy=10;
}

// Position life is separate from the one-day detector; all observations are completed bars.
internal sealed class PilotPolicy
{
    private readonly string family;
    private readonly bool staged;
    private readonly int period, maxBars;
    public PilotPolicy(string family, bool staged, int period = 14, int maxBars = 10)
    { this.family=family; this.staged=staged; this.period=period; this.maxBars=maxBars; }
    private const double Lower = -.999, Upper = .999;
    private double _saturationLow = double.NaN;
    private double _anchor = double.NaN;
    private int _entryIndex = -1;
    private bool _firstSold, _secondSold, _upperSeen;

    public Decision OnClose(int index, Observation current, Observation? previous, int held, bool eligible)
    {
        double r = current.Rsi(period);
        double p = previous?.Rsi(period) ?? double.NaN;
        if (r <= Lower)
            _saturationLow = p <= Lower ? Math.Min(_saturationLow, current.Low) : current.Low;
        bool release = p <= Lower && r > Lower;
        bool onset = current.Ordered && previous is { Ordered: false };
        string ev = family == "T01" ? (onset ? "ONSET" : "") : (release ? "RELEASE" : "");
        if (!eligible) return new(held, "", "", null);
        if (held == 0)
        {
            if (family == "T01" ? onset : release && r < 0)
            {
                _entryIndex = index + 1;
                _firstSold = _secondSold = _upperSeen = false;
                _anchor = family == "T01" ? double.NaN : Math.Min(_saturationLow, current.Low);
                return new(10, family == "T01" ? "T01_START" : "RSI_RELEASE", ev, Anchor());
            }
            return new(0, "", release && family != "T01" && r >= 0 ? "SKIP_ALREADY_NEUTRAL" : ev, null);
        }
        var reasons = new List<string>();
        int target = held;
        if (family == "T01")
        {
            if (!current.Ordered) return new(0, "EMA_ORDER_BROKEN", ev, null);
            if (staged && previous is not null)
            {
                if (!_firstSold && current.E20 <= previous.E20)
                { target -= 3; _firstSold = true; reasons.Add("EMA20_NOT_RISING"); }
                if (!_secondSold && current.E50 <= previous.E50)
                { target -= 3; _secondSold = true; reasons.Add("EMA50_NOT_RISING"); }
            }
        }
        else
        {
            if (r <= Lower) reasons.Add("RSI_FAILED");
            if (current.Close < _anchor) reasons.Add("SUPPORT_BROKEN");
            if (index - _entryIndex + 1 >= maxBars) reasons.Add("TIMEOUT");
            if (reasons.Count > 0) return new(0, string.Join("|", reasons), release ? "SUPPRESSED_RELEASE" : "", Anchor());
            _upperSeen |= r >= Upper;
            if (!staged && r >= 0) { target = 0; reasons.Add("RSI_NEUTRAL_ALL"); }
            if (staged)
            {
                if (!_firstSold && r >= 0)
                { target -= 3; _firstSold = true; reasons.Add("RSI_NEUTRAL_3"); }
                if (!_secondSold && r >= .5)
                { target -= 3; _secondSold = true; reasons.Add("RSI_POSITIVE_HALF_3"); }
                if (_upperSeen && r < Upper)
                { target = 0; reasons.Add("RSI_UPPER_RELEASE_REMAINDER"); }
            }
        }
        return new(Math.Max(0, target), string.Join("|", reasons), release && family != "T01" ? "SUPPRESSED_RELEASE" : ev, Anchor());
    }

    private double? Anchor() => double.IsFinite(_anchor) ? _anchor : null;
}
}
