using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Globalization;
using System.IO;
using System.Text;
using WealthLab.Core;
using WealthLab.WPF;

namespace BreezeVL;

public partial class BreezeWindow : ChildWindow
{
    private enum EmaResearchRegime
    {
        Bullish,
        Mixed,
        Damaged
    }

    private sealed record ForwardOutcomeStats(int Count, double PositiveRate, double AverageReturn);

    private sealed record MarketResearchSummary(
        int HorizonBars,
        IReadOnlyDictionary<MarketZone, ForwardOutcomeStats> PhaseOutcomes,
        IReadOnlyDictionary<EmaResearchRegime, ForwardOutcomeStats> EmaOutcomes);

    private sealed record ExportSnapshot(
        BreadthSeries Sp50,
        BreadthSeries Sp100,
        BreadthSeries Sp200,
        BreadthSeries Nasdaq100,
        PriceSeries Sp500Price,
        PriceSeries? SvxyPrice,
        FuzzyMarketStateSeries FuzzyStates,
        HoldConfidenceSeries HoldConfidence,
        TechnicalIndicatorSeries TechnicalIndicators,
        TitanicSeries Titanic,
        ZweigSeries ZweigStrong,
        ZweigSeries ZweigWorking);

    public const string WindowToken = "BreezeVL.MarketBreadth";
    private const int DefaultVisibleBarCount = 200;
    private const int MinimumVisibleBarCount = 50;
    private const int VisibleBarStep = 25;
    private const string VixTermDataDirectory = @"D:\fear_greed_close\VIX";
    private const string VixTermExportPath = @"D:\fear_greed_close\vix_term_structure.csv";
    private const string VixCurveStateExportPath = @"D:\fear_greed_close\Breeze\contango_market_state.csv";
    private static string PhaseModelComponentsExportPath =>
        Path.Combine(ExportDirectory, "phase_model_components.csv");
    private static readonly SemaphoreSlim ExportGate = new(1, 1);
    private static string ExportDirectory => Directory.Exists(@"D:\")
        ? @"D:\fear_greed_close\Breeze"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Breeze");

    public BreezeWindow()
    {
        InitializeComponent();
        OrderTabsByAnalysisPriority();
        Loaded += OnLoaded;
        OverviewCombinedChart.SetAutoScaleBreadth(true);
        OverviewCombinedChart.SetShowDateLabels(false);
        OverviewAtrpChart.SetShowDateLabels(false);
        ContangoSpChart.SetAutoScaleBreadth(true);
        ContangoSpChart.SetPriceOnly(true);
        ContangoSpChart.SetTitle("S&P 500 (SPY)");
        ContangoSvxyChart.SetPriceOnly(true);
        ContangoSvxyChart.SetTitle("SVXY");
        foreach (BreadthChart chart in AllCharts)
            chart.CursorDateChanged += UpdateCursorValues;
        OverviewAtrpChart.CursorDateChanged += UpdateCursorValues;
        ContangoMyRsiChart.CursorDateChanged += UpdateCursorValues;
        FuzzyChart.CursorDateChanged += UpdateCursorValues;
        TitanicChart.CursorDateChanged += UpdateCursorValues;
        ZweigChart.CursorDateChanged += UpdateCursorValues;
        TitanicChart.PersistentDaysChanged += TitanicPersistentDaysChanged;
        BreadthTabs.SelectionChanged += BreadthTabsSelectionChanged;
        BreadthTabs.LostMouseCapture += (_, _) =>
        {
            _isPanning = false;
            SetHorizontalDragCursor(false);
        };
        Unloaded += (_, _) =>
        {
            SetHorizontalDragCursor(false);
            _calculationCts?.Cancel();
            _contangoLoadCts?.Cancel();
        };
        SetPlaceholders();
        UpdateOverviewBreadthMode();
    }

    private void OrderTabsByAnalysisPriority()
    {
        TabItem[] orderedTabs = [FuzzyTab, OverviewTab, ContangoTab, TitanicTab, ZweigTab];
        foreach (TabItem tab in orderedTabs)
            BreadthTabs.Items.Remove(tab);
        foreach (TabItem tab in orderedTabs)
            BreadthTabs.Items.Add(tab);
        BreadthTabs.SelectedItem = FuzzyTab;
    }

    public override string Token => WindowToken;

    public override string WorkspaceString
    {
        get
        {
            double totalWidth = FuzzyAnalysisColumn.ActualWidth + FuzzyChartColumn.ActualWidth;
            double splitRatio = totalWidth > 1.0
                ? FuzzyAnalysisColumn.ActualWidth / totalWidth
                : 0.25;
            return $"{(SpDataSet.SelectedItem as DataSet)?.QualifiedName}|" +
                   $"{(NasdaqDataSet.SelectedItem as DataSet)?.QualifiedName}|" +
                   $"{(UseWorkingZweig.IsChecked == true ? "Working" : "Strong")}|" +
                   $"{PositionProfile?.SelectedIndex ?? 1}|" +
                   splitRatio.ToString("0.####", CultureInfo.InvariantCulture);
        }
        set => _workspaceSelection = value ?? string.Empty;
    }

    private string _workspaceSelection = string.Empty;
    private BreadthSeries? _viewportMaster;
    private BreadthSeries? _sp50Series;
    private BreadthSeries? _sp100Series;
    private BreadthSeries? _sp200Series;
    private BreadthSeries? _nasdaq100Series;
    private BreadthSeries? _nasdaq50Series;
    private PriceSeries? _sp500PriceSeries;
    private PriceSeries? _svxyPriceSeries;
    private PriceSeries? _vixPriceSeries;
    private FuzzyMarketStateSeries? _fuzzyStates;
    private HoldConfidenceSeries? _holdConfidence;
    private TechnicalIndicatorSeries? _technicalIndicators;
    private TitanicSeries? _titanicSeries;
    private ZweigSeries? _zweigSeries;
    private ZweigSeries? _zweigStrongSeries;
    private ZweigSeries? _zweigWorkingSeries;
    private int _visibleBarCount = DefaultVisibleBarCount;
    private DateTime? _cursorDate;
    private bool _cursorPinned;
    private bool _horizontalDragCursorActive;
    private Cursor? _previousOverrideCursor;
    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartSlider;
    private double _panChartWidth;
    private CancellationTokenSource? _calculationCts;
    private DateTime? _pendingCursorDate;
    private bool _cursorUpdateScheduled;
    private bool _viewportUpdateScheduled;
    private int _titanicExportGeneration;
    private int _holdConfidenceExportGeneration;
    private bool _isCalculationInProgress;
    private VixTermStructureStore? _vixTermStore;
    private VixCurveStateSeries? _contangoStates;
    private Task<VixTermStructureStore>? _vixTermLoadTask;
    private Task<string>? _vixTermExportTask;
    private CancellationTokenSource? _contangoLoadCts;
    private bool _contangoInitializationStarted;
    private bool _updatingOverviewSelection;
    private bool _overviewAtrpLayoutVisible;
    private GridLength _overviewCombinedHeight = new(2.0, GridUnitType.Star);
    private GridLength _overviewAtrpHeight = new(1.0, GridUnitType.Star);
    private MarketResearchSummary? _marketResearch;

    private BreadthChart[] AllCharts =>
    [
        Sp50Chart, Sp100Chart, Sp200Chart, Nasdaq100Chart,
        OverviewCombinedChart, ContangoSpChart, ContangoSvxyChart
    ];

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        List<DataSet> dataSets = WLHost.Instance.DataSets
            .Where(item => !item.IsHidden)
            .OrderBy(item => item.QualifiedName)
            .ToList();
        SpDataSet.ItemsSource = dataSets;
        NasdaqDataSet.ItemsSource = dataSets;

        string[] saved = _workspaceSelection.Split('|');
        SpDataSet.SelectedItem = FindDataSet(dataSets, saved.ElementAtOrDefault(0), "S&P 500");
        NasdaqDataSet.SelectedItem = FindDataSet(dataSets, saved.ElementAtOrDefault(1), "NASDAQ 100", "Nasdaq 100", "Nasdaq-100");
        UseWorkingZweig.IsChecked = string.Equals(saved.ElementAtOrDefault(2), "Working", StringComparison.OrdinalIgnoreCase);
        PositionProfile.SelectedIndex = int.TryParse(saved.ElementAtOrDefault(3), out int savedProfile)
            ? Math.Clamp(savedProfile, 0, 2)
            : 1;
        if (double.TryParse(saved.ElementAtOrDefault(4), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double savedSplitRatio))
        {
            savedSplitRatio = Math.Clamp(savedSplitRatio, 0.20, 0.65);
            FuzzyAnalysisColumn.Width = new GridLength(savedSplitRatio, GridUnitType.Star);
            FuzzyChartColumn.Width = new GridLength(1.0 - savedSplitRatio, GridUnitType.Star);
        }
        UpdateZweigModeText();
        SetStatus("Select Wealth-Lab dynamic DataSets, then calculate.");
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (SpDataSet.SelectedItem is not DataSet sp || NasdaqDataSet.SelectedItem is not DataSet nasdaq)
        {
            SetStatus("Select both DataSets.", BreadthProgressLevel.Error);
            return;
        }

        _calculationCts?.Cancel();
        _calculationCts?.Dispose();
        _calculationCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _calculationCts.Token;
        RefreshButton.IsEnabled = false;
        SpDataSet.IsEnabled = false;
        NasdaqDataSet.IsEnabled = false;
        _isCalculationInProgress = true;
        var progress = new Progress<BreadthProgress>(update => SetStatus(update.Message, update.Level));
        try
        {
            DateTime end = DateTime.Today;
            DateTime start = end.AddYears(-12);
            DateTime technicalWarmupStart = start.AddYears(-3);
            BreadthCalculationResult spResult = await Task.Run(() =>
                BreadthCalculator.Calculate(sp, [200, 100, 50], start, end, progress, cancellationToken),
                cancellationToken);
            IReadOnlyList<BreadthSeries> spSeries = spResult.Series;

            BreadthSeries sp200Series = spSeries.Single(series => series.Period == 200);
            BreadthSeries sp100Series = spSeries.Single(series => series.Period == 100);
            BreadthSeries sp50Series = spSeries.Single(series => series.Period == 50);
            SetStatus("S&P 500 breadth ready. Loading SPY and NYSE Zweig histories …");

            Task<PriceSeries> spyTask = Task.Run(() =>
                BreadthCalculator.LoadSp500Price(sp, technicalWarmupStart, end), cancellationToken);
            Task<PriceSeries> advancingTask = Task.Run(() =>
                BreadthCalculator.LoadPriceSeries(sp, "$NYSE_ADVN", technicalWarmupStart, end, "NYSE Advancing"),
                cancellationToken);
            Task<PriceSeries> decliningTask = Task.Run(() =>
                BreadthCalculator.LoadPriceSeries(sp, "$NYSE_DECLN", technicalWarmupStart, end, "NYSE Declining"),
                cancellationToken);
            Task<PriceSeries?> svxyTask = Task.Run<PriceSeries?>(() =>
            {
                try
                {
                    return BreadthCalculator.LoadPriceSeries(
                        sp, "SVXY", technicalWarmupStart, end, "SVXY");
                }
                catch
                {
                    return null;
                }
            }, cancellationToken);
            await Task.WhenAll(spyTask, advancingTask, decliningTask);
            PriceSeries sp500Price = await spyTask;
            PriceSeries? svxyPrice = await svxyTask;
            PriceSeries advancingSeries = await advancingTask;
            PriceSeries decliningSeries = await decliningTask;
            TechnicalIndicatorSeries technicalIndicators = TechnicalIndicatorCalculator.Calculate(sp500Price);
            ZweigSeries zweigStrongSeries = ZweigBreadthCalculator.Calculate(
                advancingSeries, decliningSeries, ZweigMode.Strong, start, end);
            ZweigSeries zweigWorkingSeries = ZweigBreadthCalculator.Calculate(
                advancingSeries, decliningSeries, ZweigMode.Working, start, end);

            SetStatus("S&P 500, SPY and Zweig ready. Loading Nasdaq-100 …");

            BreadthCalculationResult nasdaqResult = await Task.Run(() =>
                BreadthCalculator.Calculate(nasdaq, [100, 50], start, end, progress, cancellationToken),
                cancellationToken);
            IReadOnlyList<BreadthSeries> nasdaqSeries = nasdaqResult.Series;
            BreadthSeries nasdaq100Series = nasdaqSeries.Single(series => series.Period == 100);
            BreadthSeries nasdaq50Series = nasdaqSeries.Single(series => series.Period == 50);
            FuzzyMarketStateSeries fuzzyStates = FuzzyMarketStateCalculator.Calculate(
                sp500Price, sp50Series, sp100Series, sp200Series, nasdaq100Series, technicalIndicators);
            MarketResearchSummary marketResearch = BuildMarketResearchSummary(fuzzyStates, sp500Price, 20);

            SetStatus("Breadth and phase map ready. Loading SentimenTrader Titanic …");
            Task<PriceSeries> qqqTask = Task.Run(() =>
                BreadthCalculator.LoadPriceSeries(nasdaq, "QQQ", technicalWarmupStart, end, "Nasdaq 100 (QQQ)"),
                cancellationToken);
            Task<PriceSeries> newHighsTask = Task.Run(() =>
                BreadthCalculator.LoadPriceSeries(nasdaq, "$NASDAQ_NEWHI", technicalWarmupStart, end, "Nasdaq New Highs"),
                cancellationToken);
            Task<PriceSeries> newLowsTask = Task.Run(() =>
                BreadthCalculator.LoadPriceSeries(nasdaq, "$NASDAQ_NEWLO", technicalWarmupStart, end, "Nasdaq New Lows"),
                cancellationToken);
            await Task.WhenAll(qqqTask, newHighsTask, newLowsTask);
            TitanicSeries titanicSeries = TitanicSyndromeCalculator.Calculate(
                await qqqTask, await newHighsTask, await newLowsTask, nasdaq50Series, start, end);

            cancellationToken.ThrowIfCancellationRequested();
            _sp200Series = sp200Series;
            _sp100Series = sp100Series;
            _sp50Series = sp50Series;
            _nasdaq100Series = nasdaq100Series;
            _nasdaq50Series = nasdaq50Series;
            _sp500PriceSeries = sp500Price;
            _svxyPriceSeries = svxyPrice;
            _technicalIndicators = technicalIndicators;
            _marketResearch = marketResearch;
            _zweigStrongSeries = zweigStrongSeries;
            _zweigWorkingSeries = zweigWorkingSeries;
            _fuzzyStates = fuzzyStates;
            _titanicSeries = titanicSeries;

            Sp200Chart.SetSeries(sp200Series);
            ContangoSpChart.SetSeries(sp200Series);
            ContangoSpChart.SetTechnicalIndicators(technicalIndicators);
            ContangoSpChart.SetShowEmaOverlays(ContangoEma.IsChecked == true);
            ContangoSvxyChart.SetSeries(sp200Series);
            Sp100Chart.SetSeries(sp100Series);
            Sp50Chart.SetSeries(sp50Series);
            Nasdaq100Chart.SetSeries(nasdaq100Series);
            OverviewCombinedChart.SetTechnicalIndicators(technicalIndicators);
            OverviewAtrpChart.SetSeries(technicalIndicators);
            UpdateOverviewBreadthMode();
            UpdateContangoBreadthOverlays();
            SetFuzzyStateData(fuzzyStates);
            TitanicChart.SetData(titanicSeries);
            SetActiveZweigMode(exportConfidence: false);
            SetBreadthCandleMode(FullOhlc.IsChecked == true);
            SetPriceSeries(sp500Price);
            if (svxyPrice is not null)
                ContangoSvxyChart.SetPriceSeries(svxyPrice);
            else
                ContangoSvxyChart.SetMessage("SVXY", "SVXY history could not be loaded");
            ContangoMyRsiChart.SetSeries(technicalIndicators);
            ConfigureHistorySlider(sp200Series);

            var exportSnapshot = new ExportSnapshot(
                sp50Series, sp100Series, sp200Series, nasdaq100Series, sp500Price, svxyPrice,
                fuzzyStates, _holdConfidence!, technicalIndicators, titanicSeries,
                zweigStrongSeries, zweigWorkingSeries);
            string exportMessage = await Task.Run(
                () => ExportBreadthSeries(exportSnapshot, start, end), cancellationToken);
            int providerErrors = spResult.FailedHistoryCount + nasdaqResult.FailedHistoryCount;
            string historySummary =
                $"Histories used: S&P {spResult.LoadedHistoryCount}, Nasdaq {nasdaqResult.LoadedHistoryCount}.";
            SetStatus(
                $"Ready: {start:yyyy-MM-dd} — {end:yyyy-MM-dd}. {historySummary} {exportMessage}",
                providerErrors > 0 ? BreadthProgressLevel.Waiting : BreadthProgressLevel.Normal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the child window cancels queued work without displaying an obsolete error.
        }
        catch (Exception ex)
        {
            _calculationCts?.Cancel();
            SetStatus($"CALCULATION FAILED: {ex.Message}", BreadthProgressLevel.Error);
            WLHost.Instance.AddLogItem(
                "Breeze Wealth-Lab",
                $"Market breadth calculation failed: {ex}",
                WLColor.Red,
                ex,
                this);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            SpDataSet.IsEnabled = true;
            NasdaqDataSet.IsEnabled = true;
            _isCalculationInProgress = false;
            if (IsLoaded && _sp500PriceSeries is not null && !_contangoInitializationStarted)
                StartContangoInitialization();
        }
    }

    private void SetStatus(string message, BreadthProgressLevel level = BreadthProgressLevel.Normal)
    {
        StatusText.Text = message;
        StatusText.ToolTip = new ToolTip
        {
            MaxWidth = 720,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 690,
                Padding = new Thickness(4)
            }
        };
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Opacity = 1.0;
        StatusText.Foreground = level switch
        {
            BreadthProgressLevel.Waiting => new SolidColorBrush(Color.FromRgb(181, 103, 0)),
            BreadthProgressLevel.Error => Brushes.Red,
            _ => new SolidColorBrush(Color.FromRgb(35, 72, 52))
        };

        if (level == BreadthProgressLevel.Error)
        {
            StatusText.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 1.0,
                To = 0.28,
                Duration = TimeSpan.FromSeconds(0.7),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(4),
                FillBehavior = FillBehavior.Stop
            });
        }
    }

    private void ConfigureHistorySlider(BreadthSeries master)
    {
        _viewportMaster = master;
        _visibleBarCount = Math.Min(DefaultVisibleBarCount, Math.Max(1, master.Points.Count));
        int maximumStart = Math.Max(0, master.Points.Count - _visibleBarCount);
        HistorySlider.Maximum = maximumStart;
        HistorySlider.SmallChange = 10;
        HistorySlider.LargeChange = 100;
        HistorySlider.Value = maximumStart;
        ScheduleHistoryViewport();
    }

    private void DecreaseVisibleBarsClick(object sender, RoutedEventArgs e) =>
        ChangeVisibleBarCount(-VisibleBarStep);

    private void IncreaseVisibleBarsClick(object sender, RoutedEventArgs e) =>
        ChangeVisibleBarCount(VisibleBarStep);

    private void ChartsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (IsDescendantOf(source, FuzzyAnalysisPanel))
        {
            int wheelLines = SystemParameters.WheelScrollLines;
            double amount = wheelLines < 0
                ? Math.Max(24.0, FuzzyAnalysisScroll.ViewportHeight)
                : Math.Max(24.0, wheelLines * 16.0);
            double offset = FuzzyAnalysisScroll.VerticalOffset + (e.Delta > 0 ? -amount : amount);
            FuzzyAnalysisScroll.ScrollToVerticalOffset(Math.Clamp(
                offset, 0.0, Math.Max(0.0, FuzzyAnalysisScroll.ScrollableHeight)));
            e.Handled = true;
            return;
        }

        if (FindChartElement(source) is null)
            return;

        ZoomVisibleBarsAtCursor(e.Delta > 0 ? -VisibleBarStep : VisibleBarStep);
        e.Handled = true;
    }

    private void ChartsMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SetHorizontalDragCursor(true);
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null ||
            FindAncestor<System.Windows.Controls.Primitives.RangeBase>(source) is not null ||
            FindAncestor<ComboBox>(source) is not null || FindAncestor<CheckBox>(source) is not null)
            return;

        FrameworkElement? chart = FindChartElement(source);
        if (chart is null || _viewportMaster is null || _viewportMaster.Points.Count == 0)
            return;

        if (e.ClickCount >= 2)
        {
            _isPanning = false;
            if (BreadthTabs.IsMouseCaptured)
                BreadthTabs.ReleaseMouseCapture();
            SetCursorPinned(true);
            e.Handled = true;
            return;
        }

        if (_cursorPinned)
            SetCursorPinned(false);

        _isPanning = true;
        _panStartPoint = e.GetPosition(BreadthTabs);
        _panStartSlider = HistorySlider.Value;
        _panChartWidth = Math.Max(100.0, chart.ActualWidth);
        BreadthTabs.CaptureMouse();
        e.Handled = true;
    }

    private void ChartsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            SetHorizontalDragCursor(false);
        if (!_isPanning || _viewportMaster is null || !BreadthTabs.IsMouseCaptured)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _isPanning = false;
            BreadthTabs.ReleaseMouseCapture();
            SetHorizontalDragCursor(false);
            return;
        }

        Point current = e.GetPosition(BreadthTabs);
        double deltaX = current.X - _panStartPoint.X;
        int pointCount = _viewportMaster.Points.Count;
        int maximumStart = Math.Max(0, pointCount - _visibleBarCount);
        int deltaBars = (int)Math.Round(-deltaX / _panChartWidth * Math.Max(1, _visibleBarCount - 1));
        int newStart = Math.Clamp((int)Math.Round(_panStartSlider) + deltaBars, 0, maximumStart);
        HistorySlider.Maximum = maximumStart;
        if (Math.Abs(HistorySlider.Value - newStart) > 0.01)
            HistorySlider.Value = newStart;
        e.Handled = true;
    }

    private void ChartsMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool wasPanning = _isPanning;
        _isPanning = false;
        if (BreadthTabs.IsMouseCaptured)
            BreadthTabs.ReleaseMouseCapture();
        SetHorizontalDragCursor(false);
        if (wasPanning)
            e.Handled = true;
    }

    private void BreadthTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, BreadthTabs))
            return;

        object? selectedTab = BreadthTabs.SelectedItem;
        if (ReferenceEquals(selectedTab, ContangoTab) && !_contangoInitializationStarted)
            RequestContangoInitialization();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(selectedTab, FuzzyTab))
            {
                FuzzyChart.Refresh();
            }
            else if (ReferenceEquals(selectedTab, OverviewTab))
            {
                if (OverviewFull.IsChecked == true)
                {
                    Sp50Chart.Refresh();
                    Sp100Chart.Refresh();
                    Sp200Chart.Refresh();
                    Nasdaq100Chart.Refresh();
                }
                else
                {
                    OverviewCombinedChart.Refresh();
                    if (OverviewAtrp.IsChecked == true)
                        OverviewAtrpChart.Refresh();
                }
            }
            else if (ReferenceEquals(selectedTab, TitanicTab))
            {
                TitanicChart.Refresh();
            }
            else if (ReferenceEquals(selectedTab, ZweigTab))
            {
                ZweigChart.Refresh();
            }
            // Contango controls redraw once from IsVisibleChanged. Avoid a second
            // full render every time the user returns to that tab.
        }));
    }

    private void RequestContangoInitialization()
    {
        if (_contangoInitializationStarted)
            return;

        ContangoWaitBanner.Visibility = Visibility.Visible;
        if (_isCalculationInProgress)
        {
            ContangoWaitText.Text = "Wait…  Calculate 12 years is still running";
            return;
        }

        StartContangoInitialization();
    }

    private void StartContangoInitialization()
    {
        if (_contangoInitializationStarted || _isCalculationInProgress || !IsLoaded)
            return;

        _contangoInitializationStarted = true;
        ContangoWaitText.Text = "Wait…  Loading Contango";
        ContangoWaitBanner.Visibility = Visibility.Visible;
        _ = EnsureContangoLoadedAsync();
    }

    private async Task EnsureContangoLoadedAsync()
    {
        if (_vixTermStore is not null)
        {
            if (_contangoStates is null)
            {
                CancellationToken token = _contangoLoadCts?.Token ?? CancellationToken.None;
                _contangoStates = await Task.Run(() => _vixTermStore.BuildStateSeries(token), token);
                FuzzyChart.SetContangoStates(_contangoStates);
                RefreshHoldConfidence();
                ContangoSpChart.SetVixCurveStates(_contangoStates, showVixRibbon: false);
                ContangoSvxyChart.SetVixCurveStates(_contangoStates, showVixRibbon: true, showSvxyRibbon: true);
                if (_cursorDate.HasValue)
                    ApplyCursorValues(_cursorDate.Value);
            }
            UpdateContangoSnapshot();
            _ = EnsureContangoExportAsync();
            return;
        }

        if (_vixTermLoadTask is null)
        {
            _contangoLoadCts = new CancellationTokenSource();
            CancellationToken token = _contangoLoadCts.Token;
            ContangoChart.SetMessage($"Indexing {VixTermDataDirectory} …");
            _vixTermLoadTask = Task.Run(
                () => VixTermStructureStore.BuildIndex(VixTermDataDirectory, token), token);
        }

        try
        {
            _vixTermStore = await _vixTermLoadTask;
            string? vixWarning = null;
            if (SpDataSet.SelectedItem is DataSet spDataSet)
            {
                try
                {
                    CancellationToken token = _contangoLoadCts?.Token ?? CancellationToken.None;
                    PriceSeries vixSeries = await Task.Run(() => BreadthCalculator.LoadPriceSeries(
                        spDataSet,
                        "VIX",
                        _vixTermStore.FirstDate.AddDays(-7),
                        _vixTermStore.LastDate.AddDays(7),
                        "VIX Index"), token);
                    _vixPriceSeries = vixSeries;
                    _vixTermStore.SetSpotVix(vixSeries.Points.Select(point => (point.Date, point.Close)));
                    ContangoSvxyChart.SetLeftPriceOverlay(
                        _vixPriceSeries, ContangoShowVix?.IsChecked == true);
                }
                catch (OperationCanceledException) when (_contangoLoadCts?.IsCancellationRequested == true)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    vixWarning = $" Spot VIX unavailable: {ex.Message}";
                }
            }
            CancellationToken stateToken = _contangoLoadCts?.Token ?? CancellationToken.None;
            _contangoStates = await Task.Run(() => _vixTermStore.BuildStateSeries(stateToken), stateToken);
            FuzzyChart.SetContangoStates(_contangoStates);
            RefreshHoldConfidence();
            ContangoSpChart.SetVixCurveStates(_contangoStates, showVixRibbon: false);
            ContangoSvxyChart.SetVixCurveStates(_contangoStates, showVixRibbon: true, showSvxyRibbon: true);
            if (_cursorDate.HasValue)
                ApplyCursorValues(_cursorDate.Value);
            UpdateContangoSnapshot();
            SetStatus($"Contango ready: {_vixTermStore.Count:N0} VIX term files, " +
                      $"{_vixTermStore.FirstDate:yyyy-MM-dd} — {_vixTermStore.LastDate:yyyy-MM-dd}.{vixWarning}");
            _ = EnsureContangoExportAsync();
        }
        catch (OperationCanceledException)
        {
            // Window closing cancels lazy indexing.
            _contangoInitializationStarted = false;
        }
        catch (Exception ex)
        {
            _vixTermLoadTask = null;
            _contangoInitializationStarted = false;
            ContangoChart.SetMessage($"Unable to load VIX futures: {ex.Message}");
            SetStatus($"CONTANGO FAILED: {ex.Message}", BreadthProgressLevel.Error);
        }
        finally
        {
            ContangoWaitBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async Task EnsureContangoExportAsync()
    {
        if (_vixTermStore is null || _vixTermExportTask is not null)
            return;

        CancellationToken token = _contangoLoadCts?.Token ?? CancellationToken.None;
        FuzzyMarketStateSeries? fuzzyStates = _fuzzyStates;
        TitanicSeries? titanicSeries = _titanicSeries;
        ZweigSeries? zweigSeries = _zweigSeries;
        VixCurveStateSeries? curveStates = _contangoStates;
        HoldConfidenceSeries? holdConfidence = _holdConfidence;
        _vixTermExportTask = Task.Run(() =>
        {
            string wideResult = _vixTermStore.ExportWideCsv(VixTermExportPath, token);
            string stateResult = _vixTermStore.ExportStateCsv(VixCurveStateExportPath, token);
            string componentResult = fuzzyStates is not null && holdConfidence is not null &&
                                     titanicSeries is not null && zweigSeries is not null && curveStates is not null
                ? WritePhaseModelComponentsCsv(
                    PhaseModelComponentsExportPath, fuzzyStates, holdConfidence,
                    titanicSeries, zweigSeries, curveStates)
                : "phase components pending main calculation";
            return $"{wideResult}; {stateResult}; {componentResult}";
        }, token);
        try
        {
            string result = await _vixTermExportTask;
            // The shared research model reads the just-promoted snapshots.
            // Recalculate now so the DLL and standalone strategy use the same
            // current dates, not the previous wide VIX export.
            RefreshHoldConfidence();
            SetStatus($"Contango ready. {result}.");
        }
        catch (OperationCanceledException)
        {
            // Window closing cancels the background export.
        }
        catch (Exception ex)
        {
            _vixTermExportTask = null;
            SetStatus($"CONTANGO EXPORT FAILED: {ex.Message}", BreadthProgressLevel.Error);
        }
    }

    private void UpdateContangoSnapshot()
    {
        if (_vixTermStore is null)
            return;

        DateTime date = _cursorDate
            ?? _sp500PriceSeries?.Points.LastOrDefault()?.Date
            ?? _vixTermStore.LastDate;
        try
        {
            ContangoChart.SetSnapshot(_vixTermStore.GetNearest(date));
        }
        catch (Exception ex)
        {
            ContangoChart.SetMessage($"Unable to read VIX futures for {date:yyyy-MM-dd}: {ex.Message}");
        }
    }

    private void ShowSp500PriceChanged(object sender, RoutedEventArgs e) =>
        SetPriceVisibility(ShowSp500Price.IsChecked == true);

    private void ContangoBreadthChanged(object sender, RoutedEventArgs e) =>
        UpdateContangoBreadthOverlays();

    private void ContangoEmaChanged(object sender, RoutedEventArgs e)
    {
        if (ContangoSpChart is not null)
            ContangoSpChart.SetShowEmaOverlays(ContangoEma?.IsChecked == true);
    }

    private void ContangoShowVixChanged(object sender, RoutedEventArgs e)
    {
        if (ContangoSvxyChart is not null)
            ContangoSvxyChart.SetLeftPriceOverlay(
                _vixPriceSeries, ContangoShowVix?.IsChecked == true);
    }

    private void OverviewBreadthChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingOverviewSelection || OverviewFullGrid is null)
            return;

        _updatingOverviewSelection = true;
        try
        {
            if (ReferenceEquals(sender, OverviewFull) && OverviewFull.IsChecked == true)
            {
                OverviewSp50.IsChecked = false;
                OverviewSp100.IsChecked = false;
                OverviewSp200.IsChecked = false;
                OverviewNasdaq100.IsChecked = false;
            }
            else if (sender is CheckBox checkBox && !ReferenceEquals(checkBox, OverviewFull) &&
                     checkBox.IsChecked == true)
            {
                OverviewFull.IsChecked = false;
            }

            bool hasIndividualSelection = OverviewSp50.IsChecked == true ||
                                          OverviewSp100.IsChecked == true ||
                                          OverviewSp200.IsChecked == true ||
                                          OverviewNasdaq100.IsChecked == true;
            if (OverviewFull.IsChecked != true && !hasIndividualSelection)
                OverviewFull.IsChecked = true;
        }
        finally
        {
            _updatingOverviewSelection = false;
        }

        UpdateOverviewBreadthMode();
    }

    private void UpdateOverviewBreadthMode()
    {
        if (OverviewFullGrid is null || OverviewIndividualGrid is null || OverviewCombinedChart is null)
            return;

        bool showFull = OverviewFull.IsChecked == true;
        OverviewFullGrid.Visibility = showFull ? Visibility.Visible : Visibility.Collapsed;
        OverviewIndividualGrid.Visibility = showFull ? Visibility.Collapsed : Visibility.Visible;
        bool showAtrp = !showFull && OverviewAtrp?.IsChecked == true;
        OverviewAtrpChart.Visibility = showAtrp ? Visibility.Visible : Visibility.Collapsed;
        OverviewAtrpSplitter.Visibility = showAtrp ? Visibility.Visible : Visibility.Collapsed;
        if (showAtrp && !_overviewAtrpLayoutVisible)
        {
            OverviewCombinedRow.Height = _overviewCombinedHeight;
            OverviewAtrpSplitterRow.Height = new GridLength(5.0);
            OverviewAtrpRow.Height = _overviewAtrpHeight;
        }
        else if (!showAtrp && _overviewAtrpLayoutVisible)
        {
            _overviewCombinedHeight = OverviewCombinedRow.Height;
            _overviewAtrpHeight = OverviewAtrpRow.Height;
            OverviewCombinedRow.Height = new GridLength(1.0, GridUnitType.Star);
            OverviewAtrpSplitterRow.Height = new GridLength(0.0);
            OverviewAtrpRow.Height = new GridLength(0.0);
        }
        else if (!showAtrp)
        {
            OverviewCombinedRow.Height = new GridLength(1.0, GridUnitType.Star);
            OverviewAtrpSplitterRow.Height = new GridLength(0.0);
            OverviewAtrpRow.Height = new GridLength(0.0);
        }
        _overviewAtrpLayoutVisible = showAtrp;
        OverviewCombinedChart.SetShowEmaOverlays(!showFull && OverviewEma?.IsChecked == true);
        if (showFull)
        {
            OverviewCombinedChart.SetOverlayBreadthSeries();
            return;
        }

        var selected = new List<(BreadthSeries Series, Color Color, string Label)>();
        if (OverviewSp50.IsChecked == true && _sp50Series is not null)
            selected.Add((_sp50Series, Color.FromRgb(25, 118, 210), "S&P 500 SMA(50)"));
        if (OverviewSp100.IsChecked == true && _sp100Series is not null)
            selected.Add((_sp100Series, Color.FromRgb(46, 125, 50), "S&P 500 SMA(100)"));
        if (OverviewSp200.IsChecked == true && _sp200Series is not null)
            selected.Add((_sp200Series, Color.FromRgb(142, 68, 173), "S&P 500 SMA(200)"));
        if (OverviewNasdaq100.IsChecked == true && _nasdaq100Series is not null)
            selected.Add((_nasdaq100Series, Color.FromRgb(230, 126, 34), "Nasdaq 100 SMA(100)"));
        if (selected.Count == 0)
            return;

        (BreadthSeries primary, Color primaryColor, _) = selected[0];
        OverviewCombinedChart.SetSeries(primary);
        OverviewCombinedChart.SetTitle(selected.Count == 1
            ? selected[0].Label
            : string.Join(" + ", selected.Select(item => item.Label)));
        OverviewCombinedChart.SetPrimaryBreadthColor(
            selected.Count == 1 ? Color.FromRgb(25, 112, 174) : primaryColor);
        OverviewCombinedChart.SetOverlayBreadthSeries(selected
            .Skip(1)
            .Select(item => (item.Series, item.Color))
            .ToArray());
        OverviewCombinedChart.SetShowBreadthCandles(
            selected.Count == 1 && FullOhlc.IsChecked == true);
        OverviewCombinedChart.SetLogBreadthScale(OverviewLogBreadth.IsChecked == true);
    }

    private void OverviewIndicatorChanged(object sender, RoutedEventArgs e) =>
        UpdateOverviewBreadthMode();

    private void UpdateContangoBreadthOverlays()
    {
        if (ContangoSpChart is null)
            return;

        var overlays = new List<(BreadthSeries Series, Color Color)>();
        if (ContangoSp50?.IsChecked == true && _sp50Series is not null)
            overlays.Add((_sp50Series, Color.FromRgb(25, 118, 210)));
        if (ContangoSp100?.IsChecked == true && _sp100Series is not null)
            overlays.Add((_sp100Series, Color.FromRgb(46, 125, 50)));
        if (ContangoSp200?.IsChecked == true && _sp200Series is not null)
            overlays.Add((_sp200Series, Color.FromRgb(142, 68, 173)));
        if (ContangoNasdaq100?.IsChecked == true && _nasdaq100Series is not null)
            overlays.Add((_nasdaq100Series, Color.FromRgb(230, 126, 34)));
        ContangoSpChart.SetOverlayBreadthSeries(overlays.ToArray());
    }

    private void ShowSp500CandlesChanged(object sender, RoutedEventArgs e) =>
        SetPriceCandleMode(ShowSp500Candles.IsChecked == true);

    private void OverviewLogBreadthChanged(object sender, RoutedEventArgs e) =>
        SetLogBreadthMode(OverviewLogBreadth.IsChecked == true);

    private void FullOhlcChanged(object sender, RoutedEventArgs e) =>
        SetBreadthCandleMode(FullOhlc.IsChecked == true);

    private void ShowFuzzyZonesChanged(object sender, RoutedEventArgs e) =>
        SetFuzzyZoneVisibility(ShowFuzzyZones.IsChecked == true);

    private void HelpClick(object sender, RoutedEventArgs e)
    {
        if (BreezeHelpWindow.TryOpenExternal(out string fallbackReason))
            return;

        var help = new BreezeHelpWindow(fallbackReason) { Owner = Window.GetWindow(this) };
        help.Show();
    }

    private void SetPriceSeries(PriceSeries? series)
    {
        foreach (BreadthChart chart in AllCharts)
        {
            if (!ReferenceEquals(chart, ContangoSvxyChart))
                chart.SetPriceSeries(series);
        }
        FuzzyChart?.SetData(series, _fuzzyStates, _technicalIndicators);
        SetPriceVisibility(ShowSp500Price.IsChecked == true);
        SetPriceCandleMode(ShowSp500Candles.IsChecked == true);
    }

    private void SetPriceVisibility(bool show)
    {
        if (Sp50Chart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetShowPriceSeries(show);
    }

    private void SetPriceCandleMode(bool showCandles)
    {
        if (Sp50Chart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetShowPriceCandles(showCandles);
        FuzzyChart?.SetShowCandles(showCandles);
        TitanicChart?.SetShowCandles(showCandles);
        ZweigChart?.SetShowCandles(showCandles);
    }

    private void SetLogBreadthMode(bool useLogarithmicScale)
    {
        if (Sp50Chart is null)
            return;

        foreach (BreadthChart chart in new[]
                 { Sp50Chart, Sp100Chart, Sp200Chart, Nasdaq100Chart, OverviewCombinedChart })
            chart.SetLogBreadthScale(useLogarithmicScale);
    }

    private void SetBreadthCandleMode(bool showCandles)
    {
        if (Sp50Chart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetShowBreadthCandles(showCandles);
        UpdateOverviewBreadthMode();
    }

    private void SetFuzzyStateData(FuzzyMarketStateSeries? states)
    {
        foreach (BreadthChart chart in AllCharts)
            chart.SetMarketStates(states);
        FuzzyChart?.SetData(_sp500PriceSeries, states, _technicalIndicators);
        SetFuzzyZoneVisibility(ShowFuzzyZones?.IsChecked == true);
    }

    private void SetFuzzyZoneVisibility(bool show)
    {
        if (Sp50Chart is null)
            return;
        foreach (BreadthChart chart in AllCharts)
            chart.SetShowMarketStates(show);
    }

    private void ZweigModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateZweigModeText();
        if (_zweigStrongSeries is null || _zweigWorkingSeries is null)
            return;

        SetActiveZweigMode();
    }

    private void PositionProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fuzzyStates is null || _technicalIndicators is null || _zweigSeries is null)
            return;
        RefreshHoldConfidence();
    }

    private void SetActiveZweigMode(bool exportConfidence = true)
    {
        _zweigSeries = UseWorkingZweig.IsChecked == true ? _zweigWorkingSeries : _zweigStrongSeries;
        if (_zweigSeries is null)
            return;
        ZweigChart.SetData(_sp500PriceSeries, _zweigSeries);
        ZweigChart.SetShowCandles(ShowSp500Candles.IsChecked == true);
        UpdateZweigModeText();
        RefreshHoldConfidence(exportConfidence);
    }

    private void HoldVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (FuzzyChart is not null && FuzzyShowHold is not null)
            FuzzyChart.SetShowHoldConfidence(FuzzyShowHold.IsChecked == true);
        if (TitanicChart is not null && TitanicShowHold is not null)
            TitanicChart.SetShowHoldConfidence(TitanicShowHold.IsChecked == true);
        if (ZweigChart is not null && ZweigShowHold is not null)
            ZweigChart.SetShowHoldConfidence(ZweigShowHold.IsChecked == true);
    }

    private void RefreshHoldConfidence(bool export = true)
    {
        if (_fuzzyStates is null || _technicalIndicators is null || _zweigSeries is null)
            return;

        int profile = Math.Clamp(PositionProfile?.SelectedIndex ?? 1, 0, 2);
        if (!ResearchHoldConfidenceCalculator.TryCalculate(profile, out HoldConfidenceSeries research, out _))
        {
            // During the first calculation the CSV snapshot may not have been
            // promoted yet. Keep the UI usable; the research model is retried
            // immediately after the current VIX/breadth exports complete.
            _holdConfidence = HoldConfidenceCalculator.Calculate(
                _fuzzyStates,
                _technicalIndicators,
                _contangoStates,
                _titanicSeries,
                _zweigSeries);
        }
        else _holdConfidence = research;
        FuzzyChart.SetHoldConfidence(_holdConfidence);
        TitanicChart.SetHoldConfidence(_holdConfidence);
        ZweigChart.SetHoldConfidence(_holdConfidence);
        if (_cursorDate.HasValue)
            ApplyCursorValues(_cursorDate.Value);

        if (!export)
            return;
        int generation = Interlocked.Increment(ref _holdConfidenceExportGeneration);
        HoldConfidenceSeries snapshot = _holdConfidence;
        _ = ExportHoldConfidenceAsync(snapshot, generation);
    }

    private async Task ExportHoldConfidenceAsync(HoldConfidenceSeries series, int generation)
    {
        try
        {
            await Task.Run(() => ExportHoldConfidenceSnapshot(series, generation));
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _holdConfidenceExportGeneration))
                SetStatus($"HOLD CONFIDENCE EXPORT FAILED: {ex.Message}", BreadthProgressLevel.Error);
        }
    }

    private void UpdateZweigModeText()
    {
        if (ZweigModeText is null)
            return;
        ZweigModeText.Text = UseWorkingZweig.IsChecked == true ? "Current: Working" : "Current: Strong";
    }

    private void UpdateCursorValues(DateTime date)
    {
        if (_cursorPinned)
            return;
        _pendingCursorDate = date;
        if (_cursorUpdateScheduled)
            return;

        _cursorUpdateScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _cursorUpdateScheduled = false;
            if (_pendingCursorDate.HasValue)
                ApplyCursorValues(_pendingCursorDate.Value);
        }));
    }

    private void ApplyCursorValues(DateTime date)
    {
        _cursorDate = date;
        foreach (BreadthChart chart in AllCharts)
            chart.SetCursorDate(date);
        OverviewAtrpChart.SetCursorDate(date);
        ContangoMyRsiChart.SetCursorDate(date);
        FuzzyChart.SetCursorDate(date);
        TitanicChart.SetCursorDate(date);
        ZweigChart.SetCursorDate(date);
        UpdateContangoSnapshot();

        BreadthPoint? sp50 = FindPoint(_sp50Series, date);
        BreadthPoint? sp100 = FindPoint(_sp100Series, date);
        BreadthPoint? sp200 = FindPoint(_sp200Series, date);
        BreadthPoint? nasdaq100 = FindPoint(_nasdaq100Series, date);
        if (sp50 is null || sp100 is null || sp200 is null || nasdaq100 is null)
            return;

        FuzzyMarketStatePoint? fuzzy = FindPoint(_fuzzyStates, date);
        HoldConfidencePoint? hold = FindPoint(_holdConfidence, date);
        VixCurveStatePoint? curve = FindPoint(_contangoStates, date);
        string fuzzyText = fuzzy is null
            ? string.Empty
            : $"   Phase: {FuzzyStatePalette.Label(fuzzy.DominantZone)} {fuzzy.Confidence:P0} " +
              $"(SB {fuzzy.Buy:P0}, B {fuzzy.PrepareBuy:P0}, Warn {fuzzy.Caution:P0}, " +
              $"S {fuzzy.PrepareSell:P0}, SS {fuzzy.ReduceRisk:P0})" +
              (hold is null
                  ? string.Empty
                  : $"   Hold: {hold.Value:F0}% ({HoldConfidenceCalculator.Band(hold.Value)})");
        TechnicalIndicatorPoint? technical = FindPoint(_technicalIndicators, date);
        TitanicPoint? titanic = FindPoint(_titanicSeries, date);
        ZweigPoint? zweig = FindPoint(_zweigSeries, date);
        string titanicText = titanic is null
            ? string.Empty
            : $"   Titanic: {(titanic.Signal ? $"ACTIVE d{titanic.RunLength}" : "off")} " +
              $"NH {titanic.NasdaqNewHighs:F0}/NL {titanic.NasdaqNewLows:F0}";
        string zweigText = zweig is null
            ? string.Empty
            : $"   ZW Density: {zweig.Density}";
        string curveText = curve is null
            ? "   VIX curve: no data"
            : $"   VIX curve: {VixCurveStateClassifier.Label(curve.State)} " +
              $"30–60 {curve.Contango30To60Pct:+0.00;-0.00;0.00}% " +
              $"score {curve.CurveRegimeScore:+0.00;-0.00;0.00}";
        string svxyRecommendationText = curve?.SvxyRecommendation switch
        {
            SvxyRecommendation.StrongBuy => "   SVXY: Strong buy candidate d0",
            SvxyRecommendation.Buy => $"   SVXY: Buy / hold d{curve.SvxyBarsSinceCandidate}",
            SvxyRecommendation.ExtendedHold =>
                $"   SVXY: Extended hold d{curve.SvxyBarsSinceCandidate}",
            _ => "   SVXY: Neutral"
        };
        if (FuzzyAnalysisText is not null)
            FuzzyAnalysisText.Text = BuildMarketStateExplanation(
                date, fuzzy, hold, technical, curve, titanic, zweig, _marketResearch);
        UpdateOperationalPosition(date, hold, curve);
        CursorValuesText.Text =
            $"{date:yyyy-MM-dd}   S&P SMA50: {sp50.Value:F1}%   " +
            $"S&P SMA100: {sp100.Value:F1}%   S&P SMA200: {sp200.Value:F1}%   " +
            $"Nasdaq SMA100: {nasdaq100.Value:F1}%{fuzzyText}";
        CursorIndicatorsText.Text = technical is null
            ? "EMA and MyRSI_rep + AGC: no value"
            : $"EMA20: {technical.Ema20:F1}   EMA50: {technical.Ema50:F1}   " +
              $"EMA100: {technical.Ema100:F1}   EMA200: {technical.Ema200:F1}   " +
              $"MyRSI 14,8: {technical.MyRsiRep:F3}   MyRSI 10,8: {technical.MyRsiRep10:F3}   " +
              $"ATRP(1): {technical.Atrp1:F2}%" +
              $"{curveText}{svxyRecommendationText}{zweigText}{titanicText}";
    }

    private void UpdateOperationalPosition(
        DateTime date,
        HoldConfidencePoint? hold,
        VixCurveStatePoint? curve)
    {
        if (OperationalPositionTitle is null || OperationalPositionText is null ||
            OperationalPositionBorder is null)
            return;

        OperationalPositionTitle.Text = $"MODEL POSITION · {date:yyyy-MM-dd}";
        if (hold is null)
        {
            OperationalPositionText.Text = "Hold: no data\nTarget: no data\nAction: WAIT";
            OperationalPositionBorder.Background = new SolidColorBrush(Color.FromRgb(245, 247, 249));
            OperationalPositionBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(185, 200, 213));
            return;
        }

        int target = ModelPositionSizing.TargetContracts(hold.Value);
        HoldConfidencePoint? previous = FindPreviousPoint(_holdConfidence, hold.Date);
        int previousTarget = previous is null ? 0 : ModelPositionSizing.TargetContracts(previous.Value);
        int delta = target - previousTarget;
        string action = previous is null
            ? target == 0 ? "FLAT" : $"INITIAL {target}"
            : delta > 0
                ? $"ADD +{delta} → {target}"
                : delta < 0
                    ? target == 0 ? $"EXIT −{previousTarget}" : $"REDUCE {delta} → {target}"
                    : target == 0 ? "FLAT" : $"HOLD {target}";
        string yesterday = previous is null
            ? "no previous value"
            : $"{previous.Value:F1}% · {previousTarget}/10 ({previous.Date:yyyy-MM-dd})";
        string curveState = curve is null
            ? "no data"
            : VixCurveStateClassifier.Label(curve.State);
        string svxy = curve is null
            ? "no data"
            : curve.SvxyRecommendation switch
            {
                SvxyRecommendation.StrongBuy => "Strong buy candidate d0",
                SvxyRecommendation.Buy => $"Buy / hold d{curve.SvxyBarsSinceCandidate}",
                SvxyRecommendation.ExtendedHold => $"Extended hold d{curve.SvxyBarsSinceCandidate}",
                _ => "Neutral"
            };

        string model = hold.Profile == "Legacy" ? "Legacy fallback" : hold.Profile;
        OperationalPositionTitle.Text = $"MODEL POSITION · {model} · {date:yyyy-MM-dd}";
        OperationalPositionText.Text =
            $"Score: {hold.Value:F1}% · {HoldConfidenceCalculator.Band(hold.Value)}\n" +
            $"Target: {target}/10 contracts · fixed level {ModelPositionSizing.FixedLevel:F0}%\n" +
            $"Action: {action}\n" +
            $"Yesterday: {yesterday}\n" +
            $"Main support: {MainHoldSupport(hold)}\n" +
            $"Main risk: {MainHoldRisk(hold)}\n" +
            $"VIX curve: {curveState}\n" +
            $"SVXY: {svxy}";

        Color confidenceColor = FuzzyStateChart.ColorForHoldConfidence(hold.Value);
        OperationalPositionBorder.BorderBrush = new SolidColorBrush(confidenceColor);
        OperationalPositionBorder.Background = new SolidColorBrush(Color.FromArgb(
            24, confidenceColor.R, confidenceColor.G, confidenceColor.B));
    }

    private static string MainHoldSupport(HoldConfidencePoint hold)
    {
        if (!string.IsNullOrWhiteSpace(hold.MainSupport))
            return hold.MainSupport;
        (string Label, double Value)[] supports =
        [
            ("EMA trend", 0.30 * hold.TrendComponent),
            ("Breadth", 0.25 * hold.BreadthComponent),
            ("Phase", 0.20 * hold.PhaseComponent),
            ("Calm ATRP", 0.10 * hold.VolatilityComponent),
            ("Contango", 0.10 * hold.ContangoComponent),
            ("Zweig", 0.05 * hold.ZweigComponent)
        ];
        (string Label, double Value) strongest = supports.MaxBy(item => item.Value);
        return $"{strongest.Label} (+{strongest.Value * 100.0:F0} pp)";
    }

    private static string MainHoldRisk(HoldConfidencePoint hold)
    {
        if (!string.IsNullOrWhiteSpace(hold.MainRisk))
            return hold.MainRisk;
        (string Label, double Value)[] risks =
        [
            ("EMA weakness", 0.30 * (1.0 - hold.TrendComponent)),
            ("Weak breadth", 0.25 * (1.0 - hold.BreadthComponent)),
            ("Weak Phase", 0.20 * (1.0 - hold.PhaseComponent)),
            ("High ATRP", 0.10 * (1.0 - hold.VolatilityComponent)),
            ("VIX curve", 0.10 * (1.0 - hold.ContangoComponent)),
            ("Titanic", 0.20 * hold.TitanicPenalty),
            ("Shock / Reduce Risk", 0.25 * hold.ShockPenalty)
        ];
        (string Label, double Value) strongest = risks.MaxBy(item => item.Value);
        return strongest.Value < 0.015
            ? "No material pressure"
            : $"{strongest.Label} (−{strongest.Value * 100.0:F0} pp)";
    }

    private static string BuildMarketStateExplanation(
        DateTime date,
        FuzzyMarketStatePoint? fuzzy,
        HoldConfidencePoint? hold,
        TechnicalIndicatorPoint? technical,
        VixCurveStatePoint? curve,
        TitanicPoint? titanic,
        ZweigPoint? zweig,
        MarketResearchSummary? research)
    {
        if (fuzzy is null || technical is null)
            return $"{date:yyyy-MM-dd}\nНет полного набора данных для объяснения Phase Logic.";

        string conclusion = fuzzy.DominantZone switch
        {
            MarketZone.Buy => "Покупка подтверждена разворотом после глубокого снижения.",
            MarketZone.PrepareBuy => "Формируется ранний Buy, но подтверждение пока неполное.",
            MarketZone.Caution => "Режим неоднозначный: требуется осторожность без автоматического выхода.",
            MarketZone.PrepareSell => "Внутренняя широта и моментум ослабевают; риск снижения растёт.",
            MarketZone.ReduceRisk => "Сильный сигнал сокращения риска.",
            _ => "Доминирующего сигнала Buy или Sell сейчас нет."
        };
        string emaState = fuzzy.BullishEmaOrder >= 0.70
            ? "бычий порядок EMA выражен"
            : fuzzy.BullishEmaOrder <= 0.30
                ? "порядок EMA заметно нарушен"
                : "структура EMA смешанная";
        double ema20Distance = technical.Ema20 == 0.0
            ? 0.0
            : fuzzy.SpyClose / technical.Ema20 - 1.0;
        string slowRsiState = DescribeMyRsi(technical.MyRsiRep);
        string fastRsiState = DescribeMyRsi(technical.MyRsiRep10);
        string releaseText = fuzzy.MyRsiReleaseFromLow >= 0.35
            ? $" Выход из нижней зоны значим ({fuzzy.MyRsiReleaseFromLow:P0})."
            : string.Empty;
        string rsiMeaning = fuzzy.MyRsiReleaseFromLow >= 0.35
            ? "MyRSI значим: подтверждён выход вверх из нижней зоны."
            : fuzzy.StrongReduceRiskComplex >= 0.35
                ? "MyRSI значим: совместный разворот сверху подтверждает Reduce Risk."
                : technical.MyRsiRep >= 0.68 && technical.MyRsiRep10 >= 0.68 &&
                  fuzzy.BullishEmaOrder >= 0.70
                    ? "Оба MyRSI высоки, но при бычьем порядке EMA это растяжение, а не самостоятельный Sell."
                    : technical.MyRsiRep <= -0.68 && technical.MyRsiRep10 <= -0.68 &&
                      fuzzy.DeepFallContext >= 0.40
                        ? "Оба MyRSI низки в контексте падения: зона поиска дна, требуется разворот вверх."
                        : "MyRSI сейчас не создаёт самостоятельного сигнала.";
        string candleText = fuzzy.LargeRedDay >= 0.50
            ? $"Крупная красная свеча значима ({fuzzy.LargeRedDay:P0}); это усиливает риск."
            : $"Крупного красного импульса нет ({fuzzy.LargeRedDay:P0}).";
        string vixText = curve is null
            ? "VIX curve ещё не загружена."
            : $"VIX curve: {VixCurveStateClassifier.Label(curve.State)}, " +
              $"score {curve.CurveRegimeScore:+0.00;-0.00;0.00}, 30–60 " +
              $"{curve.Contango30To60Pct:+0.00;-0.00;0.00}%. " +
              DescribeSvxyRecommendation(curve);
        string titanicText = titanic is null
            ? "Titanic: нет данных."
            : titanic.Signal
                ? $"Titanic активен d{titanic.RunLength}" +
                  (titanic.Persistent ? ", устойчивый режим." : ", ранняя стадия.")
                : "Titanic выключен.";
        string zweigText = zweig is null
            ? "Zweig: нет данных."
            : zweig.Density > 0
                ? $"Zweig: поддержка позиции, плотность {zweig.Density}."
                : "Zweig: активного прикрытия нет.";

        var text = new StringBuilder();
        text.AppendLine($"{date:yyyy-MM-dd}   {FuzzyStatePalette.Label(fuzzy.DominantZone)} " +
                        $"({fuzzy.Confidence:P0})");
        if (hold is not null)
        {
            text.AppendLine($"Позиционная модель {hold.Profile}: score {hold.Value:F1}%; " +
                            $"цель {ModelPositionSizing.TargetContracts(hold.Value)}/10 по порогу " +
                            $"{ModelPositionSizing.FixedLevel:F0}%.");
            if (hold.Profile == "Legacy")
            {
                text.AppendLine($"Вклад: EMA {hold.TrendComponent:P0}, breadth {hold.BreadthComponent:P0}, " +
                                $"Phase {hold.PhaseComponent:P0}, спокойный ATRP {hold.VolatilityComponent:P0}, " +
                                $"Contango {hold.ContangoComponent:P0}, Zweig {hold.ZweigComponent:P0}; " +
                                $"штраф Titanic {hold.TitanicPenalty:P0}, шок {hold.ShockPenalty:P0}.");
            }
            else
            {
                text.AppendLine($"Главная поддержка: {MainHoldSupport(hold)}.");
                text.AppendLine($"Главный риск: {MainHoldRisk(hold)}.");
                text.AppendLine($"Активные события: {(string.IsNullOrWhiteSpace(hold.ActiveEvents) ? "нет ненулевого вклада" : hold.ActiveEvents)}.");
            }
        }
        text.AppendLine(conclusion);
        AppendResearchLine(text, "История этой фазы", research?.PhaseOutcomes.GetValueOrDefault(fuzzy.DominantZone),
            research?.HorizonBars ?? 20);
        text.AppendLine();
        text.AppendLine($"EMA: {emaState} ({fuzzy.BullishEmaOrder:P0}); " +
                        $"SPY к EMA20 {ema20Distance:+0.0%;-0.0%;0.0%}.");
        EmaResearchRegime emaRegime = ClassifyEmaRegime(fuzzy.BullishEmaOrder);
        AppendResearchLine(text, "История такого EMA-режима", research?.EmaOutcomes.GetValueOrDefault(emaRegime),
            research?.HorizonBars ?? 20);
        text.AppendLine($"MyRSI 14,8 {technical.MyRsiRep:+0.00;-0.00;0.00}: {slowRsiState}. " +
                        $"MyRSI 10,8 {technical.MyRsiRep10:+0.00;-0.00;0.00}: {fastRsiState}." +
                        releaseText);
        text.AppendLine(rsiMeaning);
        text.AppendLine($"Breadth: S&P 50/100/200 = {fuzzy.Sp50:F0}/{fuzzy.Sp100:F0}/{fuzzy.Sp200:F0}%, " +
                        $"Nasdaq 100 = {fuzzy.Nasdaq100:F0}%; сила разворота {fuzzy.BreadthTurnUp:P0}.");
        text.AppendLine(candleText);
        text.AppendLine();
        text.AppendLine(vixText);
        text.AppendLine(titanicText);
        text.Append(zweigText);
        return text.ToString();
    }

    private static MarketResearchSummary BuildMarketResearchSummary(
        FuzzyMarketStateSeries fuzzyStates,
        PriceSeries spy,
        int horizonBars)
    {
        var fuzzyByDate = fuzzyStates.Points.ToDictionary(point => point.Date);
        var phaseReturns = new Dictionary<MarketZone, List<double>>();
        var emaReturns = new Dictionary<EmaResearchRegime, List<double>>();
        for (int index = 0; index + horizonBars < spy.Points.Count; index++)
        {
            PricePoint current = spy.Points[index];
            if (current.Close <= 0.0 || !fuzzyByDate.TryGetValue(current.Date, out FuzzyMarketStatePoint? fuzzy))
                continue;
            double forwardReturn = spy.Points[index + horizonBars].Close / current.Close - 1.0;
            if (!double.IsFinite(forwardReturn))
                continue;

            if (!phaseReturns.TryGetValue(fuzzy.DominantZone, out List<double>? phaseValues))
                phaseReturns[fuzzy.DominantZone] = phaseValues = [];
            phaseValues.Add(forwardReturn);

            EmaResearchRegime emaRegime = ClassifyEmaRegime(fuzzy.BullishEmaOrder);
            if (!emaReturns.TryGetValue(emaRegime, out List<double>? emaValues))
                emaReturns[emaRegime] = emaValues = [];
            emaValues.Add(forwardReturn);
        }

        return new MarketResearchSummary(
            horizonBars,
            phaseReturns.ToDictionary(item => item.Key, item => SummarizeOutcomes(item.Value)),
            emaReturns.ToDictionary(item => item.Key, item => SummarizeOutcomes(item.Value)));
    }

    private static ForwardOutcomeStats SummarizeOutcomes(IReadOnlyList<double> values) =>
        new(values.Count, values.Count == 0 ? 0.0 : values.Count(value => value > 0.0) / (double)values.Count,
            values.Count == 0 ? 0.0 : values.Average());

    private static EmaResearchRegime ClassifyEmaRegime(double bullishEmaOrder) => bullishEmaOrder switch
    {
        >= 0.70 => EmaResearchRegime.Bullish,
        <= 0.30 => EmaResearchRegime.Damaged,
        _ => EmaResearchRegime.Mixed
    };

    private static void AppendResearchLine(
        StringBuilder text,
        string label,
        ForwardOutcomeStats? stats,
        int horizonBars)
    {
        if (stats is null || stats.Count == 0)
        {
            text.AppendLine($"{label}: выборка пока отсутствует.");
            return;
        }

        string sampleWarning = stats.Count < 30 ? "; малая выборка" : string.Empty;
        text.AppendLine($"{label}: через {horizonBars} баров SPY был выше в {stats.PositiveRate:P0}, " +
                        $"средняя доходность {stats.AverageReturn:+0.0%;-0.0%;0.0%} " +
                        $"(n={stats.Count}{sampleWarning}).");
    }

    private static string DescribeMyRsi(double value) => value switch
    {
        <= -0.68 => "нижняя зона",
        >= 0.68 => "верхняя зона",
        < -0.20 => "слабая отрицательная область",
        > 0.20 => "положительная область",
        _ => "нейтральная область"
    };

    private static string DescribeSvxyRecommendation(VixCurveStatePoint curve) =>
        curve.SvxyRecommendation switch
        {
            SvxyRecommendation.StrongBuy => "SVXY: новый Strong Buy.",
            SvxyRecommendation.Buy => $"SVXY: Buy/Hold d{curve.SvxyBarsSinceCandidate}.",
            SvxyRecommendation.ExtendedHold =>
                $"SVXY: Extended Hold d{curve.SvxyBarsSinceCandidate}.",
            _ => "SVXY: Neutral."
        };

    private async void TitanicPersistentDaysChanged(int days)
    {
        if (_titanicSeries is null || _isCalculationInProgress)
            return;
        _titanicSeries = TitanicSyndromeCalculator.WithPersistentDays(_titanicSeries, days);
        RefreshHoldConfidence();
        int generation = Interlocked.Increment(ref _titanicExportGeneration);
        try
        {
            TitanicSeries snapshot = _titanicSeries;
            await Task.Run(() => ExportTitanicSnapshot(snapshot, generation));
            if (generation == Volatile.Read(ref _titanicExportGeneration))
                SetStatus($"Titanic Persistent Days changed to {days}; CSV updated.");
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _titanicExportGeneration))
                SetStatus($"Titanic threshold changed to {days}; CSV update failed: {ex.Message}", BreadthProgressLevel.Error);
        }
    }

    private string ExportBreadthSeries(ExportSnapshot snapshot, DateTime startDate, DateTime endDate)
    {
        ExportGate.Wait();
        string? stagingDirectory = null;
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            stagingDirectory = Path.Combine(ExportDirectory, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            WriteBreadthCsv(Path.Combine(stagingDirectory, "sp500_sma50.csv"), snapshot.Sp50);
            WriteBreadthCsv(Path.Combine(stagingDirectory, "sp500_sma100.csv"), snapshot.Sp100);
            WriteBreadthCsv(Path.Combine(stagingDirectory, "sp500_sma200.csv"), snapshot.Sp200);
            WriteBreadthCsv(Path.Combine(stagingDirectory, "nasdaq100_sma100.csv"), snapshot.Nasdaq100);
            WritePriceCsv(Path.Combine(stagingDirectory, "SPY.csv"), snapshot.Sp500Price, startDate, endDate);
            if (snapshot.SvxyPrice is not null)
                WritePriceCsv(Path.Combine(stagingDirectory, "SVXY.csv"), snapshot.SvxyPrice, startDate, endDate);
            WriteFuzzyCsv(Path.Combine(stagingDirectory, "fuzzy_market_states.csv"), snapshot.FuzzyStates);
            WriteHoldConfidenceCsv(
                Path.Combine(stagingDirectory, "hold_confidence.csv"), snapshot.HoldConfidence);
            WriteTechnicalIndicatorsCsv(
                Path.Combine(stagingDirectory, "spy_ema_myrsi_agc.csv"),
                snapshot.TechnicalIndicators,
                startDate,
                endDate);
            WriteTechnicalOhlcvFiles(
                stagingDirectory,
                snapshot.TechnicalIndicators,
                snapshot.Sp500Price,
                startDate,
                endDate);
            WriteTitanicCsv(Path.Combine(stagingDirectory, "titanic_sentiment.csv"), snapshot.Titanic, false);
            WriteTitanicCsv(Path.Combine(stagingDirectory, "titanic_sentiment_events.csv"), snapshot.Titanic, true);
            WriteZweigCsv(
                Path.Combine(stagingDirectory, "zweig_breadth_thrust.csv"),
                snapshot.ZweigStrong,
                snapshot.ZweigWorking);
            PromoteStagedFiles(stagingDirectory, ExportDirectory);
            string svxyStatus = snapshot.SvxyPrice is null
                ? " SVXY history was unavailable, so SVXY.csv was not written."
                : " SVXY OHLCV saved to SVXY.csv.";
            return $"CSV, phase states, Titanic, Zweig density and both MyRSI/AGC series saved.{svxyStatus}";
        }
        catch (Exception ex)
        {
            WLHost.Instance.AddLogItem(
                "Breeze",
                $"Breadth CSV export failed: {ex}",
                WLColor.Orange,
                ex,
                this);
            throw;
        }
        finally
        {
            TryDeleteStagingDirectory(stagingDirectory);
            ExportGate.Release();
        }
    }

    private void ExportTitanicSnapshot(TitanicSeries series, int generation)
    {
        ExportGate.Wait();
        string? stagingDirectory = null;
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            stagingDirectory = Path.Combine(ExportDirectory, $".staging-titanic-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            WriteTitanicCsv(Path.Combine(stagingDirectory, "titanic_sentiment.csv"), series, false);
            WriteTitanicCsv(Path.Combine(stagingDirectory, "titanic_sentiment_events.csv"), series, true);
            if (generation == Volatile.Read(ref _titanicExportGeneration))
                PromoteStagedFiles(stagingDirectory, ExportDirectory);
        }
        finally
        {
            TryDeleteStagingDirectory(stagingDirectory);
            ExportGate.Release();
        }
    }

    private void ExportHoldConfidenceSnapshot(HoldConfidenceSeries series, int generation)
    {
        ExportGate.Wait();
        string? stagingDirectory = null;
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            stagingDirectory = Path.Combine(ExportDirectory, $".staging-hold-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            WriteHoldConfidenceCsv(Path.Combine(stagingDirectory, "hold_confidence.csv"), series);
            if (generation == Volatile.Read(ref _holdConfidenceExportGeneration))
                PromoteStagedFiles(stagingDirectory, ExportDirectory);
        }
        finally
        {
            TryDeleteStagingDirectory(stagingDirectory);
            ExportGate.Release();
        }
    }

    private static void TryDeleteStagingDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // A stale staging directory is harmless and can be removed on the next maintenance pass.
        }
    }

    private static void PromoteStagedFiles(string stagingDirectory, string destinationDirectory)
    {
        foreach (string sourcePath in Directory.EnumerateFiles(stagingDirectory, "*.csv"))
        {
            string destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            File.Move(sourcePath, destinationPath, true);
        }
    }

    private static void WriteBreadthCsv(
        string path,
        BreadthSeries? series)
    {
        if (series is null)
            throw new InvalidOperationException("Breadth series has not been calculated.");

        var csv = new StringBuilder();
        csv.AppendLine("Date,Open,High,Low,Close,Volume,EligibleCount");
        foreach (BreadthPoint point in series.Points)
        {
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(point.Open.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.High.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Low.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Close.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append("0,")
                .AppendLine(point.EligibleCount.ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void WritePriceCsv(string path, PriceSeries series, DateTime startDate, DateTime endDate)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,Open,High,Low,Close,Volume");
        foreach (PricePoint point in series.Points.Where(point => point.Date >= startDate && point.Date <= endDate))
        {
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Open.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.High.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Low.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Close.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .AppendLine(point.Volume.ToString("0", CultureInfo.InvariantCulture));
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void WriteFuzzyCsv(string path, FuzzyMarketStateSeries series)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,Zone,Confidence,Score,StrongBuy,Buy,Neutral,Warning,Sell,StrongSell,SPY,Drawdown,SP50,SP100,SP200,Nasdaq100,EMA20,EMA50,EMA100,EMA200,MyRSI_rep_AGC,BullishEMAOrder,BreadthTurnUp,DeepFallContext,MyRSIReleaseFromLow,EarlyBuyComplex,StrongReduceRiskComplex,LargeRedDay,Rationale");
        foreach (FuzzyMarketStatePoint point in series.Points)
        {
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(FuzzyStatePalette.Label(point.DominantZone)).Append(',')
                .Append(point.Confidence.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Score.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Buy.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.PrepareBuy.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Neutral.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Caution.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.PrepareSell.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.ReduceRisk.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.SpyClose.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Drawdown.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Sp50.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Sp100.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Sp200.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Nasdaq100.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Ema20.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Ema50.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Ema100.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Ema200.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.MyRsiRep.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.BullishEmaOrder.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.BreadthTurnUp.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.DeepFallContext.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.MyRsiReleaseFromLow.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.EarlyBuyComplex.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.StrongReduceRiskComplex.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.LargeRedDay.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append('"').Append(point.Rationale.Replace("\"", "\"\"")).AppendLine("\"");
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void WriteHoldConfidenceCsv(string path, HoldConfidenceSeries series)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,HoldConfidencePct,Band,Trend,Breadth,Phase,VolatilityCalm,Contango,ZweigSupport,TitanicPenalty,ShockPenalty,Rationale,Profile,MainSupport,MainRisk,ActiveEvents");
        foreach (HoldConfidencePoint point in series.Points)
        {
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Value.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(HoldConfidenceCalculator.Band(point.Value)).Append(',')
                .Append(point.TrendComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.BreadthComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.PhaseComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.VolatilityComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.ContangoComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.ZweigComponent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.TitanicPenalty.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.ShockPenalty.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append('"').Append(point.Rationale.Replace("\"", "\"\"")).Append("\",")
                .Append('"').Append(point.Profile.Replace("\"", "\"\"")).Append("\",")
                .Append('"').Append(point.MainSupport.Replace("\"", "\"\"")).Append("\",")
                .Append('"').Append(point.MainRisk.Replace("\"", "\"\"")).Append("\",")
                .Append('"').Append(point.ActiveEvents.Replace("\"", "\"\"")).AppendLine("\"");
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void WriteTechnicalIndicatorsCsv(
        string path,
        TechnicalIndicatorSeries series,
        DateTime startDate,
        DateTime endDate)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,EMA20,EMA50,EMA100,EMA200,MyRSI_rep_AGC_14_8,MyRSI_rep_AGC_10_8,ATRP_1,SmoothPeriod,AGCHalfLife");
        foreach (TechnicalIndicatorPoint point in series.Points.Where(point => point.Date >= startDate && point.Date <= endDate))
        {
            ValidateTechnicalPoint(point);
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Ema20.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Ema50.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Ema100.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Ema200.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.MyRsiRep.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.MyRsiRep10.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Atrp1.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(series.MyRsiSmoothPeriod.ToString(CultureInfo.InvariantCulture)).Append(',')
                .AppendLine(series.AgcHalfLife.ToString(CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static string WritePhaseModelComponentsCsv(
        string path,
        FuzzyMarketStateSeries fuzzyStates,
        HoldConfidenceSeries holdConfidence,
        TitanicSeries titanicSeries,
        ZweigSeries zweigSeries,
        VixCurveStateSeries curveStates)
    {
        var titanicByDate = titanicSeries.Points.ToDictionary(point => point.Date);
        var zweigByDate = zweigSeries.Points.ToDictionary(point => point.Date);
        var curveByDate = curveStates.Points.ToDictionary(point => point.Date);
        var holdByDate = holdConfidence.Points.ToDictionary(point => point.Date);
        var csv = new StringBuilder();
        csv.AppendLine("Date,Phase,Confidence,Score,HoldConfidencePct,HoldBand,HoldTrend,HoldBreadth,HoldPhase,HoldVolatilityCalm,HoldContango,HoldZweigSupport,HoldTitanicPenalty,HoldShockPenalty,StrongBuy,Buy,Neutral,Warning,Sell,StrongSell,BullishEMAOrder,BreadthTurnUp,DeepFallContext,MyRSIReleaseFromLow,EarlyBuyComplex,StrongReduceRiskComplex,LargeRedDay,VIXCurveState,Contango30To60Pct,RisingPairShare,CurveSlope30DayPct,CurveRegimeScore,SVXYBuyCandidate,SVXYRecommendation,SVXYBarsSinceCandidate,TitanicSignal,TitanicPersistent,TitanicRunLength,ZweigDensity,ZweigSignal");
        foreach (FuzzyMarketStatePoint point in fuzzyStates.Points)
        {
            titanicByDate.TryGetValue(point.Date, out TitanicPoint? titanic);
            zweigByDate.TryGetValue(point.Date, out ZweigPoint? zweig);
            curveByDate.TryGetValue(point.Date, out VixCurveStatePoint? curve);
            holdByDate.TryGetValue(point.Date, out HoldConfidencePoint? hold);
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(FuzzyStatePalette.Label(point.DominantZone)).Append(',')
                .Append(point.Confidence.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Score.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(hold?.Value.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(hold is null ? string.Empty : HoldConfidenceCalculator.Band(hold.Value)).Append(',')
                .Append(hold?.TrendComponent.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(hold?.BreadthComponent.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(hold?.PhaseComponent.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(hold?.VolatilityComponent.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(hold?.ContangoComponent.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(hold?.ZweigComponent.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(hold?.TitanicPenalty.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(hold?.ShockPenalty.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(point.Buy.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.PrepareBuy.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Neutral.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Caution.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.PrepareSell.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.ReduceRisk.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.BullishEmaOrder.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.BreadthTurnUp.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.DeepFallContext.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.MyRsiReleaseFromLow.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.EarlyBuyComplex.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.StrongReduceRiskComplex.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.LargeRedDay.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(curve is null ? "No data" : VixCurveStateClassifier.Label(curve.State)).Append(',')
                .Append(curve?.Contango30To60Pct.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(curve?.RisingPairShare.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(curve?.CurveSlope30DayPct.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(curve?.CurveRegimeScore.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(curve?.SvxyBuyCandidate == true ? "1" : "0").Append(',')
                .Append(curve?.SvxyRecommendation.ToString() ?? SvxyRecommendation.Neutral.ToString()).Append(',')
                .Append(curve?.SvxyBarsSinceCandidate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(titanic?.Signal == true ? "1" : "0").Append(',')
                .Append(titanic?.Persistent == true ? "1" : "0").Append(',')
                .Append(titanic?.RunLength.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(zweig?.Density.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .AppendLine(zweig?.Signal == true ? "1" : "0");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, csv.ToString(), new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
        return $"{Path.GetFileName(path)}: {fuzzyStates.Points.Count:N0} explainability rows";
    }

    private static void WriteTechnicalOhlcvFiles(
        string directory,
        TechnicalIndicatorSeries series,
        PriceSeries spy,
        DateTime startDate,
        DateTime endDate)
    {
        var volumeByDate = spy.Points.ToDictionary(point => point.Date, point => point.Volume);
        IReadOnlyList<TechnicalIndicatorPoint> points = series.Points
            .Where(point => point.Date >= startDate && point.Date <= endDate)
            .ToList();
        foreach (TechnicalIndicatorPoint point in points)
            ValidateTechnicalPoint(point);

        WriteSingleIndicatorCsv(Path.Combine(directory, "spy_ema20.csv"), points, point => point.Ema20, volumeByDate);
        WriteSingleIndicatorCsv(Path.Combine(directory, "spy_ema50.csv"), points, point => point.Ema50, volumeByDate);
        WriteSingleIndicatorCsv(Path.Combine(directory, "spy_ema100.csv"), points, point => point.Ema100, volumeByDate);
        WriteSingleIndicatorCsv(Path.Combine(directory, "spy_ema200.csv"), points, point => point.Ema200, volumeByDate);
        WriteSingleIndicatorCsv(Path.Combine(directory, "spy_myrsi_rep_agc.csv"), points, point => point.MyRsiRep, volumeByDate);
        WriteSingleIndicatorCsv(Path.Combine(directory, "spy_myrsi_rep_agc_10_8.csv"), points, point => point.MyRsiRep10, volumeByDate);
        WriteSingleIndicatorCsv(Path.Combine(directory, "spy_atrp1.csv"), points, point => point.Atrp1, volumeByDate);
    }

    private static void WriteSingleIndicatorCsv(
        string path,
        IReadOnlyList<TechnicalIndicatorPoint> points,
        Func<TechnicalIndicatorPoint, double> selector,
        IReadOnlyDictionary<DateTime, double> volumeByDate)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,Open,High,Low,Close,Volume");
        foreach (TechnicalIndicatorPoint point in points)
        {
            string value = selector(point).ToString("0.########", CultureInfo.InvariantCulture);
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(value).Append(',').Append(value).Append(',').Append(value).Append(',').Append(value).Append(',')
                .AppendLine(volumeByDate.GetValueOrDefault(point.Date, 0.0).ToString("0", CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void ValidateTechnicalPoint(TechnicalIndicatorPoint point)
    {
        if (!double.IsFinite(point.Ema20) || !double.IsFinite(point.Ema50) ||
            !double.IsFinite(point.Ema100) || !double.IsFinite(point.Ema200) ||
            !double.IsFinite(point.MyRsiRep) || !double.IsFinite(point.MyRsiRep10) ||
            !double.IsFinite(point.Atrp1))
            throw new InvalidOperationException($"Technical indicator is not finite on {point.Date:yyyy-MM-dd}.");
        if (point.MyRsiRep < -1.000000001 || point.MyRsiRep > 1.000000001)
            throw new InvalidOperationException($"MyRSI_rep + AGC is outside [-1,+1] on {point.Date:yyyy-MM-dd}.");
        if (point.MyRsiRep10 < -1.000000001 || point.MyRsiRep10 > 1.000000001)
            throw new InvalidOperationException($"MyRSI_rep 10,8 + AGC is outside [-1,+1] on {point.Date:yyyy-MM-dd}.");
        if (point.Atrp1 < 0.0)
            throw new InvalidOperationException($"ATRP(1) is negative on {point.Date:yyyy-MM-dd}.");
    }

    private static void WriteTitanicCsv(string path, TitanicSeries series, bool eventsOnly)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,Signal,RunLength,Persistent,QQQOpen,QQQHigh,QQQLow,QQQClose,AnnualHigh,RecentlyAtHigh,NasdaqNewHighs,NasdaqNewLows,LowsExceedHighs,Nasdaq100AboveSMA50,ValidBreadthMembers,BreadthFilterPassed,Lookback,RecentHighWindow,HighTolerancePercent,PersistentDays,UseBreadthFilter,BreadthThreshold");
        foreach (TitanicPoint point in series.Points.Where(point => !eventsOnly || point.Signal))
        {
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Signal ? "1" : "0").Append(',')
                .Append(point.RunLength).Append(',')
                .Append(point.Persistent ? "1" : "0").Append(',')
                .Append(point.QqqOpen.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.QqqHigh.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.QqqLow.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.QqqClose.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.AnnualHigh.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.RecentlyAtHigh ? "1" : "0").Append(',')
                .Append(point.NasdaqNewHighs.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.NasdaqNewLows.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.LowsExceedHighs ? "1" : "0").Append(',')
                .Append(point.NasdaqBreadth50.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.ValidBreadthMembers).Append(',')
                .Append(point.BreadthFilterPassed ? "1" : "0").Append(',')
                .Append(series.AnnualHighLookback).Append(',')
                .Append(series.RecentHighWindow).Append(',')
                .Append(series.HighTolerancePercent.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(series.PersistentDays).Append(',')
                .Append(series.UseBreadthFilter ? "1" : "0").Append(',')
                .AppendLine(series.BreadthThreshold.ToString("0.####", CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static void WriteZweigCsv(string path, params ZweigSeries[] seriesList)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,ZweigDensity,Mode,BarsSinceSignal,SignalDate,Signal,ClassicSignal");
        foreach (ZweigSeries series in seriesList)
        {
            foreach (ZweigPoint point in series.Points)
            {
                csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.Density.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(series.Mode).Append(',')
                    .Append(point.BarsSinceSignal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                    .Append(point.SignalDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                    .Append(point.Signal ? "1" : "0").Append(',')
                    .AppendLine(point.ClassicSignal ? "1" : "0");
            }
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static BreadthPoint? FindPoint(BreadthSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;
        return FindNearest(series.Points, date, point => point.Date);
    }

    private static FuzzyMarketStatePoint? FindPoint(FuzzyMarketStateSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;
        return FindNearest(series.Points, date, point => point.Date);
    }

    private static HoldConfidencePoint? FindPoint(HoldConfidenceSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;
        return FindNearest(series.Points, date, point => point.Date);
    }

    private static HoldConfidencePoint? FindPreviousPoint(HoldConfidenceSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;

        int low = 0;
        int high = series.Points.Count - 1;
        int previousIndex = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (series.Points[middle].Date < date)
            {
                previousIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return previousIndex >= 0 ? series.Points[previousIndex] : null;
    }

    private static TechnicalIndicatorPoint? FindPoint(TechnicalIndicatorSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;
        return FindNearest(series.Points, date, point => point.Date);
    }

    private static TitanicPoint? FindPoint(TitanicSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;
        return FindNearest(series.Points, date, point => point.Date);
    }

    private static ZweigPoint? FindPoint(ZweigSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;
        return FindNearest(series.Points, date, point => point.Date);
    }

    private static VixCurveStatePoint? FindPoint(VixCurveStateSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;
        return FindNearest(series.Points, date, point => point.Date);
    }

    private static T FindNearest<T>(IReadOnlyList<T> points, DateTime date, Func<T, DateTime> dateSelector)
    {
        int low = 0;
        int high = points.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            DateTime middleDate = dateSelector(points[middle]);
            if (middleDate == date)
                return points[middle];
            if (middleDate < date)
                low = middle + 1;
            else
                high = middle - 1;
        }

        if (low <= 0)
            return points[0];
        if (low >= points.Count)
            return points[^1];
        long before = Math.Abs((dateSelector(points[low - 1]) - date).Ticks);
        long after = Math.Abs((dateSelector(points[low]) - date).Ticks);
        return before <= after ? points[low - 1] : points[low];
    }

    private void ChangeVisibleBarCount(int delta)
    {
        if (_viewportMaster is null || _viewportMaster.Points.Count == 0)
            return;

        int pointCount = _viewportMaster.Points.Count;
        int oldStart = Math.Clamp((int)Math.Round(HistorySlider.Value), 0, Math.Max(0, pointCount - 1));
        int oldEnd = Math.Min(pointCount - 1, oldStart + _visibleBarCount - 1);
        int minimum = Math.Min(MinimumVisibleBarCount, pointCount);
        int newCount = Math.Clamp(_visibleBarCount + delta, minimum, pointCount);
        if (newCount == _visibleBarCount)
            return;

        _visibleBarCount = newCount;
        int maximumStart = Math.Max(0, pointCount - _visibleBarCount);
        int newStart = Math.Clamp(oldEnd - _visibleBarCount + 1, 0, maximumStart);
        HistorySlider.Maximum = maximumStart;
        double oldSliderValue = HistorySlider.Value;
        HistorySlider.Value = newStart;
        if (Math.Abs(oldSliderValue - newStart) < 0.01)
            ApplyHistoryViewport();
    }

    private void ZoomVisibleBarsAtCursor(int delta)
    {
        if (_viewportMaster is null || _viewportMaster.Points.Count == 0)
            return;

        int pointCount = _viewportMaster.Points.Count;
        int currentStart = Math.Clamp((int)Math.Round(HistorySlider.Value), 0, Math.Max(0, pointCount - 1));
        int currentEnd = Math.Min(pointCount - 1, currentStart + _visibleBarCount - 1);
        int newCount = Math.Clamp(_visibleBarCount + delta, Math.Min(MinimumVisibleBarCount, pointCount), pointCount);
        if (newCount == _visibleBarCount)
            return;

        double anchorFraction = 0.5;
        if (_cursorDate.HasValue && currentEnd > currentStart)
        {
            int anchorIndex = Enumerable.Range(currentStart, currentEnd - currentStart + 1)
                .MinBy(index => Math.Abs((_viewportMaster.Points[index].Date - _cursorDate.Value).Ticks));
            anchorFraction = Math.Clamp((anchorIndex - currentStart) / (double)(currentEnd - currentStart), 0.0, 1.0);
        }

        int maximumStart = Math.Max(0, pointCount - newCount);
        int anchorIndexForViewport = currentStart + (int)Math.Round(anchorFraction * Math.Max(0, currentEnd - currentStart));
        int newStart = (int)Math.Round(anchorIndexForViewport - anchorFraction * Math.Max(0, newCount - 1));
        newStart = Math.Clamp(newStart, 0, maximumStart);
        _visibleBarCount = newCount;
        HistorySlider.Maximum = maximumStart;
        double oldSliderValue = HistorySlider.Value;
        HistorySlider.Value = newStart;
        if (Math.Abs(oldSliderValue - newStart) < 0.01)
            ApplyHistoryViewport();
    }

    private static FrameworkElement? FindChartElement(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is BreadthChart || current is FuzzyStateChart ||
                current is TitanicChart || current is ZweigChart || current is MyRsiChart ||
                current is AtrpChart)
                return current as FrameworkElement;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        }
        return null;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        }
        return false;
    }

    private void SetHorizontalDragCursor(bool active)
    {
        if (active == _horizontalDragCursorActive)
            return;
        _horizontalDragCursorActive = active;
        if (active)
        {
            _previousOverrideCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.SizeWE;
        }
        else
        {
            Mouse.OverrideCursor = _previousOverrideCursor;
            _previousOverrideCursor = null;
        }
    }

    private void SetCursorPinned(bool pinned)
    {
        if (_cursorPinned == pinned)
            return;

        if (pinned && _pendingCursorDate.HasValue)
        {
            DateTime pending = _pendingCursorDate.Value;
            _pendingCursorDate = null;
            ApplyCursorValues(pending);
        }
        _cursorPinned = pinned;
        foreach (BreadthChart chart in AllCharts)
            chart.SetCursorPinned(pinned);
        OverviewAtrpChart.SetCursorPinned(pinned);
        ContangoMyRsiChart.SetCursorPinned(pinned);
        FuzzyChart.SetCursorPinned(pinned);
        TitanicChart.SetCursorPinned(pinned);
        ZweigChart.SetCursorPinned(pinned);
    }

    private void HistorySliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ScheduleHistoryViewport();

    private void ScheduleHistoryViewport()
    {
        if (_viewportUpdateScheduled)
            return;
        _viewportUpdateScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _viewportUpdateScheduled = false;
            ApplyHistoryViewport();
        }));
    }

    private void ApplyHistoryViewport()
    {
        if (_viewportMaster is null || _viewportMaster.Points.Count == 0)
            return;

        int startIndex = Math.Clamp((int)Math.Round(HistorySlider.Value), 0, Math.Max(0, _viewportMaster.Points.Count - 1));
        int endIndex = Math.Min(_viewportMaster.Points.Count - 1, startIndex + _visibleBarCount - 1);
        DateTime start = _viewportMaster.Points[startIndex].Date;
        DateTime end = _viewportMaster.Points[endIndex].Date;

        Sp200Chart.SetViewport(start, end);
        Sp100Chart.SetViewport(start, end);
        Sp50Chart.SetViewport(start, end);
        Nasdaq100Chart.SetViewport(start, end);
        OverviewCombinedChart.SetViewport(start, end);
        OverviewAtrpChart.SetViewport(start, end);
        FuzzyChart.SetViewport(start, end);
        TitanicChart.SetViewport(start, end);
        ZweigChart.SetViewport(start, end);
        ContangoSpChart.SetViewport(start, end);
        ContangoSvxyChart.SetViewport(start, end);
        ContangoMyRsiChart.SetViewport(start, end);
        VisibleBarsText.Text = $"{_visibleBarCount} bars";
        VisibleRangeText.Text = $"{start:yyyy-MM-dd} — {end:yyyy-MM-dd}";
    }

    private void SetPlaceholders()
    {
        Sp200Chart.SetMessage("S&P 500 — above SMA(200)", "Waiting for calculation");
        Sp100Chart.SetMessage("S&P 500 — above SMA(100)", "Waiting for calculation");
        Sp50Chart.SetMessage("S&P 500 — above SMA(50)", "Waiting for calculation");
        Nasdaq100Chart.SetMessage("Nasdaq-100 — above SMA(100)", "Waiting for calculation");
        OverviewCombinedChart.SetMessage("Market breadth", "Waiting for calculation");
        OverviewAtrpChart.SetMessage("Waiting for calculation");
        FuzzyChart.SetMessage("Fuzzy market-state map", "Waiting for calculation");
        OperationalPositionTitle.Text = "MODEL POSITION";
        OperationalPositionText.Text = "Waiting for calculation";
        FuzzyAnalysisText.Text = "Calculate the data, then move the cursor over the chart.";
        TitanicChart.SetMessage("SentimenTrader Titanic", "Waiting for calculation");
        ZweigChart.SetMessage("Zweig Breadth Thrust", "Waiting for calculation");
        ContangoSpChart.SetMessage("S&P 500 — above SMA(200)", "Waiting for calculation");
        ContangoSvxyChart.SetMessage("SVXY", "Waiting for calculation");
        ContangoMyRsiChart.SetMessage("Waiting for calculation");
        ContangoChart.SetMessage("Open the Contango tab to load VIX futures data");
    }

    private static DataSet? FindDataSet(IEnumerable<DataSet> dataSets, string? exact, params string[] hints)
    {
        DataSet? exactMatch = dataSets.FirstOrDefault(item =>
            string.Equals(item.QualifiedName, exact, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
            return exactMatch;

        foreach (string hint in hints)
        {
            DataSet? match = dataSets.FirstOrDefault(item =>
                string.Equals(item.Name, hint, StringComparison.OrdinalIgnoreCase));
            match ??= dataSets.FirstOrDefault(item =>
                item.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }
}
