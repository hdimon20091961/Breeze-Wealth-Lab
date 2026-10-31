using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BreezeVL;
using BreezeRules2.Research;

internal static class Program
{
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    [STAThread] static void Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_,name) =>
        {
            string file=Path.Combine(@"C:\Program Files\Quantacula, LLC\WealthLab 8",name.Name+".dll");
            return File.Exists(file)?AssemblyLoadContext.Default.LoadFromAssemblyPath(file):null;
        };
        Run(Path.GetFullPath(args.ElementAtOrDefault(0)??"."));
    }
    static void Set(object o,string name,object? v)=>o.GetType().GetField(name,Private)!.SetValue(o,v);
    static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,Private|BindingFlags.Public)!.GetValue(o)!;
    static object? Call(object o,string name,params object?[] args)=>o.GetType().GetMethod(name,Private)!.Invoke(o,args);
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Run(string root)
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        WealthLab.Core.DataSet DataSet(string name,string provider) => new() { Name=name, PreferredDataProviderName=provider };
        var spDefault=DataSet("S&P 500","WealthData");
        var nasdaqDefault=DataSet("NASDAQ 100","WealthData");
        Check(spDefault.QualifiedName=="S&P 500;WealthData","Wealth-Lab qualified identifier changed");
        var sources=new[]{DataSet("S&P 500","OtherProvider"),DataSet("S&P 500 Technology","WealthData"),spDefault,nasdaqDefault};
        Check(ReferenceEquals(DefaultDataSets.FindIndex(sources,"S&P 500"),spDefault),"S&P default/provider selection failed");
        Check(ReferenceEquals(DefaultDataSets.FindIndex(sources,"NASDAQ 100"),nasdaqDefault),"Nasdaq default selection failed");
        Check(DefaultDataSets.FindIndex(sources.Take(2),"S&P 500")==null,"Wrong provider/sector selected as default");
        var alias=DataSet("Nasdaq-100","WealthData");
        Check(ReferenceEquals(DefaultDataSets.FindIndex(new[]{alias},"NASDAQ 100"),alias),"Index punctuation prevented default selection");
        Check(DefaultDataSets.FindIndex(Array.Empty<WealthLab.Core.DataSet>(),"S&P 500")==null,"Empty source list must stay missing");
        Console.WriteLine("Default DataSets: real Wealth-Lab identifiers, provider, aliases and missing-source checks passed.");
        var app=new Application();
        app.Resources["BlueBackground"]=Brushes.White;
        app.Resources["ContainerBackground"]=Brushes.White;
        var surface=new BreezeWindow();
        // Offscreen preview: no Loaded/provider work, exports or user preference writes.
        Set(surface,"_scoreSelectionReady",false);
        Set(surface,"_tradingEnsembleCalculationStarted",true);
        Set(surface,"_initialCalculationStarted",true);
        string data=Path.Combine(root,"research/artifacts/event_hypotheses_xhigh_v1/baseline/data/Breeze");
        var inputs=ResearchData.Load(data,Path.Combine(root,"research/rules2_vix_term_structure/vix_term_structure_rules2.csv")).Inputs;
        var prices=new PriceSeries("SPY",inputs.Select(p=>new PricePoint(p.Date,p.Open,p.High,p.Low,p.Close,p.Volume)).ToList());
        BreadthSeries Breadth(string name,int period,Func<InputRow,double> selector)=>new(name,period,
            inputs.Select(p=>new BreadthPoint(p.Date,selector(p),selector(p),selector(p),selector(p),500)).ToList(),true);
        var sp50=Breadth("S&P50",50,p=>p.B50);var sp100=Breadth("S&P100",100,p=>p.B100);
        var sp200=Breadth("S&P200",200,p=>p.B200);var nasdaq=Breadth("Nasdaq100",100,p=>p.N100);
        var technical=TechnicalIndicatorCalculator.Calculate(prices);
        var fuzzy=FuzzyMarketStateCalculator.Calculate(prices,sp50,sp100,sp200,nasdaq,technical);
        Check(ResearchHoldConfidenceCalculator.TryCalculate(1,out var rules1,out var error,out _),error);
        var rules2=Rules2HoldConfidenceCalculator.Calculate(Rules2HoldConfidenceCalculator.CalculateFeatures(inputs),1);
        Set(surface,"_rules1Confidence",rules1);Set(surface,"_rules2Confidence",rules2);
        Set(surface,"_fuzzyStates",fuzzy);Set(surface,"_technicalIndicators",technical);
        Set(surface,"_sp50Series",sp50);Set(surface,"_sp100Series",sp100);Set(surface,"_sp200Series",sp200);
        Set(surface,"_nasdaq100Series",nasdaq);Set(surface,"_sp500PriceSeries",prices);
        var chart=Field<FuzzyStateChart>(surface,"FuzzyChart");
        chart.SetData(prices,fuzzy,technical);chart.SetScoreComparison(rules1,rules2);chart.SetShowCandles(true);
        chart.SetViewport(inputs[^200].Date,inputs[^1].Date);
        var ensembles=Field<TradingEnsembleChart>(surface,"TradingEnsembleChart");
        typeof(TradingEnsembleChart).GetField("PaneLayoutChanged",Private)!.SetValue(ensembles,null);
        var testSeries=new Dictionary<string,TradingEnsembleSeries>();
        for(int k=1;k<=3;k++)
        {
            int package=k;
            testSeries["GPU-ZZ"+k]=new("GPU-ZZ"+k,inputs.Select((p,i)=>
            {
                double ensemble=i==inputs.Count-1?60:50+30*Math.Sin(i*.045+package*.2);
                double[] deciles=i==inputs.Count-1
                    ? [60,60,60,60,60,40,40,40,40,40]
                    : Enumerable.Range(0,10).Select(n=>50+20*Math.Sin(i*.06+n*.15+package*.2)).ToArray();
                return new TradingEnsemblePoint(p.Date,ensemble,deciles);
            }).ToArray(),[]);
        }
        ensembles.SetData(testSeries,prices,technical);ensembles.SetPackages(testSeries.Keys);
        ensembles.SetViewport(inputs[^200].Date,inputs[^1].Date);
        var tabs=Field<TabControl>(surface,"BreadthTabs");
        Call(surface,"ConfigureHistorySlider",sp200);
        var state=Field<TextBlock>(surface,"StatusText");
        string longStatus="Торговые ансамбли: ZZ1: готов (600 моделей, до 2026-10-28); ZZ2: готов (319 моделей, до 2026-10-28); ZZ3: готов (900 моделей, до 2026-10-28)";
        var progressLevel=typeof(BreezeWindow).Assembly.GetType("BreezeVL.BreadthProgressLevel")!;
        typeof(BreezeWindow).GetMethod("SetStatus",Private)!.Invoke(surface,[longStatus,Enum.Parse(progressLevel,"Normal")]);
        string output=Path.Combine(root,"research/artifacts/rules2_dll_requirements_20261029/implementation/screenshots");Directory.CreateDirectory(output);
        typeof(BreezeWindow).GetMethod("WriteHoldConfidenceCsv",BindingFlags.NonPublic|BindingFlags.Static)!
            .Invoke(null,[Path.Combine(Path.GetDirectoryName(output)!,"rules2_export_check.csv"),rules2,true]);
        void Frame(string name,int width,int height,object plot,double dpi=96)
        {
            surface.Measure(new Size(width,height));surface.Arrange(new Rect(0,0,width,height));surface.UpdateLayout();
            Call(plot,"RenderSeries");
            surface.UpdateLayout();
            Check(Field<Canvas>(plot,"_canvas").Children.Count>10,"Preview has no rendered chart geometry");
            var bitmap=new RenderTargetBitmap((int)(width*dpi/96),(int)(height*dpi/96),dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(surface);
            using var stream=File.Create(Path.Combine(output,name+".png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
        }
        var one=Field<RadioButton>(surface,"Rules1Selector");var two=Field<RadioButton>(surface,"Rules2Selector");
        two.IsChecked=true;Call(surface,"PopulateMarketSetupRuleTree");Call(surface,"RefreshMarketSetupData");
        var setupSummary=Field<TextBlock>(surface,"MarketSetupCatalogSummary");
        var setupTitle=Field<TextBlock>(surface,"MarketSetupRuleTitle");
        var setupDescription=Field<TextBlock>(surface,"MarketSetupRuleDescription");
        var setupTree=Field<TreeView>(surface,"MarketSetupRuleTree");
        Check(setupSummary.Text.StartsWith("Rules2:") && !setupTitle.Text.StartsWith("T02"),
            "Rules2 selection did not replace the Market Setup catalogue");
        Check(setupDescription.Text.Contains("Вес Rules2") && setupTree.Items.Count==3,
            "Rules2 Market Setup description/families are missing");
        string rules2SetupTitle=setupTitle.Text;
        one.IsChecked=true;Call(surface,"PopulateMarketSetupRuleTree");Call(surface,"RefreshMarketSetupData");
        Check(setupSummary.Text.StartsWith("Rules1:") && setupTitle.Text!=rules2SetupTitle &&
              !setupDescription.Text.Contains("Вес Rules2"),
            "Rules1 selection did not restore its own Market Setup catalogue");
        Set(surface,"_contangoInitializationStarted",true);
        Set(surface,"_vixTermExportTask",Task.FromResult("old export"));
        Call(surface,"ResetContangoForRecalculation");
        Check(!Field<bool>(surface,"_contangoInitializationStarted") &&
              Field<Task<string>?>(surface,"_vixTermExportTask")==null,
            "Recalculate did not invalidate the completed VIX index/export cycle");
        one.IsChecked=true;Call(surface,"ApplySelectedScoreModel");
        var date=inputs[^50].Date;
        Set(surface,"_cursorDate",(DateTime?)date);
        Call(surface,"SetCursorPinned",true);
        Call(surface,"UpdateOperationalPosition",date,rules1.Points.FirstOrDefault(p=>p.Date==date),null);
        Frame("rules1_wide",1500,920,chart);
        var refresh=Field<Button>(surface,"RefreshButton");
        Point statusOrigin=state.TranslatePoint(new Point(0,0),surface);
        Point refreshOrigin=refresh.TranslatePoint(new Point(0,0),surface);
        double statusCenter=statusOrigin.Y+state.ActualHeight/2;
        double refreshCenter=refreshOrigin.Y+refresh.ActualHeight/2;
        Check(Math.Abs(statusCenter-refreshCenter)<1 && state.ActualWidth>0,
            "Top status moved to a second row instead of staying beside Help");
        Check(state.TextWrapping==TextWrapping.NoWrap && state.TextTrimming==TextTrimming.CharacterEllipsis && state.ToolTip!=null,
            "Top status must remain one-line/ellipsized with the full hover tooltip");
        two.IsChecked=true;Check(one.IsChecked==false,"Model selectors must exclude one another");
        two.IsChecked=false; // Real user click cannot clear a RadioButton; restore for the selected-model fixture.
        two.IsChecked=true;Call(surface,"ApplySelectedScoreModel");
        Check(Field<DateTime?>(surface,"_cursorDate")==date,"Model switch changed cursor date");
        Check(Field<bool>(surface,"_cursorPinned"),"Model switch unpinned cursor");
        int bars=Field<int>(surface,"_visibleBarCount");
        Call(surface,"IncreaseVisibleBarsClick",null,new RoutedEventArgs());
        Check(Field<int>(surface,"_visibleBarCount")==bars+20,"Shared navigation step changed");
        Call(surface,"DecreaseVisibleBarsClick",null,new RoutedEventArgs());
        Check(Field<int>(surface,"_visibleBarCount")==bars,"Shared navigation did not restore range");
        Call(surface,"UpdateOperationalPosition",date,rules2.Points.FirstOrDefault(p=>p.Date==date),null);
        Frame("rules2_wide",1500,920,chart);Frame("rules2_narrow",900,800,chart);
        Frame("rules2_narrow_125pct",900,800,chart,120);
        var text=Field<TextBlock>(surface,"OperationalPositionText");
        Check(text.Inlines.OfType<System.Windows.Documents.Run>().Count(r=>r.FontWeight==FontWeights.Bold)==2,"Score and target must be bold");
        Check(!text.Text.Contains("выше порога")&&!text.Text.Contains("ниже порога"),"Redundant threshold distance remains");
        var missing=rules2.Points.Last(p=>!double.IsFinite(p.Value));
        Call(surface,"UpdateOperationalPosition",missing.Date,missing,null);
        Check(text.Text.Contains("сохраняется прежняя позиция"),"Missing-score position explanation is absent");
        Frame("rules2_missing_score",1500,920,chart);
        tabs.SelectedItem=Field<TabItem>(surface,"TradingEnsemblesTab");
        Frame("ensemble_expanded",1500,920,ensembles);
        var canvas=Field<Canvas>(ensembles,"_canvas");
        var activeMode=Field<CheckBox>(surface,"TradingShowActiveEnsembles");
        Check(activeMode.IsChecked!=true && Equals(activeMode.Content,"Ensembles"),
            "Ensembles checkbox must replace Scoring and be off by default");
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Active Deciles"),
            "Default active pane title must be Active Deciles");
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Last: 15 (50.0%)"),
            "Active Deciles Last label must contain the exact count and selected-decile percentage");
        activeMode.IsChecked=true;
        Frame("ensemble_active_ensembles",1500,920,ensembles);
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Active Ensembles"),
            "Ensembles mode did not switch the active pane title");
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Last: 3 (33.3%)"),
            "Active Ensembles Last label must use the fixed nine-ensemble percentage");
        activeMode.IsChecked=false;
        Frame("ensemble_expanded",1500,920,ensembles);
        var weights=ensembles.GetPaneBoundaries();
        canvas.Children.OfType<Button>().ElementAt(2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ensembles.GetCollapsedPanes()[2],"Deciles collapse button failed");
        Check(ensembles.GetPaneBoundaries()==weights,"Collapse must preserve expanded weights");
        Frame("ensemble_deciles_collapsed",1500,920,ensembles);
        for(int i=0;i<4;i++)if(!ensembles.GetCollapsedPanes()[i])
            canvas.Children.OfType<Button>().ElementAt(i).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ensembles.GetCollapsedPanes().All(c=>c),"All pane headers must remain available");
        Frame("ensemble_all_collapsed",900,800,ensembles);
        ensembles.SetCollapsedPanes(new bool[4]);
        Frame("ensemble_restored",1500,920,ensembles);
        Check(ensembles.GetPaneBoundaries()==weights,"Restore must preserve original proportions");
        Console.WriteLine("RulesSwitchUI passed: full window layouts at 100/125%, shared navigation, pinned cursor, model selection, bold runs, missing data and collapse/restore.");
        Environment.Exit(0);
    }
}
