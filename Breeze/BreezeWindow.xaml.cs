using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private const int MinimumVisibleBarCount = 20;
    private const int VisibleBarStep = 20;
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
        LoadPersistentLayout();
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
        TradingEnsembleChart.CursorDateChanged += UpdateCursorValues;
        TitanicChart.PersistentDaysChanged += TitanicPersistentDaysChanged;
        BreadthTabs.SelectionChanged += BreadthTabsSelectionChanged;
        BreadthTabs.LostMouseCapture += (_, _) =>
        {
            _isPanning = false;
            bool saveLayout = _isTradingPaneResizing;
            _isTradingPaneResizing = false;
            TradingEnsembleChart.EndPaneResize();
            if (saveLayout)
            {
                CaptureTradingLayout();
                SavePersistentLayout();
            }
            SetHorizontalDragCursor(false);
        };
        Unloaded += (_, _) =>
        {
            SavePersistentLayout();
            SetHorizontalDragCursor(false);
            _calculationCts?.Cancel();
            _contangoLoadCts?.Cancel();
            _tradingEnsembleCts?.Cancel();
        };
        SetPlaceholders();
        UpdateOverviewBreadthMode();
    }

    private void OrderTabsByAnalysisPriority()
    {
        TabItem[] orderedTabs = [FuzzyTab, TradingEnsemblesTab, OverviewTab, ContangoTab, TitanicTab, ZweigTab];
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
            return $"{(SpDataSet.SelectedItem as DataSet)?.QualifiedName}|" +
                   $"{(NasdaqDataSet.SelectedItem as DataSet)?.QualifiedName}|" +
                   $"{(UseWorkingZweig.IsChecked == true ? "Working" : "Strong")}|" +
                   $"{PositionProfile?.SelectedIndex ?? 1}|" +
                   $"{_layoutState.FuzzyAnalysisRatio.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   $"{_layoutState.TradingPriceBoundary.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   $"{_layoutState.TradingEnsembleBoundary.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   $"{_layoutState.TradingDecileBoundary.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   $"{_layoutState.OverviewCombinedRatio.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   $"{_layoutState.ContangoLeftRatio.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   _layoutState.ContangoTopRatio.ToString("0.####", CultureInfo.InvariantCulture);
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
    private BarHistory? _sp500Bars;
    private PriceSeries? _svxyPriceSeries;
    private PriceSeries? _vixPriceSeries;
    private FuzzyMarketStateSeries? _fuzzyStates;
    private HoldConfidenceSeries? _holdConfidence;
    private TechnicalIndicatorSeries? _technicalIndicators;
    private TitanicSeries? _titanicSeries;
    private ZweigSeries? _zweigSeries;
    private ZweigSeries? _zweigStrongSeries;
    private ZweigSeries? _zweigWorkingSeries;
    private IReadOnlyDictionary<string, TradingEnsembleSeries> _tradingEnsembles =
        new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _tradingEnsembleCts;
    private bool _tradingEnsembleCalculationStarted;
    private int _tradingEnsembleCalculationGeneration;
    private bool _initialCalculationStarted;
    private int _tradingForecastDays = 10;
    private bool _tradingDirectoryTreeInitialized;
    private static readonly object DirectoryTreePlaceholder = new();
    private int _visibleBarCount = DefaultVisibleBarCount;
    private DateTime? _cursorDate;
    private bool _cursorPinned;
    private bool _horizontalDragCursorActive;
    private Cursor? _previousOverrideCursor;
    private bool _isPanning;
    private bool _isTradingPaneResizing;
    private bool _persistentLayoutLoaded;
    private BreezeLayoutState _layoutState = new();
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

    private void LoadPersistentLayout()
    {
        if (!BreezeLayoutStore.TryLoad(out BreezeLayoutState state))
            return;

        _layoutState = state;
        _persistentLayoutLoaded = true;
        double fuzzyRatio = Math.Clamp(state.FuzzyAnalysisRatio, 0.20, 0.65);
        FuzzyAnalysisColumn.Width = new GridLength(fuzzyRatio, GridUnitType.Star);
        FuzzyChartColumn.Width = new GridLength(1.0 - fuzzyRatio, GridUnitType.Star);
        TradingEnsembleChart.SetPaneBoundaries(
            state.TradingPriceBoundary,
            state.TradingEnsembleBoundary,
            state.TradingDecileBoundary);
        ApplyOverviewRatio(state.OverviewCombinedRatio);
        ApplyStarRatio(ContangoLeftColumn, ContangoRightColumn,
            state.ContangoLeftRatio, 0.20, 0.80);
        ApplyStarRatio(ContangoTopRow, ContangoBottomRow,
            state.ContangoTopRatio, 0.20, 0.85);
    }

    private void SavePersistentLayout()
    {
        if (BreezeLayoutStore.TrySave(_layoutState))
            _persistentLayoutLoaded = true;
    }

    private void CaptureTradingLayout()
    {
        (double price, double ensemble, double decile) = TradingEnsembleChart.GetPaneBoundaries();
        _layoutState.TradingPriceBoundary = price;
        _layoutState.TradingEnsembleBoundary = ensemble;
        _layoutState.TradingDecileBoundary = decile;
    }

    private void LayoutSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ReferenceEquals(sender, OverviewAtrpSplitter))
        {
            double ratio = Ratio(
                OverviewCombinedRow.ActualHeight, OverviewAtrpRow.ActualHeight, 2.0 / 3.0);
            ApplyOverviewRatio(ratio);
            _layoutState.OverviewCombinedRatio = ratio;
        }
        else if (ReferenceEquals(sender, FuzzyHorizontalSplitter))
            _layoutState.FuzzyAnalysisRatio = Ratio(
                FuzzyAnalysisColumn.ActualWidth, FuzzyChartColumn.ActualWidth,
                _layoutState.FuzzyAnalysisRatio);
        else if (ReferenceEquals(sender, ContangoVerticalSplitter))
            _layoutState.ContangoLeftRatio = Ratio(
                ContangoLeftColumn.ActualWidth, ContangoRightColumn.ActualWidth,
                _layoutState.ContangoLeftRatio);
        else if (ReferenceEquals(sender, ContangoLeftHorizontalSplitter) ||
                 ReferenceEquals(sender, ContangoRightHorizontalSplitter))
            _layoutState.ContangoTopRatio = Ratio(
                ContangoTopRow.ActualHeight, ContangoBottomRow.ActualHeight,
                _layoutState.ContangoTopRatio);
        SavePersistentLayout();
    }

    private double CurrentOverviewRatio()
    {
        if (_overviewAtrpLayoutVisible && OverviewCombinedRow.ActualHeight + OverviewAtrpRow.ActualHeight > 1.0)
            return Ratio(OverviewCombinedRow.ActualHeight, OverviewAtrpRow.ActualHeight, 2.0 / 3.0);
        double first = _overviewCombinedHeight.Value;
        double second = _overviewAtrpHeight.Value;
        return first + second > 0.0 ? Math.Clamp(first / (first + second), 0.20, 0.85) : 2.0 / 3.0;
    }

    private void ApplyOverviewRatio(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.20, 0.85);
        _overviewCombinedHeight = new GridLength(ratio, GridUnitType.Star);
        _overviewAtrpHeight = new GridLength(1.0 - ratio, GridUnitType.Star);
        if (_overviewAtrpLayoutVisible)
        {
            OverviewCombinedRow.Height = _overviewCombinedHeight;
            OverviewAtrpRow.Height = _overviewAtrpHeight;
        }
    }

    private static void ApplyStarRatio(ColumnDefinition first, ColumnDefinition second,
        double ratio, double minimum, double maximum)
    {
        ratio = Math.Clamp(ratio, minimum, maximum);
        first.Width = new GridLength(ratio, GridUnitType.Star);
        second.Width = new GridLength(1.0 - ratio, GridUnitType.Star);
    }

    private static void ApplyStarRatio(RowDefinition first, RowDefinition second,
        double ratio, double minimum, double maximum)
    {
        ratio = Math.Clamp(ratio, minimum, maximum);
        first.Height = new GridLength(ratio, GridUnitType.Star);
        second.Height = new GridLength(1.0 - ratio, GridUnitType.Star);
    }

    private static double Ratio(double first, double second, double fallback)
    {
        double total = first + second;
        return double.IsFinite(total) && total > 1.0
            ? Math.Clamp(first / total, 0.0, 1.0)
            : fallback;
    }

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
        if (!_persistentLayoutLoaded && double.TryParse(saved.ElementAtOrDefault(4), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double savedSplitRatio))
        {
            savedSplitRatio = Math.Clamp(savedSplitRatio, 0.20, 0.65);
            _layoutState.FuzzyAnalysisRatio = savedSplitRatio;
            FuzzyAnalysisColumn.Width = new GridLength(savedSplitRatio, GridUnitType.Star);
            FuzzyChartColumn.Width = new GridLength(1.0 - savedSplitRatio, GridUnitType.Star);
        }
        if (!_persistentLayoutLoaded &&
            double.TryParse(saved.ElementAtOrDefault(5), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedPriceBoundary) &&
            double.TryParse(saved.ElementAtOrDefault(6), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedEnsembleBoundary) &&
            double.TryParse(saved.ElementAtOrDefault(7), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedDecileBoundary))
        {
            _layoutState.TradingPriceBoundary = savedPriceBoundary;
            _layoutState.TradingEnsembleBoundary = savedEnsembleBoundary;
            _layoutState.TradingDecileBoundary = savedDecileBoundary;
            TradingEnsembleChart.SetPaneBoundaries(
                savedPriceBoundary, savedEnsembleBoundary, savedDecileBoundary);
        }
        if (!_persistentLayoutLoaded &&
            double.TryParse(saved.ElementAtOrDefault(8), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedOverviewRatio))
        {
            _layoutState.OverviewCombinedRatio = savedOverviewRatio;
            ApplyOverviewRatio(savedOverviewRatio);
        }
        if (!_persistentLayoutLoaded &&
            double.TryParse(saved.ElementAtOrDefault(9), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedContangoLeftRatio))
        {
            _layoutState.ContangoLeftRatio = savedContangoLeftRatio;
            ApplyStarRatio(ContangoLeftColumn, ContangoRightColumn, savedContangoLeftRatio, 0.20, 0.80);
        }
        if (!_persistentLayoutLoaded &&
            double.TryParse(saved.ElementAtOrDefault(10), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedContangoTopRatio))
        {
            _layoutState.ContangoTopRatio = savedContangoTopRatio;
            ApplyStarRatio(ContangoTopRow, ContangoBottomRow, savedContangoTopRatio, 0.20, 0.85);
        }
        UpdateZweigModeText();
        TradingEnsembleDirectory.Text = @"D:\TorchSharp4GPU";
        SetStatus("Ожидайте — запускается первоначальный расчёт данных …", BreadthProgressLevel.Waiting);
        if (!_initialCalculationStarted)
        {
            _initialCalculationStarted = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => RefreshClick(RefreshButton, new RoutedEventArgs())));
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_persistentLayoutLoaded)
                    SavePersistentLayout();
            }));
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
        RefreshButton.Content = "Calculating…";
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

            Task<BarHistory> spyTask = Task.Run(() =>
                BreadthCalculator.LoadRawHistory(sp, "SPY", technicalWarmupStart, end), cancellationToken);
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
            BarHistory sp500Bars = await spyTask;
            PriceSeries sp500Price = BreadthCalculator.ToPriceSeries(sp500Bars, "S&P 500 (SPY)");
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
            _sp500Bars = sp500Bars;
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
            TradingEnsembleChart.SetData(_tradingEnsembles, sp500Price, technicalIndicators);
            ApplyTradingEnsembleOptions();
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
            RefreshButton.Content = "Recalculate";
            SpDataSet.IsEnabled = true;
            NasdaqDataSet.IsEnabled = true;
            _isCalculationInProgress = false;
            if (IsLoaded && _sp500PriceSeries is not null && !_contangoInitializationStarted)
                StartContangoInitialization();
            if (ReferenceEquals(BreadthTabs.SelectedItem, TradingEnsemblesTab))
                StartTradingEnsembleCalculationIfNeeded();
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

    private void SetTradingCalculationStatus(string message,
        BreadthProgressLevel level = BreadthProgressLevel.Normal)
    {
        if (TradingCalculationStatus is null)
            return;
        TradingCalculationStatus.Text = message;
        TradingCalculationStatus.ToolTip = message;
        TradingCalculationStatus.Foreground = level switch
        {
            BreadthProgressLevel.Waiting => new SolidColorBrush(Color.FromRgb(181, 103, 0)),
            BreadthProgressLevel.Error => Brushes.Red,
            _ => new SolidColorBrush(Color.FromRgb(20, 112, 68))
        };
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
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null ||
            FindAncestor<System.Windows.Controls.Primitives.RangeBase>(source) is not null ||
            FindAncestor<ComboBox>(source) is not null || FindAncestor<CheckBox>(source) is not null ||
            FindAncestor<TextBox>(source) is not null || FindAncestor<TreeView>(source) is not null)
            return;

        if (IsDescendantOf(source, TradingEnsembleChart) &&
            TradingEnsembleChart.TryBeginPaneResize(e.GetPosition(TradingEnsembleChart)))
        {
            _isTradingPaneResizing = true;
            BreadthTabs.CaptureMouse();
            e.Handled = true;
            return;
        }

        FrameworkElement? chart = FindChartElement(source);
        if (chart is null || _viewportMaster is null || _viewportMaster.Points.Count == 0)
            return;

        SetHorizontalDragCursor(true);

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
        if (_isTradingPaneResizing)
        {
            if (e.LeftButton == MouseButtonState.Pressed && BreadthTabs.IsMouseCaptured)
            {
                TradingEnsembleChart.ResizePane(e.GetPosition(TradingEnsembleChart));
                e.Handled = true;
                return;
            }
            _isTradingPaneResizing = false;
            TradingEnsembleChart.EndPaneResize();
            CaptureTradingLayout();
            SavePersistentLayout();
            if (BreadthTabs.IsMouseCaptured)
                BreadthTabs.ReleaseMouseCapture();
        }
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
        if (_isTradingPaneResizing)
        {
            _isTradingPaneResizing = false;
            TradingEnsembleChart.EndPaneResize();
            CaptureTradingLayout();
            SavePersistentLayout();
            if (BreadthTabs.IsMouseCaptured)
                BreadthTabs.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
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
        UpdateBottomNavigationForSelectedTab();
        if (ReferenceEquals(selectedTab, ContangoTab) && !_contangoInitializationStarted)
            RequestContangoInitialization();
        if (ReferenceEquals(selectedTab, TradingEnsemblesTab))
            StartTradingEnsembleCalculationIfNeeded();

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
            else if (ReferenceEquals(selectedTab, TradingEnsemblesTab))
            {
                TradingEnsembleChart.Refresh();
            }
            else if (ReferenceEquals(selectedTab, ZweigTab))
            {
                ZweigChart.Refresh();
            }
            // Contango controls redraw once from IsVisibleChanged. Avoid a second
            // full render every time the user returns to that tab.
        }));
    }

    private void UpdateBottomNavigationForSelectedTab()
    {
        bool tradingEnsemble = ReferenceEquals(BreadthTabs.SelectedItem, TradingEnsemblesTab);
        Visibility detailVisibility = tradingEnsemble ? Visibility.Collapsed : Visibility.Visible;
        CursorValuesText.Visibility = detailVisibility;
        CursorIndicatorsText.Visibility = detailVisibility;
        VisibleBarsText.Visibility = Visibility.Visible;
    }

    private void RequestContangoInitialization()
    {
        if (_contangoInitializationStarted)
            return;

        ContangoWaitBanner.Visibility = Visibility.Visible;
        if (_isCalculationInProgress)
        {
            ContangoWaitText.Text = "Wait…  Recalculation is still running";
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

    private async void StartTradingEnsembleCalculationIfNeeded()
    {
        if (_tradingEnsembleCalculationStarted)
            return;
        if (_sp500Bars is null)
        {
            TradingEnsembleChart.SetMessage("Дождитесь завершения общего расчёта. Расчёт ZZ1, ZZ2 и ZZ3 начнётся при открытой вкладке.");
            SetTradingCalculationStatus("Ожидайте — подготовка данных…", BreadthProgressLevel.Waiting);
            return;
        }

        _tradingEnsembleCalculationStarted = true;
        int generation = ++_tradingEnsembleCalculationGeneration;
        _tradingEnsembleCts?.Cancel();
        _tradingEnsembleCts?.Dispose();
        _tradingEnsembleCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _tradingEnsembleCts.Token;
        string directory = TradingEnsembleDirectory.Text?.Trim() ?? @"D:\TorchSharp4GPU";
        var progress = new Progress<BreadthProgress>(update =>
        {
            if (generation != _tradingEnsembleCalculationGeneration)
                return;
            SetStatus(update.Message, update.Level);
            SetTradingCalculationStatus(update.Message, update.Level);
        });
        TradingEnsembleChart.SetMessage("Расчёт ZZ1, ZZ2 и ZZ3 из готовых JBIN/INI-пакетов …");
        SetTradingCalculationStatus(
            _tradingForecastDays == 0
                ? "Ожидайте — расчёт ансамблей…"
                : $"Ожидайте — Forecast {_tradingForecastDays}…",
            BreadthProgressLevel.Waiting);
        try
        {
            IReadOnlyDictionary<string, TradingEnsembleSeries> result = await Task.Run(() =>
                TradingEnsembleInference.Calculate(
                    _sp500Bars, directory, _tradingForecastDays, progress, cancellationToken),
                cancellationToken);
            if (generation != _tradingEnsembleCalculationGeneration || cancellationToken.IsCancellationRequested)
                return;
            _tradingEnsembles = result;
            TradingEnsembleChart.SetData(_tradingEnsembles, _sp500PriceSeries, _technicalIndicators);
            ApplyTradingEnsembleOptions();
            SetStatus("Торговые ансамбли ZZ1, ZZ2 и ZZ3 рассчитаны напрямую из JBIN/​INI-пакетов.");
            SetTradingCalculationStatus(
                _tradingForecastDays == 0
                    ? "Ансамбли рассчитаны"
                    : $"Forecast {_tradingForecastDays}: готово");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _tradingEnsembleCalculationGeneration)
                SetTradingCalculationStatus("Расчёт отменён", BreadthProgressLevel.Waiting);
        }
        catch (Exception ex)
        {
            if (generation != _tradingEnsembleCalculationGeneration)
                return;
            TradingEnsembleChart.SetMessage(ex.Message);
            SetStatus($"Расчёт торговых ансамблей не выполнен: {ex.Message}", BreadthProgressLevel.Error);
            SetTradingCalculationStatus("Ошибка Forecast — см. сообщение", BreadthProgressLevel.Error);
        }
    }

    private void OpenTradingDirectoryTreeClick(object sender, RoutedEventArgs e)
    {
        EnsureTradingDirectoryTree();
        TradingDirectoryPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => SelectDirectoryTreePath(TradingEnsembleDirectory.Text)));
    }

    private void EnsureTradingDirectoryTree()
    {
        if (_tradingDirectoryTreeInitialized)
            return;
        _tradingDirectoryTreeInitialized = true;
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            TreeViewItem item = CreateDirectoryTreeItem(drive.RootDirectory.FullName, drive.Name);
            TradingDirectoryTree.Items.Add(item);
        }
    }

    private TreeViewItem CreateDirectoryTreeItem(string path, string? header = null)
    {
        var item = new TreeViewItem
        {
            Header = header ?? new DirectoryInfo(path).Name,
            Tag = path
        };
        item.Items.Add(DirectoryTreePlaceholder);
        item.Expanded += DirectoryTreeItemExpanded;
        return item;
    }

    private void DirectoryTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string path ||
            item.Items.Count != 1 || !ReferenceEquals(item.Items[0], DirectoryTreePlaceholder))
            return;
        item.Items.Clear();
        try
        {
            foreach (string directory in Directory.GetDirectories(path).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        item.Items.Add(CreateDirectoryTreeItem(directory));
                }
                catch { }
            }
        }
        catch { }
        e.Handled = true;
    }

    private void SelectDirectoryTreePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return; }
        string rootPath = Path.GetPathRoot(fullPath) ?? string.Empty;
        TreeViewItem? current = TradingDirectoryTree.Items.OfType<TreeViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, rootPath, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            return;
        current.IsExpanded = true;
        string remainder = fullPath[rootPath.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string partial = rootPath;
        foreach (string segment in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            partial = Path.Combine(partial, segment);
            TreeViewItem? next = current.Items.OfType<TreeViewItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, partial, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                break;
            current = next;
            current.IsExpanded = true;
        }
        current.IsSelected = true;
        current.BringIntoView();
    }

    private void SelectTradingDirectoryClick(object sender, RoutedEventArgs e) =>
        ApplySelectedTradingDirectory();

    private void TradingDirectoryTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplySelectedTradingDirectory();
        e.Handled = true;
    }

    private void CloseTradingDirectoryTreeClick(object sender, RoutedEventArgs e) =>
        TradingDirectoryPopup.IsOpen = false;

    private void ApplySelectedTradingDirectory()
    {
        if (TradingDirectoryTree.SelectedItem is not TreeViewItem item || item.Tag is not string path)
            return;
        TradingEnsembleDirectory.Text = path;
        TradingDirectoryPopup.IsOpen = false;
        _tradingEnsembleCalculationGeneration++;
        _tradingEnsembleCts?.Cancel();
        _tradingEnsembleCalculationStarted = false;
        _tradingEnsembles = new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
        if (ReferenceEquals(BreadthTabs.SelectedItem, TradingEnsemblesTab))
            StartTradingEnsembleCalculationIfNeeded();
    }

    private void TradingEnsembleOptionsChanged(object sender, RoutedEventArgs e) =>
        ApplyTradingEnsembleOptions();

    private void DecreaseTradingForecastClick(object sender, RoutedEventArgs e) =>
        SetTradingForecastDays(_tradingForecastDays - 5);

    private void IncreaseTradingForecastClick(object sender, RoutedEventArgs e) =>
        SetTradingForecastDays(_tradingForecastDays + 5);

    private void SetTradingForecastDays(int value)
    {
        int normalized = Math.Clamp(value, 0, 100);
        if (normalized == _tradingForecastDays)
            return;

        _tradingForecastDays = normalized;
        TradingForecastValue.Text = normalized.ToString(CultureInfo.InvariantCulture);
        _tradingEnsembleCalculationGeneration++;
        _tradingEnsembleCts?.Cancel();
        _tradingEnsembleCalculationStarted = false;
        _tradingEnsembles = new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
        TradingEnsembleChart.SetMessage(normalized == 0
            ? "Forecast отключён. Пересчёт основных ансамблей …"
            : $"Forecast: {normalized} дней. Пересчёт ансамблей …");
        SetTradingCalculationStatus(
            normalized == 0 ? "Ожидайте — расчёт ансамблей…" : $"Ожидайте — Forecast {normalized}…",
            BreadthProgressLevel.Waiting);
        if (ReferenceEquals(BreadthTabs.SelectedItem, TradingEnsemblesTab))
            StartTradingEnsembleCalculationIfNeeded();
    }

    private void ApplyTradingEnsembleOptions()
    {
        if (TradingEnsembleChart is null || TradingShowZz1 is null) return;
        TradingEnsembleChart.SetPackages(
            TradingShowZz1.IsChecked == true,
            TradingShowZz2.IsChecked == true,
            TradingShowZz3.IsChecked == true);
        TradingEnsembleChart.SetShowEma(TradingShowEma.IsChecked == true);
        TradingEnsembleChart.SetShowScoringClassification(TradingShowScoring.IsChecked == true);
        TradingEnsembleChart.SetShowPrice(
            TradingShowSp500.IsChecked == true && ShowSp500Price.IsChecked == true);
    }

    private void SetPriceSeries(PriceSeries? series)
    {
        foreach (BreadthChart chart in AllCharts)
        {
            if (!ReferenceEquals(chart, ContangoSvxyChart))
                chart.SetPriceSeries(series);
        }
        FuzzyChart?.SetData(series, _fuzzyStates, _technicalIndicators);
        TradingEnsembleChart?.SetData(_tradingEnsembles, series, _technicalIndicators);
        SetPriceVisibility(ShowSp500Price.IsChecked == true);
        SetPriceCandleMode(ShowSp500Candles.IsChecked == true);
    }

    private void SetPriceVisibility(bool show)
    {
        if (Sp50Chart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetShowPriceSeries(show);
        TradingEnsembleChart?.SetShowPrice(show && TradingShowSp500.IsChecked == true);
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
        TradingEnsembleChart?.SetShowCandles(showCandles);
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

    private void TitanicLayerVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (TitanicChart is null)
            return;

        TitanicChart.SetLayerVisibility(
            TitanicShowActive?.IsChecked == true,
            TitanicShowPersistent?.IsChecked == true,
            TitanicShowNewHigh?.IsChecked == true,
            TitanicShowNewLow?.IsChecked == true,
            TitanicShowValue?.IsChecked == true);
    }

    private void RefreshHoldConfidence(bool export = true)
    {
        if (_fuzzyStates is null || _technicalIndicators is null || _zweigSeries is null)
            return;

        int profile = Math.Clamp(PositionProfile?.SelectedIndex ?? 1, 0, 2);
        if (!ResearchHoldConfidenceCalculator.TryCalculate(
                profile, out HoldConfidenceSeries research, out string researchError,
                out ResearchCalculationFailure failure))
        {
            if (failure == ResearchCalculationFailure.SnapshotNotReady)
            {
                // The first calculation has not promoted its CSV snapshot yet.
                // The research model is retried after the current export.
                _holdConfidence ??= HoldConfidenceCalculator.Calculate(
                    _fuzzyStates,
                    _technicalIndicators,
                    _contangoStates,
                    _titanicSeries,
                    _zweigSeries);
            }
            else
            {
                // Retain the last valid result instead of silently replacing it
                // with a different model when a real calculation error occurs.
                _holdConfidence ??= HoldConfidenceCalculator.Calculate(
                    _fuzzyStates,
                    _technicalIndicators,
                    _contangoStates,
                    _titanicSeries,
                    _zweigSeries);
                SetStatus($"Ошибка исследовательской модели: {researchError}", BreadthProgressLevel.Error);
            }
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
        TradingEnsembleChart.SetCursorDate(date);
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
            : $"   Фаза: {HumanLanguage.MarketZone(fuzzy.DominantZone)} {fuzzy.Confidence:P0} " +
              $"(SB {fuzzy.Buy:P0}, B {fuzzy.PrepareBuy:P0}, Warn {fuzzy.Caution:P0}, " +
              $"S {fuzzy.PrepareSell:P0}, SS {fuzzy.ReduceRisk:P0})" +
              (hold is null
                  ? string.Empty
                  : $"   Оценка модели: {hold.Value:F0} ({HumanLanguage.PositionDecision(hold.Value)})");
        TechnicalIndicatorPoint? technical = FindPoint(_technicalIndicators, date);
        TitanicPoint? titanic = FindPoint(_titanicSeries, date);
        ZweigPoint? zweig = FindPoint(_zweigSeries, date);
        string titanicRatioText = titanic is not null && double.IsFinite(titanic.NasdaqHighLowRatio)
            ? titanic.NasdaqHighLowRatio.ToString("F3")
            : "нет данных";
        string titanicText = titanic is null
            ? string.Empty
            : $"   Титаник: {(titanic.Signal ? $"действует {titanic.RunLength} дн." : "выключен")} " +
              $"NH/(NH+NL) {titanicRatioText}; " +
              $"новые максимумы {titanic.NasdaqNewHighs:F0}/минимумы {titanic.NasdaqNewLows:F0}";
        string zweigText = zweig is null
            ? string.Empty
            : $"   Плотность Цвейга: {zweig.Density}";
        string curveText = curve is null
            ? "   Кривая VIX: данных нет"
            : $"   Кривая VIX: {HumanLanguage.CurveState(curve.State)}; " +
              $"участок 30–60 дней {curve.Contango30To60Pct:+0.00;-0.00;0.00}%; " +
              $"оценка формы {curve.CurveRegimeScore:+0.00;-0.00;0.00}";
        string svxyRecommendationText = curve?.SvxyRecommendation switch
        {
            SvxyRecommendation.StrongBuy => "   SVXY: первый день после выхода из красной зоны",
            SvxyRecommendation.Buy => $"   SVXY: покупка или удержание, день {curve.SvxyBarsSinceCandidate}",
            SvxyRecommendation.ExtendedHold =>
                $"   SVXY: продлённое удержание, день {curve.SvxyBarsSinceCandidate}",
            _ => "   SVXY: активного сигнала нет"
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

        OperationalPositionTitle.Text = $"ПОЗИЦИЯ ПО МОДЕЛИ НА {date:yyyy-MM-dd}";
        UpdateExpirationWarning(date);
        if (hold is null)
        {
            OperationalPositionText.Text = "Расчёт модели: данных нет\nЦелевая позиция: данных нет\nДействие: ждать";
            OperationalPositionBorder.Background = new SolidColorBrush(Color.FromRgb(245, 247, 249));
            OperationalPositionBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(185, 200, 213));
            if (TopContributionsBorder is not null)
                TopContributionsBorder.Visibility = Visibility.Collapsed;
            return;
        }

        int target = ModelPositionSizing.TargetContracts(hold.Value);
        HoldConfidencePoint? previous = FindPreviousPoint(_holdConfidence, hold.Date);
        int previousTarget = previous is null ? 0 : ModelPositionSizing.TargetContracts(previous.Value);
        int delta = target - previousTarget;
        string action = previous is null
            ? target == 0 ? "оставаться вне рынка" : $"открыть {target} контрактов"
            : delta > 0
                ? $"увеличить позицию на {delta}, до {target} контрактов"
                : delta < 0
                    ? target == 0 ? $"закрыть все {previousTarget} контрактов" : $"сократить позицию до {target} контрактов"
                    : target == 0 ? "оставаться вне рынка" : $"удерживать {target} контрактов";
        string yesterday = previous is null
            ? "предыдущего значения нет"
            : $"{previous.Date:yyyy-MM-dd}: уровень {previous.Value:F1}%, позиция {previousTarget} из 10";
        string curveState = curve is null
            ? "данных нет"
            : HumanLanguage.CurveState(curve.State);
        string svxy = curve is null
            ? "данных нет"
            : curve.SvxyRecommendation switch
            {
                SvxyRecommendation.StrongBuy => "первый день после выхода кривой из красной зоны",
                SvxyRecommendation.Buy => $"окно покупки или удержания, день {curve.SvxyBarsSinceCandidate}",
                SvxyRecommendation.ExtendedHold => $"продлённое удержание, день {curve.SvxyBarsSinceCandidate}",
                _ => "активного сигнала нет"
            };

        string model = HumanLanguage.Profile(hold.Profile);
        double distance = hold.Value - ModelPositionSizing.FixedLevel;
        string thresholdExplanation = distance >= 0
            ? $"на {distance:F1} балла выше порога {ModelPositionSizing.FixedLevel:F0}"
            : $"на {Math.Abs(distance):F1} балла ниже порога {ModelPositionSizing.FixedLevel:F0}";
        OperationalPositionTitle.Text =
            $"ПОЗИЦИЯ ПО МОДЕЛИ «{model.ToUpperInvariant()}» НА {date:yyyy-MM-dd}";
        OperationalPositionText.Text =
            $"Оценка модели: {hold.Value:F1} балла из 100; {thresholdExplanation}.\n" +
            $"Позиция на следующем открытии: {target} из 10 контрактов.\n" +
            $"Действие: {action}.\n" +
            $"Предыдущий торговый день: {yesterday}.\n" +
            $"Наиболее сильный положительный фактор: {MainHoldSupport(hold)}.\n" +
            $"Наиболее сильный отрицательный фактор: {MainHoldRisk(hold)}.\n" +
            $"Кривая фьючерсов VIX: {curveState}.\n" +
            $"SVXY: {svxy}.";

        if (TopContributionsBorder is not null && TopContributionsTitle is not null && TopContributionsText is not null)
        {
            bool hasContributions = !string.IsNullOrWhiteSpace(hold.TopContributions);
            TopContributionsBorder.Visibility = hasContributions ? Visibility.Visible : Visibility.Collapsed;
            if (hasContributions)
            {
                TopContributionsTitle.Text =
                    $"ТОП-10 ПОКАЗАТЕЛЕЙ РЕЖИМА {HumanLanguage.Profile(hold.Profile)} " +
                    $"НА {date:yyyy-MM-dd} И ИХ ВЛИЯНИЕ НА ОЦЕНКУ";
                TopContributionsText.Text = hold.TopContributions;
            }
        }

        Color confidenceColor = FuzzyStateChart.ColorForHoldConfidence(hold.Value);
        OperationalPositionBorder.BorderBrush = new SolidColorBrush(confidenceColor);
        OperationalPositionBorder.Background = new SolidColorBrush(Color.FromArgb(
            24, confidenceColor.R, confidenceColor.G, confidenceColor.B));
    }

    private void CopyMarketStateClick(object sender, RoutedEventArgs e)
    {
        var sections = new List<string>();

        var position = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(OperationalPositionTitle?.Text))
            position.AppendLine(OperationalPositionTitle.Text.Trim());
        if (OperationalEventWarning?.Visibility == Visibility.Visible &&
            !string.IsNullOrWhiteSpace(OperationalEventWarning.Text))
            position.AppendLine(OperationalEventWarning.Text.Trim());
        if (OperationalVixExpirationNotice?.Visibility == Visibility.Visible &&
            !string.IsNullOrWhiteSpace(OperationalVixExpirationNotice.Text))
            position.AppendLine(OperationalVixExpirationNotice.Text.Trim());
        if (!string.IsNullOrWhiteSpace(OperationalPositionText?.Text))
            position.Append(OperationalPositionText.Text.Trim());
        if (position.Length > 0)
            sections.Add(position.ToString());

        if (!string.IsNullOrWhiteSpace(FuzzyAnalysisText?.Text))
            sections.Add("СОСТОЯНИЕ РЫНКА" + Environment.NewLine + FuzzyAnalysisText.Text.Trim());

        if (TopContributionsBorder?.Visibility == Visibility.Visible &&
            !string.IsNullOrWhiteSpace(TopContributionsText?.Text))
        {
            string title = string.IsNullOrWhiteSpace(TopContributionsTitle?.Text)
                ? "ТОП-10 ПОКАЗАТЕЛЕЙ"
                : TopContributionsTitle.Text.Trim();
            sections.Add(title + Environment.NewLine + TopContributionsText.Text.Trim());
        }

        if (sections.Count == 0)
        {
            SetStatus("Нет текста для копирования.", BreadthProgressLevel.Error);
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine + Environment.NewLine, sections));
            SetStatus("Отчёт о состоянии рынка скопирован в буфер обмена.");
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось скопировать отчёт: {ex.Message}", BreadthProgressLevel.Error);
        }
    }

    private void UpdateExpirationWarning(DateTime date)
    {
        if (OperationalEventWarning is null || OperationalVixExpirationNotice is null)
            return;

        DateTime? spxExpiration = AdjustedSpxExpiration(date.Year, date.Month);
        int? daysBeforeSpx = spxExpiration.HasValue
            ? (spxExpiration.Value - date.Date).Days
            : null;
        string? warning = daysBeforeSpx switch
        {
            0 => "КАЛЕНДАРНОЕ СОБЫТИЕ: сегодня месячная экспирация SPX. Исторически такие сессии чаще завершались снижением; повышения дневной волатильности и устойчивого эффекта следующего дня не обнаружено. Исследовательский вес: 0.",
            1 => "КАЛЕНДАРНОЕ СОБЫТИЕ: завтра месячная экспирация SPX. Исторически такие сессии чаще завершались снижением; повышения дневной волатильности и устойчивого эффекта следующего дня не обнаружено. Исследовательский вес: 0.",
            2 => "КАЛЕНДАРНОЕ СОБЫТИЕ: через два дня месячная экспирация SPX. Исторически такие сессии чаще завершались снижением; повышения дневной волатильности и устойчивого эффекта следующего дня не обнаружено. Исследовательский вес: 0.",
            _ => null
        };
        OperationalEventWarning.Text = warning ?? string.Empty;
        OperationalEventWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;

        DateTime? vixSettlement = StandardVixSettlement(date.Year, date.Month);
        int? daysBeforeVix = vixSettlement.HasValue
            ? (vixSettlement.Value - date.Date).Days
            : null;
        string? vixNotice = daysBeforeVix switch
        {
            0 => "Календарь VIX: сегодня расчётная дата стандартного фьючерса VIX. Самостоятельный статистически значимый эффект не подтверждён; на оценку модели эта календарная дата не влияет.",
            1 => "Календарь VIX: завтра расчётная дата стандартного фьючерса VIX. Самостоятельный статистически значимый эффект не подтверждён; на оценку модели эта календарная дата не влияет.",
            2 => "Календарь VIX: через два дня расчётная дата стандартного фьючерса VIX. Самостоятельный статистически значимый эффект не подтверждён; на оценку модели эта календарная дата не влияет.",
            _ => null
        };
        OperationalVixExpirationNotice.Text = vixNotice ?? string.Empty;
        OperationalVixExpirationNotice.Visibility = vixNotice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static DateTime ThirdFriday(int year, int month)
    {
        DateTime first = new(year, month, 1);
        int offset = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 14);
    }

    private DateTime? AdjustedSpxExpiration(int year, int month)
    {
        return PreviousLoadedTradingDay(ThirdFriday(year, month));
    }

    private DateTime? StandardVixSettlement(int year, int month)
    {
        DateTime followingMonth = new DateTime(year, month, 1).AddMonths(1);
        DateTime? adjustedFollowingFriday = AdjustedSpxExpiration(followingMonth.Year, followingMonth.Month);
        return adjustedFollowingFriday.HasValue
            ? PreviousLoadedTradingDay(adjustedFollowingFriday.Value.AddDays(-30))
            : null;
    }

    private DateTime? PreviousLoadedTradingDay(DateTime candidate)
    {
        HashSet<DateTime> tradingDays = _sp500PriceSeries?.Points
            .Select(point => point.Date.Date)
            .ToHashSet() ?? [];
        DateTime? lastLoadedDate = tradingDays.Count == 0 ? null : tradingDays.Max();
        for (int attempts = 0; attempts < 10; attempts++, candidate = candidate.AddDays(-1))
        {
            bool knownLoadedDay = tradingDays.Contains(candidate.Date);
            bool futureRegularDay = lastLoadedDate.HasValue && candidate.Date > lastLoadedDate.Value &&
                                    IsRegularUsTradingDay(candidate.Date);
            if (knownLoadedDay || futureRegularDay)
                return candidate.Date;
        }
        return null;
    }

    private static bool IsRegularUsTradingDay(DateTime date)
    {
        date = date.Date;
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        DateTime Observed(DateTime holiday) => holiday.DayOfWeek switch
        {
            DayOfWeek.Saturday => holiday.AddDays(-1),
            DayOfWeek.Sunday => holiday.AddDays(1),
            _ => holiday
        };
        DateTime NthWeekday(int month, DayOfWeek weekday, int occurrence)
        {
            DateTime first = new(date.Year, month, 1);
            int offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(offset + 7 * (occurrence - 1));
        }
        DateTime LastWeekday(int month, DayOfWeek weekday)
        {
            DateTime last = new DateTime(date.Year, month, DateTime.DaysInMonth(date.Year, month));
            int offset = ((int)last.DayOfWeek - (int)weekday + 7) % 7;
            return last.AddDays(-offset);
        }

        var holidays = new HashSet<DateTime>
        {
            Observed(new DateTime(date.Year, 1, 1)),
            NthWeekday(1, DayOfWeek.Monday, 3),
            NthWeekday(2, DayOfWeek.Monday, 3),
            EasterSunday(date.Year).AddDays(-2),
            LastWeekday(5, DayOfWeek.Monday),
            Observed(new DateTime(date.Year, 7, 4)),
            NthWeekday(9, DayOfWeek.Monday, 1),
            NthWeekday(11, DayOfWeek.Thursday, 4),
            Observed(new DateTime(date.Year, 12, 25))
        };
        if (date.Year >= 2022)
            holidays.Add(Observed(new DateTime(date.Year, 6, 19)));
        return !holidays.Contains(date);
    }

    private static DateTime EasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }

    private static string MainHoldSupport(HoldConfidencePoint hold)
    {
        if (!string.IsNullOrWhiteSpace(hold.MainSupport))
            return hold.MainSupport;
        (string Label, double Value)[] supports =
        [
            ("бычий порядок средних", 0.30 * hold.TrendComponent),
            ("широта рынка", 0.25 * hold.BreadthComponent),
            ("текущая фаза рынка", 0.20 * hold.PhaseComponent),
            ("спокойная дневная волатильность", 0.10 * hold.VolatilityComponent),
            ("контанго", 0.10 * hold.ContangoComponent),
            ("импульс широты Цвейга", 0.05 * hold.ZweigComponent)
        ];
        (string Label, double Value) strongest = supports.MaxBy(item => item.Value);
        return $"{strongest.Label}: +{strongest.Value * 100.0:F1} балла к оценке модели";
    }

    private static string MainHoldRisk(HoldConfidencePoint hold)
    {
        if (!string.IsNullOrWhiteSpace(hold.MainRisk))
            return hold.MainRisk;
        (string Label, double Value)[] risks =
        [
            ("нарушение порядка средних", 0.30 * (1.0 - hold.TrendComponent)),
            ("слабая широта рынка", 0.25 * (1.0 - hold.BreadthComponent)),
            ("неблагоприятная фаза рынка", 0.20 * (1.0 - hold.PhaseComponent)),
            ("высокая дневная волатильность", 0.10 * (1.0 - hold.VolatilityComponent)),
            ("неблагоприятная кривая VIX", 0.10 * (1.0 - hold.ContangoComponent)),
            ("сигнал «Титаник»", 0.20 * hold.TitanicPenalty),
            ("ценовой шок и сокращение риска", 0.25 * hold.ShockPenalty)
        ];
        (string Label, double Value) strongest = risks.MaxBy(item => item.Value);
        return strongest.Value < 0.015
            ? "заметного давления нет"
            : $"{strongest.Label}: -{strongest.Value * 100.0:F1} балла к оценке модели";
    }

    private string BuildMarketStateExplanation(
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
            : $"Кривая фьючерсов VIX: {HumanLanguage.CurveState(curve.State)}; " +
              $"оценка формы {curve.CurveRegimeScore:+0.00;-0.00;0.00}, участок 30–60 дней " +
              $"{curve.Contango30To60Pct:+0.00;-0.00;0.00}%. " +
              DescribeSvxyRecommendation(curve);
        string titanicText = titanic is null
            ? "Titanic: нет данных."
            : titanic.Signal
                ? $"Titanic активен d{titanic.RunLength}" +
                  (titanic.Persistent ? ", устойчивый режим. " : ", ранняя стадия. ") +
                  DescribeTitanicHistoricalStatus(date, titanic)
                : "Titanic выключен.";
        string zweigText = zweig is null
            ? "Zweig: нет данных."
            : zweig.Density > 0
                ? $"Zweig: поддержка позиции, плотность {zweig.Density}."
                : "Zweig: активного прикрытия нет.";

        var text = new StringBuilder();
        text.AppendLine($"{date:yyyy-MM-dd}   {HumanLanguage.MarketZone(fuzzy.DominantZone)} " +
                        $"({fuzzy.Confidence:P0})");
        if (hold is not null)
        {
            if (hold.Profile == "Legacy")
            {
                text.AppendLine($"Состав расчёта: средние {hold.TrendComponent:P0}, широта {hold.BreadthComponent:P0}, " +
                                $"фаза рынка {hold.PhaseComponent:P0}, спокойная волатильность {hold.VolatilityComponent:P0}, " +
                                $"контанго {hold.ContangoComponent:P0}, импульс Цвейга {hold.ZweigComponent:P0}; " +
                                $"давление «Титаника» {hold.TitanicPenalty:P0}, ценовой шок {hold.ShockPenalty:P0}.");
            }
        }
        text.AppendLine(conclusion);
        AppendResearchLine(text, $"Режим «{HumanLanguage.MarketRegime(fuzzy.DominantZone)}» за исследуемый период",
            research?.PhaseOutcomes.GetValueOrDefault(fuzzy.DominantZone),
            research?.HorizonBars ?? 20);
        text.AppendLine();
        string emaDistanceText = ema20Distance >= 0
            ? $"индекс SPY находится на {ema20Distance:P1} выше 20-дневной EMA"
            : $"индекс SPY находится на {Math.Abs(ema20Distance):P1} ниже 20-дневной EMA";
        text.AppendLine($"EMA: {emaState} ({fuzzy.BullishEmaOrder:P0}); {emaDistanceText}. " +
                        DescribeEmaDirections(date));
        EmaResearchRegime emaRegime = ClassifyEmaRegime(fuzzy.BullishEmaOrder);
        AppendResearchLine(text, "Аналогичный EMA-режим за исследуемый период",
            research?.EmaOutcomes.GetValueOrDefault(emaRegime),
            research?.HorizonBars ?? 20);
        text.AppendLine($"MyRSI 14,8 {technical.MyRsiRep:+0.00;-0.00;0.00}: {slowRsiState}. " +
                        $"MyRSI 10,8 {technical.MyRsiRep10:+0.00;-0.00;0.00}: {fastRsiState}." +
                        releaseText);
        text.AppendLine(rsiMeaning);
        text.AppendLine($"Breadth: S&P 50/100/200 = {fuzzy.Sp50:F0}/{fuzzy.Sp100:F0}/{fuzzy.Sp200:F0}%, " +
                        $"Nasdaq 100 = {fuzzy.Nasdaq100:F0}%; сила разворота {fuzzy.BreadthTurnUp:P0}.");
        text.AppendLine(candleText);
        text.AppendLine();
        string recentEvents = BuildRecentMarketNarrative(date);
        if (!string.IsNullOrWhiteSpace(recentEvents))
        {
            text.AppendLine("Что изменилось за последние торговые дни:");
            text.AppendLine(recentEvents);
        }
        text.AppendLine(vixText);
        text.AppendLine(titanicText);
        text.Append(zweigText);
        return text.ToString();
    }

    private string BuildRecentMarketNarrative(DateTime date)
    {
        var lines = new List<string>();
        PricePoint[] allPrices = _sp500PriceSeries?.Points
            .Where(point => point.Date <= date)
            .ToArray() ?? [];
        PricePoint[] prices = allPrices.TakeLast(3).ToArray();
        var technicalByDate = _technicalIndicators?.Points
            .ToDictionary(point => point.Date) ?? [];
        string[] dayNames = prices.Length switch
        {
            3 => ["Два торговых дня назад", "В предыдущий торговый день", "В выбранный день"],
            2 => ["В предыдущий торговый день", "В выбранный день"],
            _ => ["В выбранный день"]
        };
        for (int index = 0; index < prices.Length; index++)
        {
            PricePoint price = prices[index];
            if (!technicalByDate.TryGetValue(price.Date, out TechnicalIndicatorPoint? technical))
                continue;
            double move = price.Open == 0.0 ? 0.0 : price.Close / price.Open - 1.0;
            string color = price.Close >= price.Open ? "зелёная" : "красная";
            int allIndex = Array.FindIndex(allPrices, point => point.Date == price.Date);
            PricePoint? previousPrice = allIndex > 0 ? allPrices[allIndex - 1] : null;
            TechnicalIndicatorPoint? previousTechnical = previousPrice is not null &&
                technicalByDate.TryGetValue(previousPrice.Date, out TechnicalIndicatorPoint? foundPreviousTechnical)
                    ? foundPreviousTechnical
                    : null;
            string emaPosition = DescribeDailyEma20And50Position(
                price, technical, previousPrice, previousTechnical);
            lines.Add($"• {dayNames[index]} ({price.Date:dd.MM}): {color} свеча {move:+0.0%;-0.0%;0.0%}; {emaPosition}.");
        }

        int betweenRun = CountConsecutiveBodiesBetweenEma20And50(allPrices, technicalByDate);
        if (betweenRun >= 2 && prices.Length > 0 && technicalByDate.TryGetValue(prices[^1].Date, out TechnicalIndicatorPoint? latestTechnical))
        {
            string upper = latestTechnical.Ema20 >= latestTechnical.Ema50 ? "EMA20" : "EMA50";
            string lower = latestTechnical.Ema20 >= latestTechnical.Ema50 ? "EMA50" : "EMA20";
            lines.Add($"• Уже {betweenRun} {RussianSessionWord(betweenRun)} подряд тела дневных свечей SPY остаются между {upper} и {lower}; внутридневные выходы теней за средние описаны отдельно для каждого дня.");
        }

        FuzzyMarketStatePoint[] breadth = _fuzzyStates?.Points
            .Where(point => point.Date <= date)
            .TakeLast(2)
            .ToArray() ?? [];
        if (breadth.Length == 2)
        {
            int rising = 0;
            if (breadth[1].Sp50 > breadth[0].Sp50) rising++;
            if (breadth[1].Sp100 > breadth[0].Sp100) rising++;
            if (breadth[1].Sp200 > breadth[0].Sp200) rising++;
            if (breadth[1].Nasdaq100 > breadth[0].Nasdaq100) rising++;
            lines.Add(rising switch
            {
                >= 3 => $"• Широта продолжает расти: улучшились {rising} из 4 наблюдаемых рядов.",
                0 => "• Широта не подтвердила рост цены: ни один из четырёх рядов не улучшился.",
                _ => $"• Широта неоднородна: улучшились {rising} из 4 наблюдаемых рядов."
            });
        }

        VixCurveStatePoint[] curves = _contangoStates?.Points
            .Where(point => point.Date <= date)
            .TakeLast(2)
            .ToArray() ?? [];
        if (curves.Length == 2)
        {
            bool previousBackwardation = curves[0].M1ToM2Pct <= 0.0;
            bool currentContango = curves[1].M1ToM2Pct > 0.0;
            if (previousBackwardation && currentContango)
            {
                int run = 0;
                if (_contangoStates is not null)
                {
                    foreach (VixCurveStatePoint point in _contangoStates.Points.Where(point => point.Date < curves[1].Date).Reverse())
                    {
                        if (!point.M1ToM2Pct.HasValue || point.M1ToM2Pct > 0.0) break;
                        run++;
                    }
                }
                lines.Add($"• В выбранный день между двумя ближайшими фьючерсами VIX началось контанго после бэквордации; " +
                          $"предыдущая бэквордация длилась {run} " +
                          RussianDayWord(run) + ".");
            }
            else if (curves[0].State != curves[1].State)
            {
                lines.Add($"• Кривая VIX изменилась: было «{HumanLanguage.CurveState(curves[0].State)}», " +
                          $"стало «{HumanLanguage.CurveState(curves[1].State)}».");
            }
        }

        PricePoint[] svxy = _svxyPriceSeries?.Points
            .Where(point => point.Date <= date)
            .TakeLast(2)
            .ToArray() ?? [];
        if (svxy.Length == 2 && svxy[0].Close > 0.0)
        {
            double change = svxy[1].Close / svxy[0].Close - 1.0;
            double range = svxy[0].High - svxy[0].Low;
            double location = range <= 0.0 ? 0.5 : Math.Clamp((svxy[1].Close - svxy[0].Low) / range, 0.0, 1.0);
            string locationText = location is >= 0.40 and <= 0.60
                ? "около середины диапазона предыдущей сессии"
                : location < 0.40 ? "в нижней части диапазона предыдущей сессии" : "в верхней части диапазона предыдущей сессии";
            lines.Add($"• SVXY изменился на {change:+0.0%;-0.0%;0.0%} и находится {locationText}.");
        }

        DateTime? spxExpiration = AdjustedSpxExpiration(date.Year, date.Month);
        if (spxExpiration.HasValue && prices.Any(point => point.Date == spxExpiration.Value))
        {
            int tradingDaysAgo = prices.Length - 1 - Array.FindIndex(prices, point => point.Date == spxExpiration.Value);
            string when = tradingDaysAgo == 0 ? "Выбранный день" : tradingDaysAgo == 1 ? "Предыдущий торговый день" : "Два торговых дня назад";
            lines.Add($"• {when} был днём месячной экспирации SPX. Исторически такие сессии чаще завершались снижением, но повышения дневной волатильности и устойчивого эффекта следующего дня не обнаружено; вес признака равен 0.");
        }

        DateTime? vixSettlement = StandardVixSettlement(date.Year, date.Month);
        if (vixSettlement.HasValue && prices.Any(point => point.Date == vixSettlement.Value))
        {
            int tradingDaysAgo = prices.Length - 1 - Array.FindIndex(prices, point => point.Date == vixSettlement.Value);
            string when = tradingDaysAgo == 0 ? "Выбранный день" : tradingDaysAgo == 1 ? "Предыдущий торговый день" : "Два торговых дня назад";
            lines.Add($"• {when} был расчётной датой стандартного фьючерса VIX. Самостоятельный эффект календарной даты статистически не подтверждён; на оценку модели эта дата не влияет.");
        }

        PricePoint[] loadedPrices = _sp500PriceSeries?.Points
            .Where(point => point.Date <= date)
            .ToArray() ?? [];
        if (loadedPrices.Length > 1)
        {
            PricePoint current = loadedPrices[^1];
            double priorHigh = loadedPrices[..^1].Max(point => point.High);
            if (current.High > priorHigh)
            {
                lines.Add($"• НОВЫЙ МАКСИМУМ SPY: {current.High:F2} — самый высокий уровень " +
                          $"за всю загруженную историю с {loadedPrices[0].Date:dd.MM.yyyy}. " +
                          "Это максимум доступного набора данных, а не утверждение о всей истории инструмента.");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeDailyEma20And50Position(
        PricePoint price,
        TechnicalIndicatorPoint technical,
        PricePoint? previousPrice,
        TechnicalIndicatorPoint? previousTechnical)
    {
        var events = new List<string>();
        if (previousPrice is not null && previousTechnical is not null)
        {
            AppendCloseCross(events, "EMA20", previousPrice.Close, previousTechnical.Ema20,
                price.Close, technical.Ema20);
            AppendCloseCross(events, "EMA50", previousPrice.Close, previousTechnical.Ema50,
                price.Close, technical.Ema50);
        }

        AppendIntradayTest(events, "EMA20", price, technical.Ema20);
        AppendIntradayTest(events, "EMA50", price, technical.Ema50);

        string upperName = technical.Ema20 >= technical.Ema50 ? "EMA20" : "EMA50";
        string lowerName = technical.Ema20 >= technical.Ema50 ? "EMA50" : "EMA20";
        double upperValue = Math.Max(technical.Ema20, technical.Ema50);
        double lowerValue = Math.Min(technical.Ema20, technical.Ema50);
        string closePosition = price.Close >= technical.Ema20
            ? price.Close >= technical.Ema50
                ? "закрытие выше EMA20 и EMA50"
                : "закрытие выше EMA20, но ниже EMA50"
            : price.Close >= technical.Ema50
                ? "закрытие между EMA20 и EMA50: ниже EMA20 и выше EMA50"
                : "закрытие ниже EMA20 и EMA50";
        events.Add(closePosition);

        if (IsCandleBodyBetweenEma20And50(price, technical))
            events.Add($"тело свечи полностью осталось между {upperName} и {lowerName}");

        string rangePosition = IsEntireRangeBetweenEma20And50(price, technical)
            ? $"весь дневной диапазон находился между {upperName} и {lowerName}: цена не поднялась выше {upperName} и не опустилась ниже {lowerName}"
            : price.Low > upperValue
                ? $"весь дневной диапазон находился выше EMA20 и EMA50"
                : price.High < lowerValue
                    ? $"весь дневной диапазон находился ниже EMA20 и EMA50"
                    : price.Low < lowerValue && price.High > upperValue
                        ? $"внутридневной диапазон вышел выше {upperName} и ниже {lowerName}"
                        : price.High > upperValue
                            ? $"внутридневной диапазон выходил выше {upperName}, но не опускался ниже {lowerName}"
                            : price.Low < lowerValue
                                ? $"внутридневной диапазон опускался ниже {lowerName}, но не поднимался выше {upperName}"
                                : $"внутридневной диапазон коснулся одной из границ коридора {upperName}–{lowerName}";
        events.Add(rangePosition);
        return string.Join("; ", events.Distinct());
    }

    private static void AppendCloseCross(
        ICollection<string> events,
        string name,
        double previousClose,
        double previousEma,
        double currentClose,
        double currentEma)
    {
        if (previousClose >= previousEma && currentClose < currentEma)
            events.Add($"закрытие пробило {name} сверху вниз");
        else if (previousClose <= previousEma && currentClose > currentEma)
            events.Add($"закрытие вернулось выше {name}");
    }

    private static void AppendIntradayTest(
        ICollection<string> events,
        string name,
        PricePoint price,
        double ema)
    {
        if (price.Open >= ema && price.Low < ema && price.Close >= ema)
            events.Add($"внутри дня цена опускалась ниже {name}, но закрылась выше неё");
        else if (price.Open <= ema && price.High > ema && price.Close <= ema)
            events.Add($"внутри дня цена поднималась выше {name}, но закрылась ниже неё");
    }

    private static bool IsEntireRangeBetweenEma20And50(
        PricePoint price,
        TechnicalIndicatorPoint technical)
    {
        double upper = Math.Max(technical.Ema20, technical.Ema50);
        double lower = Math.Min(technical.Ema20, technical.Ema50);
        return price.High <= upper && price.Low >= lower;
    }

    private static bool IsCandleBodyBetweenEma20And50(
        PricePoint price,
        TechnicalIndicatorPoint technical)
    {
        double upper = Math.Max(technical.Ema20, technical.Ema50);
        double lower = Math.Min(technical.Ema20, technical.Ema50);
        return Math.Max(price.Open, price.Close) <= upper &&
               Math.Min(price.Open, price.Close) >= lower;
    }

    private static int CountConsecutiveBodiesBetweenEma20And50(
        IReadOnlyList<PricePoint> prices,
        IReadOnlyDictionary<DateTime, TechnicalIndicatorPoint> technicalByDate)
    {
        int count = 0;
        for (int index = prices.Count - 1; index >= 0; index--)
        {
            if (!technicalByDate.TryGetValue(prices[index].Date, out TechnicalIndicatorPoint? technical) ||
                !IsCandleBodyBetweenEma20And50(prices[index], technical))
                break;
            count++;
        }
        return count;
    }

    private static string RussianSessionWord(int count)
    {
        int lastTwo = count % 100;
        int last = count % 10;
        return lastTwo is >= 11 and <= 14
            ? "торговых сессий"
            : last == 1
                ? "торговую сессию"
                : last is >= 2 and <= 4
                    ? "торговые сессии"
                    : "торговых сессий";
    }

    private string DescribeTitanicHistoricalStatus(DateTime date, TitanicPoint current)
    {
        TitanicPoint[] points = _titanicSeries?.Points
            .Where(point => point.Date <= date)
            .ToArray() ?? [];
        if (!current.Signal || points.Length == 0)
            return string.Empty;

        int currentIndex = Array.FindLastIndex(points, point => point.Date == current.Date);
        if (currentIndex < 0)
            return string.Empty;
        int startIndex = currentIndex;
        while (startIndex > 0 && points[startIndex - 1].Signal)
            startIndex--;

        TitanicPoint? priorRecord = points
            .Take(startIndex)
            .Where(point => point.Signal)
            .OrderByDescending(point => point.RunLength)
            .ThenByDescending(point => point.Date)
            .FirstOrDefault();
        if (priorRecord is null)
        {
            return "Это первый сигнал Titanic в загруженной исследуемой истории. " +
                   "Историческая уникальность сама по себе не означает торговой значимости.";
        }
        if (current.RunLength > priorRecord.RunLength)
        {
            return $"ЭКСТРАОРДИНАРНОЕ СОСТОЯНИЕ: текущая серия длится {current.RunLength} " +
                   $"{RussianDayWord(current.RunLength)} — такого продолжительного сигнала раньше в загруженной истории не было. " +
                   $"Предыдущий рекорд составлял {priorRecord.RunLength} {RussianDayWord(priorRecord.RunLength)} " +
                   $"и был достигнут {priorRecord.Date:dd.MM.yyyy}. " +
                   "Историческая уникальность сама по себе не означает торговой значимости.";
        }
        if (current.RunLength == priorRecord.RunLength)
        {
            return $"Текущая серия повторила прежний рекорд: {priorRecord.RunLength} " +
                   $"{RussianDayWord(priorRecord.RunLength)}, достигнутый {priorRecord.Date:dd.MM.yyyy}. " +
                   "Само совпадение с рекордом не означает торговой значимости.";
        }
        return $"Предыдущий рекорд загруженной истории — {priorRecord.RunLength} " +
               $"{RussianDayWord(priorRecord.RunLength)}, достигнут {priorRecord.Date:dd.MM.yyyy}.";
    }

    private string DescribeEmaDirections(DateTime date)
    {
        TechnicalIndicatorPoint[] points = _technicalIndicators?.Points
            .Where(point => point.Date <= date)
            .ToArray() ?? [];
        if (points.Length < 2)
            return "Направление средних пока определить нельзя.";

        return "Направление средних: " + string.Join("; ",
        [
            DescribeEmaDirection(points, point => point.Ema20, "EMA20"),
            DescribeEmaDirection(points, point => point.Ema50, "EMA50"),
            DescribeEmaDirection(points, point => point.Ema100, "EMA100"),
            DescribeEmaDirection(points, point => point.Ema200, "EMA200")
        ]) + ".";
    }

    private static string DescribeEmaDirection(
        IReadOnlyList<TechnicalIndicatorPoint> points,
        Func<TechnicalIndicatorPoint, double> selector,
        string name)
    {
        int last = points.Count - 1;
        int direction = Math.Sign(selector(points[last]) - selector(points[last - 1]));
        if (direction == 0)
            return $"{name} не изменилась по сравнению с предыдущим торговым днём";

        int sessions = 1;
        for (int index = last - 1; index > 0; index--)
        {
            int priorDirection = Math.Sign(selector(points[index]) - selector(points[index - 1]));
            if (priorDirection != direction)
                break;
            sessions++;
        }

        string verb = direction > 0 ? "растёт" : "снижается";
        return $"{name} {verb} {TradingSessionsPhrase(sessions)} подряд";
    }

    private static string TradingSessionsPhrase(int value)
    {
        int mod100 = Math.Abs(value) % 100;
        int mod10 = mod100 % 10;
        if (mod100 is >= 11 and <= 14) return $"{value} торговых сессий";
        return mod10 switch
        {
            1 => $"{value} торговую сессию",
            >= 2 and <= 4 => $"{value} торговые сессии",
            _ => $"{value} торговых сессий"
        };
    }

    private static string RussianDayWord(int value)
    {
        int mod100 = Math.Abs(value) % 100;
        int mod10 = mod100 % 10;
        if (mod100 is >= 11 and <= 14) return "дней";
        return mod10 switch { 1 => "день", >= 2 and <= 4 => "дня", _ => "дней" };
    }

    private static MarketResearchSummary BuildMarketResearchSummary(
        FuzzyMarketStateSeries fuzzyStates,
        PriceSeries spy,
        int horizonBars)
    {
        var fuzzyByDate = fuzzyStates.Points.ToDictionary(point => point.Date);
        var phaseReturns = new Dictionary<MarketZone, List<double>>();
        var emaReturns = new Dictionary<EmaResearchRegime, List<double>>();
        MarketZone? previousPhase = null;
        EmaResearchRegime? previousEmaRegime = null;
        for (int index = 0; index + horizonBars < spy.Points.Count; index++)
        {
            PricePoint current = spy.Points[index];
            if (current.Close <= 0.0 || !fuzzyByDate.TryGetValue(current.Date, out FuzzyMarketStatePoint? fuzzy))
                continue;
            double forwardReturn = spy.Points[index + horizonBars].Close / current.Close - 1.0;
            if (!double.IsFinite(forwardReturn))
                continue;

            if (previousPhase != fuzzy.DominantZone)
            {
                if (!phaseReturns.TryGetValue(fuzzy.DominantZone, out List<double>? phaseValues))
                    phaseReturns[fuzzy.DominantZone] = phaseValues = [];
                phaseValues.Add(forwardReturn);
            }
            previousPhase = fuzzy.DominantZone;

            EmaResearchRegime emaRegime = ClassifyEmaRegime(fuzzy.BullishEmaOrder);
            if (previousEmaRegime != emaRegime)
            {
                if (!emaReturns.TryGetValue(emaRegime, out List<double>? emaValues))
                    emaReturns[emaRegime] = emaValues = [];
                emaValues.Add(forwardReturn);
            }
            previousEmaRegime = emaRegime;
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

        string sampleWarning = stats.Count < 30 ? " Исторических данных для этого режима пока мало." : string.Empty;
        text.AppendLine($"{label}: через {horizonBars} торговых дней SPY был выше исходного уровня " +
                        $"в {stats.PositiveRate:P0} случаев. " +
                        $"Среднее изменение цены SPY за эти {horizonBars} дней составило " +
                        $"{stats.AverageReturn:+0.0%;-0.0%;0.0%}.{sampleWarning}");
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
            SvxyRecommendation.StrongBuy => "SVXY: первый день после выхода кривой из красной зоны.",
            SvxyRecommendation.Buy => $"SVXY: окно покупки или удержания, день {curve.SvxyBarsSinceCandidate}.",
            SvxyRecommendation.ExtendedHold =>
                $"SVXY: продлённое удержание, день {curve.SvxyBarsSinceCandidate}.",
            _ => "SVXY: активного сигнала нет."
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
        csv.AppendLine("Date,Signal,RunLength,Persistent,QQQOpen,QQQHigh,QQQLow,QQQClose,AnnualHigh,RecentlyAtHigh,NasdaqNewHighs,NasdaqNewLows,NasdaqHighLowRatio,LowsExceedHighs,Nasdaq100AboveSMA50,ValidBreadthMembers,BreadthFilterPassed,Lookback,RecentHighWindow,HighTolerancePercent,PersistentDays,UseBreadthFilter,BreadthThreshold");
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
                .Append(double.IsFinite(point.NasdaqHighLowRatio)
                    ? point.NasdaqHighLowRatio.ToString("0.######", CultureInfo.InvariantCulture)
                    : string.Empty).Append(',')
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
        bool keepRightEdge = currentEnd >= pointCount - 1;
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
        int newStart = keepRightEdge
            ? maximumStart
            : (int)Math.Round(anchorIndexForViewport - anchorFraction * Math.Max(0, newCount - 1));
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
                current is AtrpChart || current is TradingEnsembleChart)
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
        TradingEnsembleChart.SetCursorPinned(pinned);
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
        TradingEnsembleChart.SetViewport(start, end);
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
        OperationalPositionTitle.Text = "ПОЗИЦИЯ ПО МОДЕЛИ";
        OperationalEventWarning.Text = string.Empty;
        OperationalEventWarning.Visibility = Visibility.Collapsed;
        OperationalPositionText.Text = "Ожидание расчёта";
        FuzzyAnalysisText.Text = "Рассчитайте данные, затем наведите курсор на график.";
        TitanicChart.SetMessage("SentimenTrader Titanic", "Waiting for calculation");
        ZweigChart.SetMessage("Zweig Breadth Thrust", "Waiting for calculation");
        TradingEnsembleChart.SetMessage("После завершения общего расчёта откройте эту вкладку: расчёт ZZ1, ZZ2 и ZZ3 запустится один раз автоматически.");
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
