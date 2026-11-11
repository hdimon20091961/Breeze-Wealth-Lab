using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            }).ToArray(),[], ModelCount: package switch { 1 => 600, 2 => 319, _ => 900 },
                ArchitectureCounts: new Dictionary<int,int> { [3] = package switch { 1 => 600, 2 => 319, _ => 900 } },
                ValidFromDate: inputs[0].Date,
                Last125BenchmarkPercent: 100+package,
                BenchmarkPercentByLookback: new Dictionary<int,double>
                {
                    [140]=90+package,
                    [150]=100+package
                });
        }
        Set(surface,"_tradingEnsembles",testSeries);
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
        var fuzzyCanvas=Field<Canvas>(chart,"_canvas");
        Check(fuzzyCanvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Оценка 1") &&
              fuzzyCanvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Оценка 2"),
            "Separate Rules1/Rules2 score legends are missing");
        Check(!fuzzyCanvas.Children.OfType<TextBlock>().Any(t=>
                  t.Text is "EMA20" or "EMA50" or "EMA100" or "EMA200"),
            "EMA legend remains on Fuzzy map");
        Check(ScorePalette.Rules2Boundaries.SequenceEqual(new[]{0.0,41.0,45.0,55.0,59.0,100.0}),
            "Rules2 calibrated score boundaries changed");
        Check(fuzzyCanvas.Children.OfType<TextBlock>().Count(t=>t.Text=="50")>=2,
            "Both score legends must mark the fixed trading centre at 50");
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
        Call(surface,"ApplyTradingEnsembleOptions");
        Call(surface,"ApplyCursorValues",date);
        Call(surface,"UpdateBottomNavigationForSelectedTab");
        var performanceControls=Field<StackPanel>(surface,"TradingPerformancePeriodControls");
        var performanceBarsText=Field<TextBlock>(surface,"TradingPerformanceBarsText");
        Check(performanceControls.Visibility==Visibility.Visible && performanceBarsText.Text=="150",
            "Trading Ensemble must show the 150-bar performance-period control in the bottom strip");
        Check(performanceControls.Children.OfType<RepeatButton>().All(button=>
              button.Background is SolidColorBrush background && background.Color==Color.FromRgb(194,237,203) &&
              button.BorderBrush is SolidColorBrush border && border.Color==Color.FromRgb(134,220,151)),
            "Performance-period minus and plus buttons must match the light-green boundary color");
        var tradingCursor=Field<TextBlock>(surface,"TradingCursorValuesText");
        string tradingCursorText=string.Concat(tradingCursor.Inlines
            .OfType<System.Windows.Documents.Run>().Select(run=>run.Text));
        Check(tradingCursor.Visibility==Visibility.Visible &&
              tradingCursorText.StartsWith(date.ToString("yyyy-MM-dd")+"   Active ",StringComparison.Ordinal) &&
              tradingCursorText.Contains("%   Stop ") &&
              !tradingCursorText.Contains("Active Deciles") &&
              !tradingCursorText.Contains("Active Ensembles"),
            "Trading cursor row must show date, active percentage, Stop and ensemble values: " +
            $"visibility={tradingCursor.Visibility}; text={tradingCursorText}");
        Check(tradingCursor.Inlines.OfType<System.Windows.Documents.Run>().Any(run=>
              run.Text.StartsWith("ZZ1 ") && run.Foreground is SolidColorBrush brush &&
              brush.Color==Color.FromRgb(32,116,75)),
            "Trading cursor ZZ values must reuse the corresponding Ensemble % package color");
        string ensembleDescription=Field<string>(surface,"_tradingAnalysisReportText");
        Check(ensembleDescription.Contains("Elman — 600") &&
              ensembleDescription.Contains("Elman — 319") &&
              ensembleDescription.Contains("Elman — 900") &&
              ensembleDescription.Contains("Состав: Elman — 600") &&
              ensembleDescription.Contains("Последнее значение:") &&
              ensembleDescription.Contains('%') &&
              !ensembleDescription.Contains("Фактический состав:") &&
              ensembleDescription.Contains("SuperSmoother(8)") &&
              ensembleDescription.Contains("Режим: независимые децели") &&
              ensembleDescription.Contains("Ансамбль\tПрофит, весь период\tStr%, 150 баров") &&
              ensembleDescription.Contains("Период: 2018-07-15"),
            "Trading Ensemble report is incomplete: " + ensembleDescription);
        var analysisCards=Field<StackPanel>(surface,"TradingAnalysisCards");
        var packageRuns=analysisCards.Children.OfType<Border>()
            .Select(border=>border.Child).OfType<StackPanel>()
            .SelectMany(panel=>panel.Children.OfType<TextBlock>())
            .SelectMany(block=>block.Inlines.OfType<System.Windows.Documents.Run>())
            .ToArray();
        Check(packageRuns.Any(run=>run.Text=="ZZ1" && run.Foreground is SolidColorBrush brush &&
              brush.Color==Color.FromRgb(32,116,75)),
            "Active-ensemble ZZ labels must use the same package colors as the Ensemble % lines");
        Frame("ensemble_expanded",1500,920,ensembles);
        var canvas=Field<Canvas>(ensembles,"_canvas");
        var recentBoundary=canvas.Children.OfType<System.Windows.Shapes.Rectangle>()
            .SingleOrDefault(rectangle=>System.Windows.Automation.AutomationProperties.GetName(rectangle)==
                "Recent trading bars boundary");
        Check(recentBoundary is not null && Math.Abs(recentBoundary.Opacity-0.5)<1e-9 &&
              recentBoundary.Fill is SolidColorBrush boundaryBrush &&
              boundaryBrush.Color==Color.FromRgb(134,220,151) && recentBoundary.Height>400,
            "Trading Ensemble must show one 50%-opacity light-green recent-period boundary through all panes");
        double boundaryAt150=Canvas.GetLeft(recentBoundary!);
        Call(surface,"DecreaseTradingPerformanceBarsClick",null,new RoutedEventArgs());
        Check(performanceBarsText.Text=="140" &&
              Field<string>(surface,"_tradingAnalysisReportText").Contains("Str%, 140 баров") &&
              Field<string>(surface,"_tradingAnalysisReportText").Contains("91,0%"),
            "The minus button must switch the report and source-compatible Str% to 140 trading bars");
        Call(ensembles,"RenderSeries");
        recentBoundary=canvas.Children.OfType<System.Windows.Shapes.Rectangle>()
            .SingleOrDefault(rectangle=>System.Windows.Automation.AutomationProperties.GetName(rectangle)==
                "Recent trading bars boundary");
        double boundaryAt140=recentBoundary is null ? double.NaN : Canvas.GetLeft(recentBoundary);
        Check(recentBoundary is not null && boundaryAt140>boundaryAt150,
            $"Reducing the recent-performance period must move its boundary to the right: 150={boundaryAt150}, 140={boundaryAt140}");
        Call(surface,"IncreaseTradingPerformanceBarsClick",null,new RoutedEventArgs());
        Check(performanceBarsText.Text=="150" &&
              Field<string>(surface,"_tradingAnalysisReportText").Contains("101,0%"),
            "The plus button must restore the default 150-bar period");
        Call(surface,"ApplyTradingEnsembleOptions");
        var displayStops=Field<IReadOnlyDictionary<DateTime,IReadOnlyList<double>>>(
            surface,"_tradingChandelierStops");
        var displayExecutions=Field<IReadOnlyDictionary<DateTime,IReadOnlyList<double>>>(
            surface,"_tradingChandelierExecutions");
        Check(displayStops.Values.All(stops=>stops.Count<=1),
            "Trading chart stop cache must contain at most one maximum stop per date");
        Check(displayExecutions.Values.All(executions=>executions.Count<=1),
            "Trading chart execution cache must contain at most one maximum fill per date");
        DateTime lastTradingDate=Field<IReadOnlyDictionary<string,TradingEnsembleSeries>>(
            surface,"_tradingEnsembles").Values.SelectMany(series=>series.Points)
            .Max(point=>point.Date.Date);
        Check(displayStops.Keys.Any(stopDate=>stopDate.Date>lastTradingDate),
            "An open latest-bar position must expose its already calculated stop on the next trading session");
        var projectedStop=canvas.Children.OfType<System.Windows.Shapes.Ellipse>()
            .SingleOrDefault(marker=>System.Windows.Automation.AutomationProperties.GetName(marker)==
                "Projected next-session chandelier stop");
        Check(projectedStop is not null && projectedStop.Fill==Brushes.Transparent &&
              projectedStop.Stroke==Brushes.Black,
            "The next-session chandelier must be drawn as a hollow black circle in the future slot");
        var activeMode=Field<ComboBox>(surface,"TradingActiveMode");
        Check(activeMode.SelectedIndex==1 &&
              ((ComboBoxItem)activeMode.Items[0]).Content?.ToString()=="Ансамбли 0–10" &&
              ((ComboBoxItem)activeMode.Items[2]).Content?.ToString()=="Ансамбли 0/10",
            "Trading mode combo must contain three modes with Active Deciles selected by default");
        foreach(string name in new[]{"TradingShowGpuZz1","TradingShowGpuZz2","TradingShowGpuZz3",
                    "TradingShowTorchZz1","TradingShowTorchZz2","TradingShowTorchZz1Jordan",
                    "TradingShowTorchZz2Jordan","TradingShowEncogZz1","TradingShowEncogZz2"})
            Check(Field<CheckBox>(surface,name).IsChecked==true,$"{name} must be checked by default");
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Active Deciles"),
            "Default active pane title must retain the concise Active Deciles label");
        var targetPositions=Field<IReadOnlyDictionary<DateTime,TradingActivePosition>>(
            surface,"_tradingTargetActivePositions");
        TradingActivePosition lastTarget=targetPositions[lastTradingDate];
        int RequestedDeciles(int barIndex)=>testSeries.Values.Sum(series=>
            series.Points[barIndex].Deciles.Count(value=>value>=50.0));
        int shiftedBar=Enumerable.Range(1,inputs.Count-1)
            .First(index=>RequestedDeciles(index-1)!=RequestedDeciles(index));
        TradingActivePosition shiftedTarget=targetPositions[inputs[shiftedBar].Date];
        Check(Math.Abs(shiftedTarget.Value-RequestedDeciles(shiftedBar-1))<1e-9 &&
              Math.Abs(shiftedTarget.Value-RequestedDeciles(shiftedBar))>1e-9,
            "Open target must be dated on the next SPY trading bar, not on the signal-formation bar");
        var actualPositions=Field<IReadOnlyDictionary<DateTime,TradingActivePosition>>(
            surface,"_tradingActualActivePositions");
        TradingActivePosition lastActual=actualPositions[lastTradingDate];
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text==
                  $"Last: {lastActual.Value:0} ({lastActual.Percentage:0.0}%)"),
            "Decile Last label must show the actual active position");
        string analysisTitle=Field<TextBlock>(surface,"TradingAnalysisTitle").Text;
        Check(analysisTitle.Contains($"Активные ансамбли: Last {lastActual.Percentage:0.0}%") &&
              !analysisTitle.Contains("Price ") && analysisTitle.Contains("Stop "),
            "Analysis title must show actual holdings and chandelier stop");
        var openTargetHint=canvas.Children.OfType<System.Windows.Shapes.Polyline>()
            .FirstOrDefault(line=>System.Windows.Automation.AutomationProperties.GetName(line)==
                "Без люстры — оранжевая линия — подсказка линии");
        var heldHint=canvas.Children.OfType<System.Windows.Shapes.Polyline>()
            .FirstOrDefault(line=>System.Windows.Automation.AutomationProperties.GetName(line)==
                "С люстрой — синяя линия — подсказка линии");
        var hintTimer=Field<System.Windows.Threading.DispatcherTimer>(ensembles,"_activeLineHintTimer");
        var hintPopup=Field<Popup>(ensembles,"_activeLineHintPopup");
        Check(openTargetHint?.Tag is string openTargetText &&
              heldHint?.Tag is string heldText &&
              hintTimer.Interval==TimeSpan.FromMilliseconds(400) &&
              hintPopup.StaysOpen && hintPopup.PopupAnimation==PopupAnimation.None &&
              openTargetText.Contains("без защитной люстры") &&
              heldText.Contains("с учётом защитной люстры"),
            "Both active lines must expose unambiguous chandelier semantics after a 400 ms hover");
        Set(ensembles,"_activeLineHintOwner",openTargetHint);
        hintPopup.IsOpen=true;
        Call(ensembles,"CloseActiveLineHint",openTargetHint);
        Check(!hintPopup.IsOpen && !hintTimer.IsEnabled,
            "Leaving an active line must close its popup immediately and cancel the delay timer");
        activeMode.SelectedIndex=0;
        tradingCursorText=string.Concat(tradingCursor.Inlines
            .OfType<System.Windows.Documents.Run>().Select(run=>run.Text));
        Check(tradingCursorText.Contains("   Active ") && tradingCursorText.Contains("%   Stop ") &&
              !tradingCursorText.Contains("Active Ensembles") &&
              !tradingCursorText.Contains("Active Deciles"),
            "Trading cursor row must retain the concise active percentage after a mode switch");
        string ensembleModeReport=Field<string>(surface,"_tradingAnalysisReportText");
        Check(ensembleModeReport.Contains("Режим: ансамбль") && ensembleModeReport!=ensembleDescription,
            "Efficiency report did not switch from deciles to ensemble mode");
        Check(ensembleDescription.Contains("101,0%") && ensembleModeReport.Contains("101,0%") &&
              ensembleModeReport.Contains("Str%: исходное среднее по отдельным моделям"),
            "Source-compatible recent-period Str% must stay identical across trading modes");
        var quantityMethod=typeof(BreezeWindow).GetMethod("TradingDesiredQuantity",BindingFlags.NonPublic|BindingFlags.Static)!;
        Type activeModeType=typeof(BreezeWindow).Assembly.GetType("BreezeVL.TradingActiveMode")!;
        object proportionalMode=Enum.Parse(activeModeType,"ProportionalEnsembles");
        object decileMode=Enum.Parse(activeModeType,"IndependentDeciles");
        object quantumMode=Enum.Parse(activeModeType,"QuantumEnsembles");
        var quantityPoint=new TradingEnsemblePoint(date,65.0,new double[]{60,60,60,60,40,40,40,40,40,40});
        Check((int)quantityMethod.Invoke(null,new object[]{quantityPoint,proportionalMode,600})! == 7 &&
              (int)quantityMethod.Invoke(null,new object[]{quantityPoint,decileMode,600})! == 4,
            "Source modes require proportional ensemble sizing with away-from-zero rounding, or independent deciles");
        Check((int)quantityMethod.Invoke(null,new object[]{quantityPoint with { EnsemblePercent=50.0 },proportionalMode,6})! == 3 &&
              (int)quantityMethod.Invoke(null,new object[]{quantityPoint with { EnsemblePercent=49.0 },proportionalMode,600})! == 5,
            "Ensemble sizing must retain the reference small-package branch and intermediate positions below 50%");
        Check((int)quantityMethod.Invoke(null,new object[]{quantityPoint with { EnsemblePercent=50.0 },quantumMode,600})! == 10 &&
              (int)quantityMethod.Invoke(null,new object[]{quantityPoint with { EnsemblePercent=49.9 },quantumMode,600})! == 0,
            "Quantum ensemble mode must switch all ten units at the 50% threshold");
        CheckChandelierSimulation();
        CheckRealEnsembleProfit(root);
        Frame("ensemble_active_ensembles",1500,920,ensembles);
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Active Ensembles"),
            "Ensembles mode did not switch the active pane title");
        targetPositions=Field<IReadOnlyDictionary<DateTime,TradingActivePosition>>(
            surface,"_tradingTargetActivePositions");
        lastTarget=targetPositions[lastTradingDate];
        actualPositions=Field<IReadOnlyDictionary<DateTime,TradingActivePosition>>(
            surface,"_tradingActualActivePositions");
        lastActual=actualPositions[lastTradingDate];
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text==
                  $"Last: {lastActual.Value:0.0} ({lastActual.Percentage:0.0}%)"),
            "Ensemble mode must show the actual mean 0..10 position");
        Check(Field<TextBlock>(surface,"TradingAnalysisTitle").Text.Contains(
                  $"Активные ансамбли: Last {lastActual.Percentage:0.0}%"),
            "Proportional mode must update the analysis title percentage");
        activeMode.SelectedIndex=2;
        Call(ensembles,"RenderSeries");
        targetPositions=Field<IReadOnlyDictionary<DateTime,TradingActivePosition>>(
            surface,"_tradingTargetActivePositions");
        lastTarget=targetPositions[lastTradingDate];
        actualPositions=Field<IReadOnlyDictionary<DateTime,TradingActivePosition>>(
            surface,"_tradingActualActivePositions");
        lastActual=actualPositions[lastTradingDate];
        Check(canvas.Children.OfType<TextBlock>().Any(t=>t.Text=="Active Ensembles 0/10") &&
              canvas.Children.OfType<TextBlock>().Any(t=>t.Text==
                  $"Last: {lastActual.Value:0.0} ({lastActual.Percentage:0.0}%)"),
            "Quantum mode must show the actual averaged 0/10 position");
        Check(Field<string>(surface,"_tradingAnalysisReportText").Contains("квантовая позиция 0/10 по порогу 50%"),
            "Efficiency report did not switch to quantum ensemble trading");
        TextBlock quantumModeText=Field<StackPanel>(surface,"TradingAnalysisCards")
            .Children.OfType<Border>()
            .Select(border=>border.Child).OfType<StackPanel>()
            .SelectMany(panel=>panel.Children.OfType<TextBlock>())
            .Single(text=>text.Text.Contains("квантовая позиция 0/10",StringComparison.Ordinal));
        Check(quantumModeText.TextWrapping==TextWrapping.Wrap,
            "Trading mode description must reflow instead of clipping when the analysis panel is resized");
        Check(Field<TextBlock>(surface,"TradingAnalysisTitle").Text.Contains(
                  $"Активные ансамбли: Last {lastActual.Percentage:0.0}%"),
            "Quantum mode must update the analysis title percentage");
        activeMode.SelectedIndex=1;
        ensembles.SetCollapsedPanes(new bool[4]);
        Frame("ensemble_expanded",1500,920,ensembles);
        var weights=ensembles.GetPaneBoundaries();
        canvas.Children.OfType<Button>().ElementAt(2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ensembles.GetCollapsedPanes()[2],"Deciles collapse button failed");
        Check(ensembles.GetPaneBoundaries()==weights,"Collapse must preserve expanded weights");
        Frame("ensemble_deciles_collapsed",1500,920,ensembles);
        Check(Equals(canvas.Children.OfType<Button>().ElementAt(2).Content,"▲"),
            "Collapsed Deciles must use an upward triangle");
        var persistentDividers=canvas.Children.OfType<System.Windows.Shapes.Rectangle>()
            .Where(rectangle=>System.Windows.Automation.AutomationProperties.GetName(rectangle)
                .StartsWith("Trading pane divider ",StringComparison.Ordinal))
            .ToArray();
        Check(persistentDividers.Length==3,
            "All three gray divider strips must remain visible when Deciles is collapsed");
        double[] collapsedDividerYs=Field<double[]>(ensembles,"_dividerYs");
        Check(ensembles.TryBeginPaneResize(new Point(500,collapsedDividerYs[1])),
            "Divider beside collapsed Deciles must remain draggable");
        ensembles.ResizePane(new Point(500,collapsedDividerYs[1]+18));
        ensembles.EndPaneResize();
        Check(ensembles.GetCollapsedPanes()[2],
            "Dragging beside collapsed Deciles must not reopen it");
        Check(ensembles.GetPaneBoundaries()!=weights,
            "Dragging beside collapsed Deciles did not resize the nearest expanded panes");
        weights=ensembles.GetPaneBoundaries();
        for(int i=0;i<4;i++)if(!ensembles.GetCollapsedPanes()[i])
            canvas.Children.OfType<Button>().ElementAt(i).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ensembles.GetCollapsedPanes().All(c=>c),"All pane headers must remain available");
        Frame("ensemble_all_collapsed",900,800,ensembles);
        ensembles.SetCollapsedPanes(new bool[4]);
        Frame("ensemble_restored",1500,920,ensembles);
        Check(ensembles.GetPaneBoundaries()==weights,"Restore must preserve original proportions");

        TradingEnsembleSeries sourceSeries=testSeries["GPU-ZZ1"];
        var staleSeries=new TradingEnsembleSeries("GPU-ZZ1",
            sourceSeries.Points.Take(sourceSeries.Points.Count-1).ToArray(),
            new[]{new TradingForecastPoint(inputs[^1].Date,new double[]{40,45,50,55,60},true)},
            sourceSeries.ModelCount);
        ensembles.SetData(new Dictionary<string,TradingEnsembleSeries>{{"GPU-ZZ1",staleSeries}},prices,technical);
        ensembles.SetPackages(new[]{"GPU-ZZ1"});
        ensembles.SetViewport(inputs[^20].Date,inputs[^1].Date);
        ensembles.SetTargetActivePositions(new Dictionary<DateTime,TradingActivePosition>
        {
            [inputs[^2].Date]=new(5,50)
        });
        ensembles.SetActualActivePositions(new Dictionary<DateTime,TradingActivePosition>
        {
            [inputs[^2].Date]=new(3,30)
        });
        Call(ensembles,"RenderSeries");
        int staleFutureIndex=Field<int>(ensembles,"_futureDateIndex");
        IReadOnlyList<DateTime> staleDates=Field<IReadOnlyList<DateTime>>(ensembles,"_dates");
        Check(staleDates.Contains(inputs[^1].Date.Date) &&
              (staleFutureIndex<0 || staleDates[staleFutureIndex].Date>inputs[^1].Date.Date),
            "A newly downloaded SPY candle must stay a real date even when ensemble data ends one bar earlier");
        Check(!canvas.Children.OfType<TextBlock>().Any(text=>text.Text.StartsWith("Без люстры:",StringComparison.Ordinal)),
            "The Last signal/held label must not reuse a previous date when the latest real date has no position");

        DateTime nextForecastDate=inputs[^1].Date.AddDays(1);
        while(nextForecastDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            nextForecastDate=nextForecastDate.AddDays(1);
        var forecastSeries=staleSeries with
        {
            Forecasts=new[]{new TradingForecastPoint(nextForecastDate,new double[]{40,45,50,55,60},true)}
        };
        ensembles.SetData(new Dictionary<string,TradingEnsembleSeries>{{"GPU-ZZ1",forecastSeries}},prices,technical);
        Call(ensembles,"RenderSeries");
        int futureDateIndex=Field<int>(ensembles,"_futureDateIndex");
        Check(futureDateIndex>=0 &&
              Field<IReadOnlyList<DateTime>>(ensembles,"_dates")[futureDateIndex].Date==nextForecastDate.Date,
            "Only a date strictly after the latest SPY candle may occupy the forecast slot");

        int calculationGeneration=Field<int>(surface,"_tradingEnsembleCalculationGeneration");
        Set(surface,"_tradingEnsembleCalculationStarted",true);
        Call(surface,"InvalidateTradingEnsembleResults");
        Check(!Field<bool>(surface,"_tradingEnsembleCalculationStarted") &&
              Field<int>(surface,"_tradingEnsembleDataGeneration")==-1 &&
              Field<int>(surface,"_tradingEnsembleCalculationGeneration")==calculationGeneration+1 &&
              Field<IReadOnlyDictionary<string,TradingEnsembleSeries>>(surface,"_tradingEnsembles").Count==0,
            "Recalculate invalidation must cancel stale ensemble state and allow a clean retry");
        Console.WriteLine("RulesSwitchUI passed: full window layouts at 100/125%, shared navigation, pinned cursor, model selection, bold runs, missing data and collapse/restore.");
        Environment.Exit(0);
    }

    static void CheckChandelierSimulation()
    {
        DateTime first=new(2026,1,5);
        var prices=new[]
        {
            new PricePoint(first,100,100,99,100,1),
            new PricePoint(first.AddDays(1),100,110,100,109,1),
            new PricePoint(first.AddDays(2),100,102,99,101,1),
            new PricePoint(first.AddDays(3),101,102,100,101,1),
            new PricePoint(first.AddDays(4),102,104,101,103,1),
            new PricePoint(first.AddDays(5),103,105,102,104,1)
        };
        var points=prices.Select(p=>new TradingEnsemblePoint(p.Date,100,
            Enumerable.Repeat(100.0,10).ToArray())).ToArray();
        var series=new TradingEnsembleSeries("test",points,Array.Empty<TradingForecastPoint>(),10);
        TradingChandelierResult result=TradingChandelierSimulation.Run(prices,series,0,5,
            TradingActiveMode.QuantumEnsembles,3000);
        double expectedStop=110*(1.0-TradingChandelierSimulation.StopPercent/100.0);
        Check(result.StopsByDate[first.AddDays(2)].Count==10 &&
              result.StopsByDate[first.AddDays(2)].All(value=>Math.Abs(value-expectedStop)<1e-9),
            "A dated chandelier marker must show the order prepared on the previous bar");
        Check(result.ExecutedStopsByDate[first.AddDays(2)].Count==10 &&
              result.ExecutedStopsByDate[first.AddDays(2)].All(value=>Math.Abs(value-100.0)<1e-9),
            "A gap below the chandelier must record the opening price as the actual fill");
        Check(!result.StopsByDate.ContainsKey(first.AddDays(1)) &&
              !result.StopsByDate.ContainsKey(first.AddDays(3)) &&
              !result.StopsByDate.ContainsKey(first.AddDays(4)) &&
              result.StopsByDate[first.AddDays(5)].Count==10,
            "A stop exit must skip D+1, re-enter on the following bar and activate its new stop one bar later");
        Check(result.HeldQuantityByDate[first]==0 &&
              result.HeldQuantityByDate[first.AddDays(1)]==10 &&
              result.HeldQuantityByDate[first.AddDays(2)]==0 &&
              result.HeldQuantityByDate[first.AddDays(3)]==0 &&
              result.HeldQuantityByDate[first.AddDays(4)]==10,
            "Actual held quantities must reflect the stop exit and the skipped re-entry bar");
        double expectedNextStop=105*(1.0-TradingChandelierSimulation.StopPercent/100.0);
        Check(result.NextBarStops.Count==10 &&
              result.NextBarStops.All(value=>Math.Abs(value-expectedNextStop)<1e-9),
            "The latest close must expose the already known chandelier level for the next trading session");
        Check(double.IsFinite(result.ProfitPercent),"Chandelier simulation Profit must be finite");

        var partialPoints=new[]
        {
            new TradingEnsemblePoint(prices[0].Date,50,Enumerable.Repeat(50.0,10).ToArray()),
            new TradingEnsemblePoint(prices[1].Date,100,Enumerable.Repeat(100.0,10).ToArray()),
            new TradingEnsemblePoint(prices[2].Date,100,Enumerable.Repeat(100.0,10).ToArray())
        };
        var partialSeries=new TradingEnsembleSeries("partial",partialPoints,
            Array.Empty<TradingForecastPoint>(),10);
        TradingChandelierResult partial=TradingChandelierSimulation.Run(prices,partialSeries,0,2,
            TradingActiveMode.ProportionalEnsembles,3000);
        Check(partial.HeldQuantityByDate[prices[1].Date]==5 &&
              partial.HeldQuantityByDate[prices[2].Date]==5,
            "Actual held quantities must retain the five newer lots after a partial stop exit");
        Console.WriteLine("Chandelier simulation: 3.75% rising stop, gap execution and one-bar re-entry delay passed.");
    }

    static void CheckRealEnsembleProfit(string root)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("BREEZE_SKIP_REAL_ENSEMBLE_AUDIT"), "1",
                StringComparison.Ordinal)) return;
        string audit=Path.Combine(root,"research/ensemble_fix_20261102");
        string results=Path.Combine(audit,"result-short.json");
        if(!File.Exists(results)) return; // Optional local packages, never required on other machines.
        using var report=System.Text.Json.JsonDocument.Parse(File.ReadAllText(results));
        var history=File.ReadLines(@"D:\fear_greed_close\Breeze\SPY.csv").Skip(1).Select(line=>
        {
            string[] c=line.Split(',');
            return new PricePoint(DateTime.Parse(c[0],CultureInfo.InvariantCulture),
                double.Parse(c[1],CultureInfo.InvariantCulture),double.Parse(c[2],CultureInfo.InvariantCulture),
                double.Parse(c[3],CultureInfo.InvariantCulture),double.Parse(c[4],CultureInfo.InvariantCulture),
                double.Parse(c[5],CultureInfo.InvariantCulture));
        }).ToList();
        int start=history.FindIndex(p=>p.Date>=new DateTime(2018,7,15));
        var profitMethod=typeof(BreezeWindow).GetMethod("CalculateModeProfitPercent",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var row in report.RootElement.EnumerateArray())
        {
            string key=row.GetProperty("Key").GetString()!;
            var points=File.ReadLines(Path.Combine(audit,key+"-validated-series.csv")).Skip(1).Select(line=>
            {
                string[] c=line.Split(',');
                return new TradingEnsemblePoint(DateTime.Parse(c[0],CultureInfo.InvariantCulture),
                    double.Parse(c[1],CultureInfo.InvariantCulture),c.Skip(2).Select(s=>double.Parse(s,CultureInfo.InvariantCulture)).ToArray());
            }).ToArray();
            var series=new TradingEnsembleSeries(key,points,Array.Empty<TradingForecastPoint>(),row.GetProperty("Count").GetInt32());
            Type modeType=typeof(BreezeWindow).Assembly.GetType("BreezeVL.TradingActiveMode")!;
            int end=history.FindLastIndex(p=>p.Date.Date<=points[^1].Date.Date);
            foreach(string modeName in new[]{"IndependentDeciles","ProportionalEnsembles"})
            {
                object mode=Enum.Parse(modeType,modeName);
                double actual=(double)profitMethod.Invoke(null,new object[]{history,series,start,end,mode})!;
                Check(double.IsFinite(actual),
                    $"{key} mode {modeName}: chandelier Profit must remain finite");
            }
        }
        Console.WriteLine("Real ensemble Profit: all package ledgers remain finite with the permanent 3.75% chandelier and one-bar re-entry delay.");
    }
}
