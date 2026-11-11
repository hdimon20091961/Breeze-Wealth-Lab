using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

    private sealed record TradingEfficiencyRow(string Key, double FullPeriod, double RecentPerformance);

    private sealed record TradingEfficiencyReport(
        IReadOnlyList<TradingEfficiencyRow> Rows,
        DateTime FullStart,
        DateTime FullEnd,
        DateTime RecentStart,
        DateTime RecentEnd);

    private readonly record struct TradingInputReadiness(bool IsReady, string Message);

    private sealed record ExportSnapshot(
        BreadthSeries Sp50,
        BreadthSeries Sp100,
        BreadthSeries Sp200,
        BreadthSeries Nasdaq100,
        PriceSeries Sp500Price,
        PriceSeries? SvxyPrice,
        FuzzyMarketStateSeries FuzzyStates,
        TechnicalIndicatorSeries TechnicalIndicators,
        SentimentSeries Sentiment,
        TitanicSeries Titanic,
        ZweigSeries ZweigStrong,
        ZweigSeries ZweigWorking);

    public const string WindowToken = "BreezeVL.MarketBreadth";
    private const int DefaultVisibleBarCount = 200;
    private const int MinimumVisibleBarCount = 20;
    private const int VisibleBarStep = 20;
    private const int DefaultTradingPerformanceBars = 150;
    private const int TradingPerformanceBarStep = 10;
    private const int MinimumTradingPerformanceBars = 10;
    private static readonly DateTime TradingEfficiencyStartDate = new(2018, 7, 15);
    private const double TradingEfficiencyStartingCapital = 3000.0;
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
        Rules2Selector.IsChecked = _layoutState.SelectedScoreModel == "Rules2";
        Rules1Selector.IsChecked = Rules2Selector.IsChecked != true;
        _scoreSelectionReady = true;
        UpdateMarketSetupAtrpLayout();
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
        SentimentSpChart.SetAutoScaleBreadth(true);
        SentimentSpChart.SetPriceOnly(true);
        SentimentSpChart.SetTitle("S&P 500 (SPY)");
        SentimentSpChart.SetShowDateLabels(false);
        SentimentSpChart.SetHorizontalPlotMargins(50, 50);
        SentimentSpChart.SetShowLeftPriceScaleLabels(true);
        MarketSetupSpChart.SetAutoScaleBreadth(true);
        MarketSetupSpChart.SetPriceOnly(true);
        MarketSetupSpChart.SetTitle("S&P 500 (SPY) — выбранное правило");
        MarketSetupSpChart.SetShowDateLabels(false);
        MarketSetupSpChart.SetHorizontalPlotMargins(50, 50);
        MarketSetupSpChart.SetShowLeftPriceScaleLabels(true);
        foreach (BreadthChart chart in AllCharts)
            chart.CursorDateChanged += UpdateCursorValues;
        SentimentSpChart.CursorDateChanged += UpdateCursorValues;
        SentimentIndicatorChart.CursorDateChanged += UpdateCursorValues;
        MarketSetupSpChart.CursorDateChanged += UpdateCursorValues;
        MarketSetupIndicatorChart.CursorDateChanged += UpdateCursorValues;
        MarketSetupAtrpChart.CursorDateChanged += UpdateCursorValues;
        MarketSetupAtrpChart.SetShowDateLabels(false);
        MarketSetupAtrpChart.SetHorizontalPlotMargins(50, 50);
        OverviewAtrpChart.CursorDateChanged += UpdateCursorValues;
        ContangoMyRsiChart.CursorDateChanged += UpdateCursorValues;
        FuzzyChart.CursorDateChanged += UpdateCursorValues;
        TitanicChart.CursorDateChanged += UpdateCursorValues;
        ZweigChart.CursorDateChanged += UpdateCursorValues;
        TradingEnsembleChart.CursorDateChanged += UpdateCursorValues;
        TradingEnsembleChart.PaneLayoutChanged += () =>
        {
            CaptureTradingLayout();
            _layoutState.TradingCollapsed = TradingEnsembleChart.GetCollapsedPanes();
            SavePersistentLayout();
        };
        TitanicChart.PersistentDaysChanged += TitanicPersistentDaysChanged;
        BreadthTabs.SelectionChanged += BreadthTabsSelectionChanged;
        BreadthTabs.LostMouseCapture += (_, _) =>
        {
            _isPanning = false;
            bool saveTradingLayout = _isTradingPaneResizing;
            bool saveTwoPaneLayout = _activeTwoPaneResizeChart is not null;
            if (ReferenceEquals(_activeTwoPaneResizeChart, TitanicChart))
            {
                TitanicChart.EndPaneResize();
                _layoutState.TitanicTopRatio = TitanicChart.GetPaneRatio();
            }
            else if (ReferenceEquals(_activeTwoPaneResizeChart, ZweigChart))
            {
                ZweigChart.EndPaneResize();
                _layoutState.ZweigTopRatio = ZweigChart.GetPaneRatio();
            }
            _activeTwoPaneResizeChart = null;
            if (saveTwoPaneLayout)
            {
                Mouse.OverrideCursor = _previousOverrideCursor;
                _previousOverrideCursor = null;
            }
            _isTradingPaneResizing = false;
            TradingEnsembleChart.EndPaneResize();
            if (saveTradingLayout)
                CaptureTradingLayout();
            if (saveTradingLayout || saveTwoPaneLayout)
                SavePersistentLayout();
            SetHorizontalDragCursor(false);
        };
        Unloaded += (_, _) =>
        {
            SavePersistentLayout();
            SetHorizontalDragCursor(false);
            _calculationCts?.Cancel();
            _contangoLoadCts?.Cancel();
            _tradingEnsembleCts?.Cancel();
            _tradingForecastTimer?.Stop();
        };
        SetPlaceholders();
        UpdateOverviewBreadthMode();
        ApplySentimentOptions();
        PopulateMarketSetupIndicatorSelectors();
        PopulateMarketSetupRuleTree();
        ApplyMarketSetupOptions();
    }

    private void OrderTabsByAnalysisPriority()
    {
        TabItem[] orderedTabs = [FuzzyTab, TradingEnsemblesTab, OverviewTab, ContangoTab, TitanicTab, ZweigTab, SentimentTab, MarketSetupTab];
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
                   $"{_layoutState.ContangoTopRatio.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   $"{_layoutState.TitanicTopRatio.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   $"{_layoutState.ZweigTopRatio.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                   _layoutState.SentimentTopRatio.ToString("0.####", CultureInfo.InvariantCulture);
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
    private HoldConfidenceSeries? _rules1Confidence;
    private HoldConfidenceSeries? _rules2Confidence;
    private bool _scoreSelectionReady;
    private TechnicalIndicatorSeries? _technicalIndicators;
    private SentimentSeries? _sentimentSeries;
    private MarketSetupSeries? _marketSetupSeries;
    private BreezeRuleEntry? _selectedMarketSetupRule;
    private BreezeMarketEntry? _selectedMarketSetupEntry;
    private readonly Dictionary<string, string> _selectedMarketSetupIds = new(StringComparer.Ordinal);
    private readonly Dictionary<SentimentIndicator, CheckBox> _marketSetupExtraSelectors = new();
    private DataSet? _sentimentDataSet;
    private TitanicSeries? _titanicSeries;
    private ZweigSeries? _zweigSeries;
    private ZweigSeries? _zweigStrongSeries;
    private ZweigSeries? _zweigWorkingSeries;
    private IReadOnlyDictionary<string, TradingEnsembleSeries> _tradingEnsembles =
        new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<DateTime, IReadOnlyList<double>> _tradingChandelierStops =
        new Dictionary<DateTime, IReadOnlyList<double>>();
    private IReadOnlyDictionary<DateTime, IReadOnlyList<double>> _tradingChandelierExecutions =
        new Dictionary<DateTime, IReadOnlyList<double>>();
    private IReadOnlyDictionary<DateTime, TradingActivePosition> _tradingActualActivePositions =
        new Dictionary<DateTime, TradingActivePosition>();
    private IReadOnlyDictionary<DateTime, TradingActivePosition> _tradingTargetActivePositions =
        new Dictionary<DateTime, TradingActivePosition>();
    private CancellationTokenSource? _tradingEnsembleCts;
    private bool _tradingEnsembleCalculationStarted;
    private int _tradingEnsembleCalculationGeneration;
    private int _marketDataGeneration;
    private int _tradingEnsembleDataGeneration = -1;
    private bool _tradingInputsReady;
    private string _tradingInputStatus = "Ожидайте завершения общего расчёта.";
    private bool _initialCalculationStarted;
    private int _tradingForecastDays = 10;
    private int _tradingPerformanceBars = DefaultTradingPerformanceBars;
    private DispatcherTimer? _tradingForecastTimer;
    private bool _tradingGpuDirectoryTreeInitialized;
    private bool _tradingTorchDirectoryTreeInitialized;
    private bool _tradingEncogDirectoryTreeInitialized;
    private string _tradingAnalysisReportText = string.Empty;
    private static readonly object DirectoryTreePlaceholder = new();
    private int _visibleBarCount = DefaultVisibleBarCount;
    private DateTime? _cursorDate;
    private bool _cursorPinned;
    private bool _horizontalDragCursorActive;
    private Cursor? _previousOverrideCursor;
    private bool _isPanning;
    private bool _isTradingPaneResizing;
    private FrameworkElement? _activeTwoPaneResizeChart;
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
    private string _modelStatusMessage = "Оценки моделей ещё не рассчитаны.";
    private BreadthProgressLevel _modelStatusLevel = BreadthProgressLevel.Waiting;
    private bool _isCalculationInProgress;
    private VixTermStructureStore? _vixTermStore;
    private VixCurveStateSeries? _contangoStates;
    private Task<VixTermStructureStore>? _vixTermLoadTask;
    private Task<string>? _vixTermExportTask;
    private CancellationTokenSource? _contangoLoadCts;
    private bool _contangoInitializationStarted;
    private bool _updatingOverviewSelection;
    private bool _overviewAtrpLayoutVisible;
    private bool _settingMarketSetupDependencies;
    private readonly MarketSetupSelectionState _marketSetupSelection = new();
    private MarketSetupPaneLayout? _marketSetupPaneLayout;
    private bool _marketSetupRefreshScheduled;
    private GridLength _overviewCombinedHeight = new(2.0, GridUnitType.Star);
    private GridLength _overviewAtrpHeight = new(1.0, GridUnitType.Star);
    private MarketResearchSummary? _marketResearch;

    private string CurrentScoreModel => Rules2Selector?.IsChecked == true ? "Rules2" : "Rules1";

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
        double tradingAnalysisRatio = Math.Clamp(state.TradingAnalysisRatio, 0.16, 0.60);
        TradingAnalysisColumn.Width = new GridLength(tradingAnalysisRatio, GridUnitType.Star);
        TradingChartColumn.Width = new GridLength(1.0 - tradingAnalysisRatio, GridUnitType.Star);
        TradingEnsembleChart.SetPaneBoundaries(
            state.TradingPriceBoundary,
            state.TradingEnsembleBoundary,
            state.TradingDecileBoundary);
        TradingEnsembleChart.SetCollapsedPanes(state.TradingCollapsed);
        ApplyOverviewRatio(state.OverviewCombinedRatio);
        ApplyStarRatio(OverviewFullLeftColumn, OverviewFullRightColumn,
            state.OverviewFullLeftRatio, 0.20, 0.80);
        ApplyStarRatio(OverviewFullTopRow, OverviewFullBottomRow,
            state.OverviewFullTopRatio, 0.20, 0.80);
        ApplyStarRatio(ContangoLeftColumn, ContangoRightColumn,
            state.ContangoLeftRatio, 0.20, 0.80);
        ApplyStarRatio(ContangoTopRow, ContangoBottomRow,
            state.ContangoTopRatio, 0.20, 0.85);
        TitanicChart.SetPaneRatio(state.TitanicTopRatio);
        ZweigChart.SetPaneRatio(state.ZweigTopRatio);
        ApplyStarRatio(SentimentTopRow, SentimentBottomRow,
            state.SentimentTopRatio, 0.20, 0.85);
        ApplyStarRatio(MarketSetupAnalysisColumn, MarketSetupChartColumn,
            state.MarketSetupAnalysisRatio, 0.005, 0.90);
        ApplyStarRatio(MarketSetupTopRow, MarketSetupBottomRow,
            state.MarketSetupTopRatio, 0.20, 0.85);
        double indicatorsShare = double.IsFinite(state.MarketSetupIndicatorsShare)
            ? Math.Clamp(state.MarketSetupIndicatorsShare, 0.001, 0.999) : 0.30;
        double rulesShare = double.IsFinite(state.MarketSetupRulesShare)
            ? Math.Clamp(state.MarketSetupRulesShare, 0.001, 0.999) : 0.325;
        if (indicatorsShare + rulesShare > 1.0)
        {
            double totalShare = indicatorsShare + rulesShare;
            indicatorsShare /= totalShare;
            rulesShare /= totalShare;
        }
        MarketSetupIndicatorsColumn.Width = new GridLength(indicatorsShare, GridUnitType.Star);
        MarketSetupRulesColumn.Width = new GridLength(rulesShare, GridUnitType.Star);
        MarketSetupDescriptionColumn.Width = new GridLength(
            Math.Max(0.0, 1.0 - indicatorsShare - rulesShare), GridUnitType.Star);
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
        else if (ReferenceEquals(sender, OverviewFullVerticalSplitter))
            _layoutState.OverviewFullLeftRatio = Ratio(
                OverviewFullLeftColumn.ActualWidth, OverviewFullRightColumn.ActualWidth,
                _layoutState.OverviewFullLeftRatio);
        else if (ReferenceEquals(sender, OverviewFullHorizontalSplitter))
            _layoutState.OverviewFullTopRatio = Ratio(
                OverviewFullTopRow.ActualHeight, OverviewFullBottomRow.ActualHeight,
                _layoutState.OverviewFullTopRatio);
        else if (ReferenceEquals(sender, FuzzyHorizontalSplitter))
            _layoutState.FuzzyAnalysisRatio = Ratio(
                FuzzyAnalysisColumn.ActualWidth, FuzzyChartColumn.ActualWidth,
                _layoutState.FuzzyAnalysisRatio);
        else if (ReferenceEquals(sender, TradingAnalysisSplitter))
            _layoutState.TradingAnalysisRatio = Ratio(
                TradingAnalysisColumn.ActualWidth, TradingChartColumn.ActualWidth,
                _layoutState.TradingAnalysisRatio);
        else if (ReferenceEquals(sender, ContangoVerticalSplitter))
            _layoutState.ContangoLeftRatio = Ratio(
                ContangoLeftColumn.ActualWidth, ContangoRightColumn.ActualWidth,
                _layoutState.ContangoLeftRatio);
        else if (ReferenceEquals(sender, ContangoLeftHorizontalSplitter) ||
                 ReferenceEquals(sender, ContangoRightHorizontalSplitter))
            _layoutState.ContangoTopRatio = Ratio(
                ContangoTopRow.ActualHeight, ContangoBottomRow.ActualHeight,
                _layoutState.ContangoTopRatio);
        else if (ReferenceEquals(sender, SentimentSplitter))
            _layoutState.SentimentTopRatio = Ratio(
                SentimentTopRow.ActualHeight, SentimentBottomRow.ActualHeight,
                _layoutState.SentimentTopRatio);
        else if (ReferenceEquals(sender, MarketSetupVerticalSplitter))
            _layoutState.MarketSetupAnalysisRatio = Ratio(
                MarketSetupAnalysisColumn.ActualWidth, MarketSetupChartColumn.ActualWidth,
                _layoutState.MarketSetupAnalysisRatio);
        else if (ReferenceEquals(sender, MarketSetupIndicatorsSplitter) ||
                 ReferenceEquals(sender, MarketSetupRulesSplitter))
        {
            double total = MarketSetupIndicatorsColumn.ActualWidth + MarketSetupRulesColumn.ActualWidth +
                           MarketSetupDescriptionColumn.ActualWidth;
            if (total > 1.0)
            {
                _layoutState.MarketSetupIndicatorsShare = MarketSetupIndicatorsColumn.ActualWidth / total;
                _layoutState.MarketSetupRulesShare = MarketSetupRulesColumn.ActualWidth / total;
            }
        }
        else if (ReferenceEquals(sender, MarketSetupHorizontalSplitter))
            _layoutState.MarketSetupTopRatio = Ratio(
                MarketSetupTopRow.ActualHeight, MarketSetupBottomRow.ActualHeight,
                _layoutState.MarketSetupTopRatio);
        else if (ReferenceEquals(sender, MarketSetupAtrpSplitter))
        {
            _layoutState.MarketSetupIndicatorsRatio = Ratio(
                MarketSetupIndicatorRow.ActualHeight, MarketSetupAtrpRow.ActualHeight,
                _layoutState.MarketSetupIndicatorsRatio);
            UpdateMarketSetupAtrpLayout();
        }
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
        ratio = double.IsFinite(ratio) ? Math.Clamp(ratio, minimum, maximum) : Math.Clamp(0.5, minimum, maximum);
        first.Width = new GridLength(ratio, GridUnitType.Star);
        second.Width = new GridLength(1.0 - ratio, GridUnitType.Star);
    }

    private static void ApplyStarRatio(RowDefinition first, RowDefinition second,
        double ratio, double minimum, double maximum)
    {
        ratio = double.IsFinite(ratio) ? Math.Clamp(ratio, minimum, maximum) : Math.Clamp(2.0 / 3.0, minimum, maximum);
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
        ResolveDefaultDataSets();

        string[] saved = _workspaceSelection.Split('|');
        UseWorkingZweig.IsChecked = string.Equals(saved.ElementAtOrDefault(2), "Working", StringComparison.OrdinalIgnoreCase);
        PositionProfile.SelectedIndex = _persistentLayoutLoaded ? Math.Clamp(_layoutState.ScoreProfile, 0, 2) : int.TryParse(saved.ElementAtOrDefault(3), out int savedProfile)
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
        if (!_persistentLayoutLoaded &&
            double.TryParse(saved.ElementAtOrDefault(11), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedTitanicRatio))
        {
            _layoutState.TitanicTopRatio = savedTitanicRatio;
            TitanicChart.SetPaneRatio(savedTitanicRatio);
        }
        if (!_persistentLayoutLoaded &&
            double.TryParse(saved.ElementAtOrDefault(12), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedZweigRatio))
        {
            _layoutState.ZweigTopRatio = savedZweigRatio;
            ZweigChart.SetPaneRatio(savedZweigRatio);
        }
        if (!_persistentLayoutLoaded &&
            double.TryParse(saved.ElementAtOrDefault(13), NumberStyles.Float, CultureInfo.InvariantCulture, out double savedSentimentRatio))
        {
            _layoutState.SentimentTopRatio = savedSentimentRatio;
            ApplyStarRatio(SentimentTopRow, SentimentBottomRow, savedSentimentRatio, 0.20, 0.85);
        }
        UpdateZweigModeText();
        TradingGpuDirectory.Text = @"D:\TorchSharp4GPU";
        TradingTorchDirectory.Text = @"D:\TorchSharp";
        TradingEncogDirectory.Text = @"D:\model_ensemble_Encog";
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

    private void ResolveDefaultDataSets()
    {
        List<DataSet> dataSets = WLHost.Instance.DataSets
            .Where(item => !item.IsHidden)
            .OrderBy(item => item.QualifiedName)
            .ToList();
        SpDataSet.ItemsSource = dataSets;
        NasdaqDataSet.ItemsSource = dataSets;
        SpDataSet.SelectedItem = DefaultDataSets.FindIndex(dataSets, "S&P 500");
        NasdaqDataSet.SelectedItem = DefaultDataSets.FindIndex(dataSets, "NASDAQ 100");
        _sentimentDataSet = FindDataSet(dataSets, null, "setiment", "sentiment");
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        // Providers can finish registering DataSets after the window's Loaded event.
        ResolveDefaultDataSets();
        if (SpDataSet.SelectedItem is not DataSet sp || NasdaqDataSet.SelectedItem is not DataSet nasdaq)
        {
            SetStatus("Не найдены обязательные источники S&P 500:WealthData и NASDAQ 100:WealthData.", BreadthProgressLevel.Error);
            return;
        }
        if (_sentimentDataSet is not DataSet sentimentDataSet)
        {
            SetStatus("Sentiment DataSet 'setiment'/'sentiment' was not found.", BreadthProgressLevel.Error);
            return;
        }

        ResetContangoForRecalculation();

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
            DateTime spyHistoryStart = VixTermStructureStore.FindFirstSourceDate(VixTermDataDirectory)
                ?.AddDays(-7) ?? technicalWarmupStart;
            if (spyHistoryStart > technicalWarmupStart)
                spyHistoryStart = technicalWarmupStart;
            BreadthCalculationResult spResult = await Task.Run(() =>
                BreadthCalculator.Calculate(sp, [200, 100, 50], start, end, progress, cancellationToken),
                cancellationToken);
            IReadOnlyList<BreadthSeries> spSeries = spResult.Series;

            BreadthSeries sp200Series = spSeries.Single(series => series.Period == 200);
            BreadthSeries sp100Series = spSeries.Single(series => series.Period == 100);
            BreadthSeries sp50Series = spSeries.Single(series => series.Period == 50);
            SetStatus("S&P 500 breadth ready. Loading SPY and NYSE Zweig histories …");

            Task<BarHistory> spyTask = Task.Run(() =>
                BreadthCalculator.LoadRawHistory(sp, "SPY", spyHistoryStart, end), cancellationToken);
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
                catch (Exception ex)
                {
                    WLHost.Instance.AddLogItem("Breeze", $"SVXY history unavailable: {ex.Message}", WLColor.Orange, ex, this);
                    return null;
                }
            }, cancellationToken);
            await Task.WhenAll(spyTask, advancingTask, decliningTask);
            BarHistory sp500Bars = await spyTask;
            PriceSeries sp500Price = BreadthCalculator.ToPriceSeries(sp500Bars, "S&P 500 (SPY)");
            HashSet<DateTime> spyTradingDates = sp500Price.Points
                .Select(point => point.Date.Date)
                .ToHashSet();
            PriceSeries? svxyPrice = await svxyTask;
            PriceSeries advancingSeries = await advancingTask;
            PriceSeries decliningSeries = await decliningTask;
            TechnicalIndicatorSeries technicalIndicators = TechnicalIndicatorCalculator.Calculate(sp500Price);
            ZweigSeries zweigStrongSeries = ZweigBreadthCalculator.Calculate(
                advancingSeries, decliningSeries, spyTradingDates, ZweigMode.Strong, start, end);
            ZweigSeries zweigWorkingSeries = ZweigBreadthCalculator.Calculate(
                advancingSeries, decliningSeries, spyTradingDates, ZweigMode.Working, start, end);

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
            Task<BarHistory> newHighsTask = Task.Run(() =>
                BreadthCalculator.LoadRawHistory(nasdaq, "$NASDAQ_NEWHI", technicalWarmupStart, end),
                cancellationToken);
            Task<BarHistory> newLowsTask = Task.Run(() =>
                BreadthCalculator.LoadRawHistory(nasdaq, "$NASDAQ_NEWLO", technicalWarmupStart, end),
                cancellationToken);
            await Task.WhenAll(qqqTask, newHighsTask, newLowsTask);
            BarHistory nasdaqNewHighs = await newHighsTask;
            BarHistory nasdaqNewLows = await newLowsTask;
            TitanicSeries titanicSeries = TitanicSyndromeCalculator.Calculate(
                await qqqTask,
                BreadthCalculator.ToPriceSeries(nasdaqNewHighs, "Nasdaq New Highs"),
                BreadthCalculator.ToPriceSeries(nasdaqNewLows, "Nasdaq New Lows"),
                nasdaq50Series, spyTradingDates, start, end);

            SetStatus("Titanic ready. Loading Sentiment histories and calculating indicators …");
            Task<BarHistory> cnnFearGreedTask = Task.Run(() => BreadthCalculator.LoadRawHistory(
                sentimentDataSet, SentimentCalculator.CnnFearGreedSymbol, technicalWarmupStart, end), cancellationToken);
            Task<BarHistory> riskMeasureTask = Task.Run(() => BreadthCalculator.LoadRawHistory(
                sentimentDataSet, SentimentCalculator.RiskMeasureSymbol, technicalWarmupStart, end), cancellationToken);
            Task<BarHistory> panicEuphoriaTask = Task.Run(() => BreadthCalculator.LoadRawHistory(
                sentimentDataSet, SentimentCalculator.PanicEuphoriaSymbol, technicalWarmupStart, end), cancellationToken);
            Task<BarHistory> dumbMoneyTask = Task.Run(() => BreadthCalculator.LoadRawHistory(
                sentimentDataSet, SentimentCalculator.DumbMoneySymbol, technicalWarmupStart, end), cancellationToken);
            Task<BarHistory> smartMoneyTask = Task.Run(() => BreadthCalculator.LoadRawHistory(
                sentimentDataSet, SentimentCalculator.SmartMoneySymbol, technicalWarmupStart, end), cancellationToken);
            Task<BarHistory>[] sectorTasks = new[] { "XLV", "XLU", "XLE", "XLP" }
                .Select(symbol => Task.Run(() => BreadthCalculator.LoadRawHistory(
                    sp, symbol, technicalWarmupStart, end), cancellationToken))
                .ToArray();
            await Task.WhenAll(new Task[]
            {
                cnnFearGreedTask, riskMeasureTask, panicEuphoriaTask, dumbMoneyTask, smartMoneyTask
            }.Concat(sectorTasks));
            var marketBreadthHistories = new List<BarHistory> { sp500Bars };
            marketBreadthHistories.AddRange(sectorTasks.Select(task => task.Result));
            SentimentSeries sentimentSeries = await Task.Run(() => SentimentCalculator.Calculate(
                sp500Bars,
                cnnFearGreedTask.Result,
                riskMeasureTask.Result,
                panicEuphoriaTask.Result,
                dumbMoneyTask.Result,
                smartMoneyTask.Result,
                nasdaqNewHighs,
                nasdaqNewLows,
                marketBreadthHistories,
                start,
                end), cancellationToken);

            TradingInputReadiness tradingInputReadiness = EvaluateTradingInputReadiness(
                sp500Bars,
                ("CNN Fear Greed", cnnFearGreedTask.Result, 0),
                ("Risk Measure", riskMeasureTask.Result, 0),
                ("Panic Euphoria", panicEuphoriaTask.Result, 7),
                ("NASDAQ New Highs", nasdaqNewHighs, 0),
                ("NASDAQ New Lows", nasdaqNewLows, 0),
                ("XLV", sectorTasks[0].Result, 0),
                ("XLU", sectorTasks[1].Result, 0),
                ("XLE", sectorTasks[2].Result, 0),
                ("XLP", sectorTasks[3].Result, 0));

            cancellationToken.ThrowIfCancellationRequested();
            _marketDataGeneration++;
            _tradingInputsReady = tradingInputReadiness.IsReady;
            _tradingInputStatus = tradingInputReadiness.Message;
            InvalidateTradingEnsembleResults();
            _sp200Series = sp200Series;
            _sp100Series = sp100Series;
            _sp50Series = sp50Series;
            _nasdaq100Series = nasdaq100Series;
            _nasdaq50Series = nasdaq50Series;
            _sp500PriceSeries = sp500Price;
            _sp500Bars = sp500Bars;
            _svxyPriceSeries = svxyPrice;
            _technicalIndicators = technicalIndicators;
            _sentimentSeries = sentimentSeries;
            _marketResearch = marketResearch;
            _zweigStrongSeries = zweigStrongSeries;
            _zweigWorkingSeries = zweigWorkingSeries;
            _fuzzyStates = fuzzyStates;
            _titanicSeries = titanicSeries;

            Sp200Chart.SetSeries(sp200Series);
            SentimentSpChart.SetSeries(sp200Series);
            SentimentSpChart.SetTechnicalIndicators(technicalIndicators);
            SentimentSpChart.SetShowEmaOverlays(SentimentEma.IsChecked == true);
            SentimentIndicatorChart.SetSeries(sentimentSeries);
            ApplySentimentOptions();
            MarketSetupSpChart.SetSeries(sp200Series);
            MarketSetupSpChart.SetTechnicalIndicators(technicalIndicators);
            MarketSetupSpChart.SetShowEmaOverlays(MarketSetupEma.IsChecked == true);
            MarketSetupIndicatorChart.SetSeries(sentimentSeries);
            MarketSetupIndicatorChart.SetBreadthSeries(sp50Series, sp100Series, sp200Series, nasdaq100Series);
            MarketSetupAtrpChart.SetSeries(technicalIndicators);
            MarketSetupAtrpChart.SetMasterDates(sp200Series.Points.Select(point => point.Date));
            ApplyMarketSetupOptions();
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
                fuzzyStates, technicalIndicators, sentimentSeries, titanicSeries,
                zweigStrongSeries, zweigWorkingSeries);
            string exportMessage = await Task.Run(
                () => ExportBreadthSeries(exportSnapshot, start, end), cancellationToken);
            RefreshMarketSetupData();
            RefreshHoldConfidence();
            int providerErrors = spResult.FailedHistoryCount + nasdaqResult.FailedHistoryCount;
            string historySummary =
                $"Histories used: S&P {spResult.LoadedHistoryCount}, Nasdaq {nasdaqResult.LoadedHistoryCount}.";
            SetModelAwareStatus(
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
                "Market Breeze",
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
        => SetStatus(message, level);

    private void SetModelAwareStatus(string message, BreadthProgressLevel level = BreadthProgressLevel.Normal)
    {
        BreadthProgressLevel combined = level == BreadthProgressLevel.Error || _modelStatusLevel == BreadthProgressLevel.Error
            ? BreadthProgressLevel.Error
            : level == BreadthProgressLevel.Waiting || _modelStatusLevel == BreadthProgressLevel.Waiting
                ? BreadthProgressLevel.Waiting
                : BreadthProgressLevel.Normal;
        SetStatus(string.IsNullOrWhiteSpace(_modelStatusMessage)
            ? message
            : $"{message} {_modelStatusMessage}", combined);
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

    private void DecreaseTradingPerformanceBarsClick(object sender, RoutedEventArgs e) =>
        ChangeTradingPerformanceBars(-TradingPerformanceBarStep);

    private void IncreaseTradingPerformanceBarsClick(object sender, RoutedEventArgs e) =>
        ChangeTradingPerformanceBars(TradingPerformanceBarStep);

    private void ChangeTradingPerformanceBars(int delta)
    {
        int maximum = _sp500PriceSeries?.Points.Count is > 1
            ? Math.Max(MinimumTradingPerformanceBars,
                ((_sp500PriceSeries.Points.Count - 1) / TradingPerformanceBarStep) * TradingPerformanceBarStep)
            : 5000;
        int next = Math.Clamp(_tradingPerformanceBars + delta,
            MinimumTradingPerformanceBars, maximum);
        if (next == _tradingPerformanceBars)
            return;
        _tradingPerformanceBars = next;
        TradingPerformanceBarsText.Text = next.ToString(CultureInfo.InvariantCulture);
        TradingEnsembleChart.SetRecentPerformanceBars(next);
        UpdateTradingAnalysis(SelectedTradingPackageKeys());
    }

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

        if (IsDescendantOf(source, TitanicChart) && TitanicChart.TryBeginPaneResize(e.GetPosition(TitanicChart)))
        {
            _activeTwoPaneResizeChart = TitanicChart;
            _previousOverrideCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.SizeNS;
            BreadthTabs.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (IsDescendantOf(source, ZweigChart) && ZweigChart.TryBeginPaneResize(e.GetPosition(ZweigChart)))
        {
            _activeTwoPaneResizeChart = ZweigChart;
            _previousOverrideCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.SizeNS;
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
        if (_activeTwoPaneResizeChart is not null)
        {
            if (e.LeftButton == MouseButtonState.Pressed && BreadthTabs.IsMouseCaptured)
            {
                if (ReferenceEquals(_activeTwoPaneResizeChart, TitanicChart))
                    TitanicChart.ResizePane(e.GetPosition(TitanicChart));
                else if (ReferenceEquals(_activeTwoPaneResizeChart, ZweigChart))
                    ZweigChart.ResizePane(e.GetPosition(ZweigChart));
                e.Handled = true;
                return;
            }
            FinishTwoPaneResize();
        }
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
        if (_activeTwoPaneResizeChart is not null)
        {
            FinishTwoPaneResize();
            e.Handled = true;
            return;
        }
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

    private void FinishTwoPaneResize()
    {
        if (ReferenceEquals(_activeTwoPaneResizeChart, TitanicChart))
        {
            TitanicChart.EndPaneResize();
            _layoutState.TitanicTopRatio = TitanicChart.GetPaneRatio();
        }
        else if (ReferenceEquals(_activeTwoPaneResizeChart, ZweigChart))
        {
            ZweigChart.EndPaneResize();
            _layoutState.ZweigTopRatio = ZweigChart.GetPaneRatio();
        }
        _activeTwoPaneResizeChart = null;
        Mouse.OverrideCursor = _previousOverrideCursor;
        _previousOverrideCursor = null;
        SavePersistentLayout();
        if (BreadthTabs.IsMouseCaptured)
            BreadthTabs.ReleaseMouseCapture();
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
            else if (ReferenceEquals(selectedTab, SentimentTab))
            {
                SentimentSpChart.Refresh();
                SentimentIndicatorChart.Refresh();
            }
            else if (ReferenceEquals(selectedTab, MarketSetupTab))
            {
                ScheduleMarketSetupRefresh();
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
        TradingCursorValuesText.Visibility = tradingEnsemble ? Visibility.Visible : Visibility.Collapsed;
        TradingPerformancePeriodControls.Visibility = tradingEnsemble ? Visibility.Visible : Visibility.Collapsed;
        VisibleBarsText.Visibility = Visibility.Visible;
        if (_cursorDate.HasValue)
        {
            if (tradingEnsemble)
                UpdateTradingCursorValues(_cursorDate.Value);
            else
                ApplyCursorValues(_cursorDate.Value);
        }
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

    private void ResetContangoForRecalculation()
    {
        _contangoLoadCts?.Cancel();
        _contangoLoadCts?.Dispose();
        _contangoLoadCts = null;
        _vixTermLoadTask = null;
        _vixTermExportTask = null;
        _vixTermStore = null;
        _contangoStates = null;
        _vixPriceSeries = null;
        _contangoInitializationStarted = false;
        FuzzyChart?.SetContangoStates(null);
        ContangoSpChart?.SetVixCurveStates(null, showVixRibbon: false);
        ContangoSvxyChart?.SetVixCurveStates(null, showVixRibbon: true, showSvxyRibbon: true,
            showLegacyVixRibbon: true);
    }

    private async Task EnsureContangoLoadedAsync()
    {
        if (_vixTermStore is not null)
        {
            if (_contangoStates is null)
            {
                CancellationToken token = _contangoLoadCts?.Token ?? CancellationToken.None;
                IReadOnlyDictionary<DateTime, VixMarketStressContext> stressContexts = BuildContangoStressContexts();
                _contangoStates = await Task.Run(
                    () => _vixTermStore.BuildStateSeries(token, stressContexts), token);
                FuzzyChart.SetContangoStates(_contangoStates);
                RefreshHoldConfidence();
                ContangoSpChart.SetVixCurveStates(_contangoStates, showVixRibbon: false);
                ContangoSvxyChart.SetVixCurveStates(_contangoStates, showVixRibbon: true,
                    showSvxyRibbon: true, showLegacyVixRibbon: true);
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
            PriceSeries spTradingCalendar = _sp500PriceSeries
                ?? throw new InvalidOperationException("SPY bars are unavailable for the Contango trading calendar.");
            _vixTermStore.SetTradingCalendar(spTradingCalendar.Points.Select(point => point.Date));
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
            IReadOnlyDictionary<DateTime, VixMarketStressContext> stressContexts = BuildContangoStressContexts();
            _contangoStates = await Task.Run(
                () => _vixTermStore.BuildStateSeries(stateToken, stressContexts), stateToken);
            FuzzyChart.SetContangoStates(_contangoStates);
            RefreshHoldConfidence();
            ContangoSpChart.SetVixCurveStates(_contangoStates, showVixRibbon: false);
            ContangoSvxyChart.SetVixCurveStates(_contangoStates, showVixRibbon: true,
                showSvxyRibbon: true, showLegacyVixRibbon: true);
            if (_cursorDate.HasValue)
                ApplyCursorValues(_cursorDate.Value);
            UpdateContangoSnapshot();
            SetModelAwareStatus($"Contango ready: {_vixTermStore.TradingDateCount:N0} SPY trading dates; " +
                       $"excluded {_vixTermStore.ExcludedNonTradingDateCount:N0} non-trading VIX files; " +
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
        if (_vixTermStore is null || !RefreshCyclePolicy.CanStartExport(_vixTermExportTask))
            return;

        CancellationToken token = _contangoLoadCts?.Token ?? CancellationToken.None;
        FuzzyMarketStateSeries? fuzzyStates = _fuzzyStates;
        TitanicSeries? titanicSeries = _titanicSeries;
        ZweigSeries? zweigSeries = _zweigSeries;
        VixCurveStateSeries? curveStates = _contangoStates;
        HoldConfidenceSeries? holdConfidence = _rules1Confidence;
        Task<string> exportTask = Task.Run(() =>
        {
            string wideResult = _vixTermStore.ExportWideCsv(VixTermExportPath, token);
            string stateResult = curveStates is null
                ? "contango state export pending main calculation"
                : _vixTermStore.ExportStateCsv(VixCurveStateExportPath, curveStates, token);
            string rules2Result = curveStates is null
                ? "Rules2 VIX export pending main calculation"
                : Rules2VixExport.Export(
                    VixTermDataDirectory, Rules2HoldConfidenceCalculator.CurvePath, curveStates, token);
            string componentResult = fuzzyStates is not null && holdConfidence is not null &&
                                     titanicSeries is not null && zweigSeries is not null && curveStates is not null
                ? WritePhaseModelComponentsCsv(
                    PhaseModelComponentsExportPath, fuzzyStates, holdConfidence,
                    titanicSeries, zweigSeries, curveStates)
                : "phase components pending main calculation";
            return $"{wideResult}; {stateResult}; {rules2Result}; {componentResult}";
        }, token);
        _vixTermExportTask = exportTask;
        try
        {
            string result = await exportTask;
            // The shared research model reads the just-promoted snapshots.
            // Recalculate now so the DLL and standalone strategy use the same
            // current dates, not the previous wide VIX export.
            RefreshHoldConfidence();
            RefreshMarketSetupData();
            SetModelAwareStatus($"Contango ready. {result}.");
        }
        catch (OperationCanceledException)
        {
            // Window closing cancels the background export.
        }
        catch (Exception ex)
        {
            SetStatus($"CONTANGO EXPORT FAILED: {ex.Message}", BreadthProgressLevel.Error);
        }
        finally
        {
            if (ReferenceEquals(_vixTermExportTask, exportTask))
                _vixTermExportTask = null;
        }
    }

    private IReadOnlyDictionary<DateTime, VixMarketStressContext> BuildContangoStressContexts()
    {
        if (_fuzzyStates is null || _technicalIndicators is null)
            return new Dictionary<DateTime, VixMarketStressContext>();

        var technicalByDate = _technicalIndicators.Points.ToDictionary(point => point.Date);
        FuzzyMarketStatePoint[] prices = _fuzzyStates.Points.OrderBy(point => point.Date).ToArray();
        IReadOnlyDictionary<DateTime, PricePoint> vixByDate = _vixPriceSeries?.Points
            .GroupBy(point => point.Date.Date)
            .ToDictionary(group => group.Key, group => group.Last())
            ?? new Dictionary<DateTime, PricePoint>();
        var result = new Dictionary<DateTime, VixMarketStressContext>();
        for (int index = 1; index < prices.Length; index++)
        {
            FuzzyMarketStatePoint current = prices[index];
            FuzzyMarketStatePoint previous = prices[index - 1];
            if (previous.SpyClose <= 0 || !technicalByDate.TryGetValue(current.Date, out TechnicalIndicatorPoint? technical))
                continue;
            double? vixHighRisePct = null;
            if (vixByDate.TryGetValue(current.Date.Date, out PricePoint? currentVix) &&
                vixByDate.TryGetValue(previous.Date.Date, out PricePoint? previousVix) &&
                previousVix.Close > 0 && currentVix.High > 0)
                vixHighRisePct = 100.0 * (currentVix.High / previousVix.Close - 1.0);
            result[current.Date] = new VixMarketStressContext(
                100.0 * (current.SpyClose / previous.SpyClose - 1.0),
                technical.Atrp1,
                vixHighRisePct);
        }
        return result;
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
            ContangoChart.SetSnapshot(_vixTermStore.GetExact(date));
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

    private void SentimentOptionsChanged(object sender, RoutedEventArgs e) => ApplySentimentOptions();

    private void ApplySentimentOptions()
    {
        if (SentimentIndicatorChart is null || SentimentSpChart is null)
            return;

        var selected = new List<SentimentIndicator>();
        if (SentimentCnnFearGreed?.IsChecked == true) selected.Add(SentimentIndicator.CnnFearGreed);
        if (SentimentRiskMeasure?.IsChecked == true) selected.Add(SentimentIndicator.RiskMeasure);
        if (SentimentPanicEuphoria?.IsChecked == true) selected.Add(SentimentIndicator.PanicEuphoria);
        if (SentimentArsi?.IsChecked == true) selected.Add(SentimentIndicator.Arsi);
        if (SentimentMyRsi?.IsChecked == true) selected.Add(SentimentIndicator.MyRsi);
        if (SentimentTrendScore?.IsChecked == true) selected.Add(SentimentIndicator.TrendScore);
        if (SentimentHurst?.IsChecked == true) selected.Add(SentimentIndicator.Hurst);
        if (SentimentCmf?.IsChecked == true) selected.Add(SentimentIndicator.Cmf);
        if (SentimentDecibel?.IsChecked == true) selected.Add(SentimentIndicator.Decibel);
        if (SentimentJocker?.IsChecked == true) selected.Add(SentimentIndicator.Jocker);
        if (SentimentMarketBreadth?.IsChecked == true) selected.Add(SentimentIndicator.MarketBreadth);
        if (SentimentDumbMoney?.IsChecked == true) selected.Add(SentimentIndicator.DumbMoney);
        if (SentimentSmartMoney?.IsChecked == true) selected.Add(SentimentIndicator.SmartMoney);
        SentimentIndicatorChart.SetSelectedIndicators(selected);
        SentimentSpChart.SetShowEmaOverlays(SentimentEma?.IsChecked == true);
    }

    private void PopulateMarketSetupRuleTree()
    {
        if (MarketSetupRuleTree is null)
            return;

        string model = CurrentScoreModel;
        _selectedMarketSetupIds.TryGetValue(model, out string? selectedId);
        HashSet<string> expandedFamilies = MarketSetupRuleTree.Items.OfType<TreeViewItem>()
            .Where(item => item.IsExpanded && item.Tag is string)
            .Select(item => (string)item.Tag).ToHashSet(StringComparer.Ordinal);
        bool activeOnly = MarketSetupActiveRules?.IsChecked == true;
        MarketSetupRuleTree.Items.Clear();
        BreezeMarketEntry? firstRule = null;
        TreeViewItem? firstRuleItem = null;
        TreeViewItem? selectedRuleItem = null;
        BreezeMarketCatalogSet catalog = model == "Rules2" ? Rules2MarketCatalog.Current : BreezeMarketCatalog.Current;
        MarketSetupCatalogSummary.Text = $"{model}: всего {catalog.Entries.Count}; расчётных {catalog.RuntimeCount}; активных {catalog.RuntimeActive}";
        MarketSetupCatalogSummary.ToolTip = model == "Rules2"
            ? "Точные факторы замороженной Rules2. Активные — факторы с ненулевым весом; профили меняют пороги, но не веса."
            : $"Общий каталог {catalog.Version}: {catalog.Entries.Count} записей. " +
              $"Расчётных детекторов {catalog.RuntimeCount}; исходных весовых признаков {BreezeRuleCatalog.Current.Rules.Count}. " +
              $"Принятая исследовательская модель: {catalog.AcceptedSelected} правил, {catalog.AcceptedNonzero} с ненулевыми весами. " +
              "Фильтр «Активные» относится к трём профилям установленного Breeze, а не к исследовательским весам.";
        foreach (IGrouping<string, BreezeMarketEntry> family in catalog.Entries
                     .Where(rule => !activeOnly || rule.IsActiveInBreeze)
                     .GroupBy(rule => rule.Family))
        {
            var familyItem = new TreeViewItem
            {
                Header = MarketSetupFamilyName(family.Key),
                Tag = family.Key,
                ToolTip = MarketSetupFamilyName(family.Key),
                IsExpanded = activeOnly || expandedFamilies.Contains(family.Key) ||
                             string.Equals(family.Key, "T", StringComparison.Ordinal)
            };
            foreach (BreezeMarketEntry rule in family)
            {
                var ruleItem = new TreeViewItem
                {
                    Header = $"{rule.Number:00}  {rule.Id} — {rule.NameRu}",
                    Tag = rule,
                    ToolTip = rule.NameRu
                };
                familyItem.Items.Add(ruleItem);
                if (string.Equals(rule.Id, selectedId, StringComparison.Ordinal))
                {
                    selectedRuleItem = ruleItem;
                    familyItem.IsExpanded = true;
                }
                if (firstRule is null)
                {
                    firstRule = rule;
                    firstRuleItem = ruleItem;
                }
            }
            MarketSetupRuleTree.Items.Add(familyItem);
        }

        TreeViewItem? selection = selectedRuleItem ?? firstRuleItem;
        if (selection?.Tag is BreezeMarketEntry selectedRule)
        {
            selection.IsSelected = true;
            selection.BringIntoView();
            SelectMarketSetupRule(selectedRule);
        }
        else
        {
            _selectedMarketSetupRule = null;
            _selectedMarketSetupEntry = null;
            MarketSetupRuleTitle.Text = "Нет активных правил";
            MarketSetupRuleDescription.Text = "Ни одно правило не имеет ненулевого веса в профилях Soft, Standard или Hard.";
            MarketSetupSpChart.SetMarketSetupOverlay(_marketSetupSeries, null);
        }
        UpdateMarketSetupCompactColumns();
    }

    private void MarketSetupActiveRulesChanged(object sender, RoutedEventArgs e) => PopulateMarketSetupRuleTree();

    private void MarketSetupColumnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMarketSetupCompactColumns();

    private void UpdateMarketSetupCompactColumns()
    {
        if (MarketSetupIndicatorSelectors is null || MarketSetupRuleTree is null ||
            MarketSetupIndicatorsTitle is null)
            return;
        bool compactIndicators = MarketSetupIndicatorsColumn.ActualWidth < 100;
        DataTemplate template = (DataTemplate)FindResource(compactIndicators
            ? "MarketSetupCompactToggleTemplate" : "SentimentIndicatorToggleTemplate");
        foreach (CheckBox selector in MarketSetupIndicatorSelectors.Children.OfType<CheckBox>()
                     .Concat(_marketSetupExtraSelectors.Values))
        {
            selector.ContentTemplate = template;
            selector.ToolTip ??= selector.Content?.ToString();
        }
        MarketSetupIndicatorsTitle.Text = compactIndicators ? "И" : "Индикаторы";
        MarketSetupIndicatorsTitle.ToolTip = "Индикаторы";
        bool compactRules = MarketSetupRulesColumn.ActualWidth < 175;
        MarketSetupRuleTree.FontSize = compactRules ? 11 : 12;
        foreach (TreeViewItem family in MarketSetupRuleTree.Items.OfType<TreeViewItem>())
        {
            if (family.Tag is string code)
                family.Header = compactRules ? code : MarketSetupFamilyName(code);
            foreach (TreeViewItem item in family.Items.OfType<TreeViewItem>())
                if (item.Tag is BreezeMarketEntry rule)
                    item.Header = compactRules ? $"{rule.Number:00} {rule.Id}"
                        : $"{rule.Number:00}  {rule.Id} — {rule.NameRu}";
        }
    }

    private static string MarketSetupFamilyName(string family) => family switch
    {
        "T" => "T — Тренд / EMA",
        "E" => "E — События EMA",
        "R" => "R — MyRSI",
        "B" => "B — Широта рынка",
        "P" => "P — Движение цены",
        "V" => "V — Кривая VIX",
        "Z" => "Z — Zweig",
        "I" => "I — Titanic",
        "C" => "C — Составные условия",
        "U" => "U — Исследовательские условия",
        "X" => "X — Гипотезы исполнения",
        "S1" => "S1 — Конструкции Sentiment 1",
        "VB" => "VB — Уточнения широт",
        "PH" => "PH — Подхваты с памятью",
        "PX" => "PX — Уточнения исполнения",
        "EN" => "EN — Добавки ансамбля",
        "ST" => "ST — События сентимента",
        "H" => "H — События и состояния Rules2",
        "F" => "F — Непрерывные факторы Rules2",
        "EMA" => "EMA — Контекст порядка средних Rules2",
        _ => family
    };

    private void MarketSetupRuleSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: BreezeMarketEntry rule })
            SelectMarketSetupRule(rule);
    }

    private void SelectMarketSetupRule(BreezeMarketEntry rule)
    {
        _selectedMarketSetupEntry = rule;
        _selectedMarketSetupRule = rule.RuntimeRule;
        _selectedMarketSetupIds[CurrentScoreModel] = rule.Id;
        MarketSetupRuleTitle.Text = $"{rule.Id} — {rule.NameRu}";
        MarketSetupRuleDescription.Text = DescribeMarketSetupEntry(rule);
        MarketSetupSpChart.SetMarketSetupOverlay(_marketSetupSeries, rule.RuntimeRule);

        _marketSetupSelection.SelectRule(rule.Id, rule.RuntimeRule?.ImplementedFormula ?? rule.Definition);
        _settingMarketSetupDependencies = true;
        try
        {
            foreach (CheckBox selector in MarketSetupSelectors())
                selector.IsChecked = _marketSetupSelection.IsSelected(selector.Name);
        }
        finally { _settingMarketSetupDependencies = false; }
        MarketSetupAtrpChart.SetReferenceLevel(rule.Id.StartsWith("T03", StringComparison.Ordinal)
            ? rule.RuntimeRule?.Parameters?.AtrpMaxPercent ?? 2.0 : null);
        ApplyMarketSetupOptions();
        if (_cursorDate.HasValue)
            ApplyCursorValues(_cursorDate.Value);
    }

    private static string BuildMarketSetupRuleDescription(BreezeRuleEntry rule) => MarketSetupDescription.Build(rule);

    private static string DescribeMarketSetupEntry(BreezeMarketEntry rule) =>
        rule.Kind == "rules2" ? Rules2MarketCatalog.Describe(rule) : BreezeMarketCatalog.Describe(rule);

    private void MarketSetupOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_settingMarketSetupDependencies) return;
        if (sender is CheckBox selector && !string.IsNullOrEmpty(selector.Name))
            _marketSetupSelection.SetManual(selector.Name, selector.IsChecked == true);
        ApplyMarketSetupOptions();
    }

    private IEnumerable<CheckBox> MarketSetupSelectors() =>
        MarketSetupIndicatorSelectors.Children.OfType<CheckBox>().Concat(_marketSetupExtraSelectors.Values);

    private void PopulateMarketSetupIndicatorSelectors()
    {
        SentimentIndicator[] additional =
        [
            SentimentIndicator.AroonUp, SentimentIndicator.AroonDown,
            SentimentIndicator.DiMinus, SentimentIndicator.DiPlus,
            SentimentIndicator.CorrelationAngle, SentimentIndicator.CorrelationTrend
        ];
        foreach (SentimentIndicator indicator in additional)
        {
            var selector = new CheckBox
            {
                Name = "MarketSetup" + indicator,
                Content = SentimentIndicatorCatalog.DisplayName(indicator),
                Foreground = new SolidColorBrush(SentimentChart.ColorFor(indicator)),
                Style = (Style)FindResource("SentimentIndicatorToggleStyle"),
                ToolTip = SentimentIndicatorCatalog.DisplayName(indicator)
            };
            selector.Checked += MarketSetupOptionsChanged;
            selector.Unchecked += MarketSetupOptionsChanged;
            _marketSetupExtraSelectors.Add(indicator, selector);
            MarketSetupEnsembleIndicators.Children.Add(selector);
        }
        foreach (CheckBox selector in MarketSetupSelectors())
            _marketSetupSelection.SetManual(selector.Name, selector.IsChecked == true);
    }

    private void ApplyMarketSetupOptions()
    {
        if (_settingMarketSetupDependencies || MarketSetupIndicatorChart is null || MarketSetupSpChart is null)
            return;

        var selected = new List<SentimentIndicator>();
        if (MarketSetupCnnFearGreed?.IsChecked == true) selected.Add(SentimentIndicator.CnnFearGreed);
        if (MarketSetupRiskMeasure?.IsChecked == true) selected.Add(SentimentIndicator.RiskMeasure);
        if (MarketSetupPanicEuphoria?.IsChecked == true) selected.Add(SentimentIndicator.PanicEuphoria);
        if (MarketSetupArsi?.IsChecked == true) selected.Add(SentimentIndicator.Arsi);
        if (MarketSetupMyRsi?.IsChecked == true) selected.Add(SentimentIndicator.MyRsi);
        if (MarketSetupMyRsiFast?.IsChecked == true) selected.Add(SentimentIndicator.MyRsiFast);
        if (MarketSetupTrendScore?.IsChecked == true) selected.Add(SentimentIndicator.TrendScore);
        foreach ((SentimentIndicator indicator, CheckBox selector) in _marketSetupExtraSelectors)
            if (selector.IsChecked == true) selected.Add(indicator);
        if (MarketSetupHurst?.IsChecked == true) selected.Add(SentimentIndicator.Hurst);
        if (MarketSetupCmf?.IsChecked == true) selected.Add(SentimentIndicator.Cmf);
        if (MarketSetupDecibel?.IsChecked == true) selected.Add(SentimentIndicator.Decibel);
        if (MarketSetupJocker?.IsChecked == true) selected.Add(SentimentIndicator.Jocker);
        if (MarketSetupMarketBreadth?.IsChecked == true) selected.Add(SentimentIndicator.MarketBreadth);
        if (MarketSetupDumbMoney?.IsChecked == true) selected.Add(SentimentIndicator.DumbMoney);
        if (MarketSetupSmartMoney?.IsChecked == true) selected.Add(SentimentIndicator.SmartMoney);
        var breadths = new List<SentimentBreadth>();
        if (MarketSetupSp50?.IsChecked == true) breadths.Add(SentimentBreadth.Sp50);
        if (MarketSetupSp100?.IsChecked == true) breadths.Add(SentimentBreadth.Sp100);
        if (MarketSetupSp200?.IsChecked == true) breadths.Add(SentimentBreadth.Sp200);
        if (MarketSetupNasdaq100?.IsChecked == true) breadths.Add(SentimentBreadth.Nasdaq100);
        MarketSetupIndicatorChart.SetSelection(selected, breadths);
        MarketSetupSpChart.SetShowEmaOverlays(MarketSetupEma?.IsChecked == true);
        UpdateMarketSetupAtrpLayout();
        ScheduleMarketSetupRefresh();
        if (_cursorDate.HasValue && ReferenceEquals(BreadthTabs.SelectedItem, MarketSetupTab))
            ApplyCursorValues(_cursorDate.Value);
    }

    private void UpdateMarketSetupAtrpLayout()
    {
        if (MarketSetupAtrpChart is null || MarketSetupAtrpRow is null || MarketSetupIndicatorRow is null)
            return;
        bool showIndicator = MarketSetupSelectors().Any(selector => selector.IsChecked == true &&
            !ReferenceEquals(selector, MarketSetupEma) && !ReferenceEquals(selector, MarketSetupAtrp));
        _marketSetupPaneLayout ??= new MarketSetupPaneLayout(MarketSetupChartGrid, MarketSetupLowerGrid,
            MarketSetupHorizontalSplitter, MarketSetupIndicatorChart, MarketSetupAtrpSplitter, MarketSetupAtrpChart);
        _marketSetupPaneLayout.Apply(showIndicator, MarketSetupAtrp?.IsChecked == true,
            _layoutState.MarketSetupTopRatio, _layoutState.MarketSetupIndicatorsRatio);
    }

    private void ScheduleMarketSetupRefresh()
    {
        _marketSetupRefreshScheduled = true;
        ScheduleHistoryViewport();
    }

    private void RefreshMarketSetupData()
    {
        string model = CurrentScoreModel;
        if (MarketSetupDataLoader.TryLoad(model, out MarketSetupSeries series, out string error))
        {
            _marketSetupSeries = series;
            MarketSetupSpChart.SetMarketSetupOverlay(series, _selectedMarketSetupRule);
        }
        else
        {
            _marketSetupSeries = MarketSetupSeries.Empty;
            if (MarketSetupRuleDescription is not null && _selectedMarketSetupRule is not null)
                MarketSetupRuleDescription.Text = (_selectedMarketSetupEntry is null
                    ? BuildMarketSetupRuleDescription(_selectedMarketSetupRule)
                    : DescribeMarketSetupEntry(_selectedMarketSetupEntry)) +
                    $"\n• Доступность\n{model} недоступна: {error}";
        }
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

    private static TradingInputReadiness EvaluateTradingInputReadiness(
        BarHistory spy,
        params (string Name, BarHistory History, int MaximumCalendarLagDays)[] inputs)
    {
        if (spy.Count == 0)
            return new TradingInputReadiness(false, "История SPY пуста.");

        DateTime spyDate = spy.DateTimes[^1].Date;
        var stale = new List<string>();
        foreach ((string name, BarHistory history, int maximumCalendarLagDays) in inputs)
        {
            DateTime? latest = LatestFiniteHistoryDate(history, spyDate);
            DateTime minimumDate = spyDate.AddDays(-Math.Max(0, maximumCalendarLagDays));
            if (!latest.HasValue || latest.Value < minimumDate)
            {
                stale.Add($"{name}: {(latest.HasValue ? latest.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "нет данных")}");
            }
        }

        return stale.Count == 0
            ? new TradingInputReadiness(true, $"Входы Trading Ensemble готовы до {spyDate:yyyy-MM-dd}.")
            : new TradingInputReadiness(false,
                $"Ожидание входов для SPY {spyDate:yyyy-MM-dd}: {string.Join("; ", stale)}. " +
                "После обновления данных нажмите Recalculate.");
    }

    private static DateTime? LatestFiniteHistoryDate(BarHistory history, DateTime notAfter)
    {
        for (int index = Math.Min(history.Count, history.Close.Count) - 1; index >= 0; index--)
        {
            DateTime date = history.DateTimes[index].Date;
            if (date > notAfter || !double.IsFinite(history.Close[index]))
                continue;
            return date;
        }
        return null;
    }

    private void InvalidateTradingEnsembleResults()
    {
        _tradingEnsembleCalculationGeneration++;
        _tradingEnsembleCts?.Cancel();
        _tradingEnsembleCts?.Dispose();
        _tradingEnsembleCts = null;
        _tradingEnsembleCalculationStarted = false;
        _tradingEnsembleDataGeneration = -1;
        _tradingEnsembles = new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
        _tradingChandelierStops = new Dictionary<DateTime, IReadOnlyList<double>>();
        _tradingChandelierExecutions = new Dictionary<DateTime, IReadOnlyList<double>>();
        _tradingActualActivePositions = new Dictionary<DateTime, TradingActivePosition>();
        _tradingTargetActivePositions = new Dictionary<DateTime, TradingActivePosition>();
        TradingEnsembleChart?.SetData(_tradingEnsembles, _sp500PriceSeries, _technicalIndicators);
        TradingEnsembleChart?.SetChandelierStops(_tradingChandelierStops);
        TradingEnsembleChart?.SetChandelierExecutions(_tradingChandelierExecutions);
        TradingEnsembleChart?.SetActualActivePositions(_tradingActualActivePositions);
        TradingEnsembleChart?.SetTargetActivePositions(_tradingTargetActivePositions);
    }

    private async void StartTradingEnsembleCalculationIfNeeded()
    {
        if (_tradingEnsembleCalculationStarted &&
            _tradingEnsembleDataGeneration == _marketDataGeneration)
            return;
        if (_sp500Bars is null)
        {
            TradingEnsembleChart.SetMessage("Дождитесь завершения общего расчёта. Расчёт девяти ансамблей начнётся при открытой вкладке.");
            SetTradingCalculationStatus("Ожидайте — подготовка данных…", BreadthProgressLevel.Waiting);
            return;
        }
        if (!_tradingInputsReady)
        {
            TradingEnsembleChart.SetMessage(_tradingInputStatus);
            SetTradingCalculationStatus(_tradingInputStatus, BreadthProgressLevel.Waiting);
            return;
        }

        BarHistory barsSnapshot = _sp500Bars;
        int marketGeneration = _marketDataGeneration;
        _tradingEnsembleCalculationStarted = true;
        _tradingEnsembleDataGeneration = marketGeneration;
        int generation = ++_tradingEnsembleCalculationGeneration;
        _tradingEnsembleCts?.Cancel();
        _tradingEnsembleCts?.Dispose();
        _tradingEnsembleCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _tradingEnsembleCts.Token;
        string gpuDirectory = TradingGpuDirectory.Text?.Trim() ?? @"D:\TorchSharp4GPU";
        string torchDirectory = TradingTorchDirectory.Text?.Trim() ?? @"D:\TorchSharp";
        string encogDirectory = TradingEncogDirectory.Text?.Trim() ?? @"D:\model_ensemble_Encog";
        var progress = new Progress<BreadthProgress>(update =>
        {
            if (generation != _tradingEnsembleCalculationGeneration ||
                marketGeneration != _marketDataGeneration)
                return;
            SetStatus(update.Message, update.Level);
            SetTradingCalculationStatus(update.Message, update.Level);
        });
        _tradingEnsembles = new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
        TradingEnsembleChart.SetData(_tradingEnsembles, _sp500PriceSeries, _technicalIndicators);
        TradingEnsembleChart.SetMessage("Расчёт ансамблей TorchSharp4GPU, TorchSharp, Jordan и Encog …");
        ApplyTradingEnsembleOptions();
        SetTradingCalculationStatus(
            _tradingForecastDays == 0
                ? "Ожидайте — расчёт ансамблей…"
                : $"Ожидайте — Forecast {_tradingForecastDays}…",
            BreadthProgressLevel.Waiting);
        try
        {
            TradingEnsembleCalculationResult result = await Task.Run(() =>
                TradingEnsembleInference.Calculate(
                    barsSnapshot, gpuDirectory, torchDirectory, encogDirectory, _tradingForecastDays, progress, cancellationToken),
                cancellationToken);
            if (generation != _tradingEnsembleCalculationGeneration ||
                marketGeneration != _marketDataGeneration ||
                cancellationToken.IsCancellationRequested)
                return;
            _tradingEnsembles = result.Series;
            TradingEnsembleChart.SetData(_tradingEnsembles, _sp500PriceSeries, _technicalIndicators);
            ApplyTradingPackageAvailability(result.Statuses);
            ApplyTradingEnsembleOptions();
            int available = result.Statuses.Count(status => status.Available);
            bool anyWarnings = result.Statuses.Any(status => status.Available && !string.IsNullOrWhiteSpace(status.Message));
            BreadthProgressLevel level = available == 9 && !anyWarnings ? BreadthProgressLevel.Normal :
                available == 0 ? BreadthProgressLevel.Error : BreadthProgressLevel.Waiting;
            if (!result.AnyAvailable)
                TradingEnsembleChart.SetMessage(result.Summary);
            SetStatus($"Торговые ансамбли: {result.Summary}", level);
            SetTradingCalculationStatus(result.Summary, level);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _tradingEnsembleCalculationGeneration &&
                marketGeneration == _marketDataGeneration)
            {
                _tradingEnsembleCalculationStarted = false;
                _tradingEnsembleDataGeneration = -1;
                SetTradingCalculationStatus("Расчёт отменён", BreadthProgressLevel.Waiting);
            }
        }
        catch (Exception ex)
        {
            if (generation != _tradingEnsembleCalculationGeneration ||
                marketGeneration != _marketDataGeneration)
                return;
            _tradingEnsembleCalculationStarted = false;
            _tradingEnsembleDataGeneration = -1;
            TradingEnsembleChart.SetMessage(ex.Message);
            SetStatus($"Расчёт торговых ансамблей не выполнен: {ex.Message}", BreadthProgressLevel.Error);
            SetTradingCalculationStatus("Ошибка Forecast — см. сообщение", BreadthProgressLevel.Error);
        }
    }

    private void ApplyTradingPackageAvailability(IReadOnlyList<TradingPackageStatus> statuses)
    {
        foreach (TradingPackageStatus status in statuses)
        {
            CheckBox selector = status.PackageName switch
            {
                "GPU-ZZ1" => TradingShowGpuZz1,
                "GPU-ZZ2" => TradingShowGpuZz2,
                "GPU-ZZ3" => TradingShowGpuZz3,
                "Torch-ZZ1" => TradingShowTorchZz1,
                "Torch-ZZ2" => TradingShowTorchZz2,
                "Torch-ZZ1-Jordan" => TradingShowTorchZz1Jordan,
                "Torch-ZZ2-Jordan" => TradingShowTorchZz2Jordan,
                "Encog-ZZ1" => TradingShowEncogZz1,
                _ => TradingShowEncogZz2
            };
            bool wasUnavailable = !selector.IsEnabled;
            selector.IsEnabled = status.Available;
            selector.ToolTip = status.Available
                ? $"{status.PackageName}: {status.ModelCount} пригодных моделей, данные до {status.LastDate:yyyy-MM-dd}" +
                  (string.IsNullOrWhiteSpace(status.Message) ? "" : $"; {status.Message}")
                : $"{status.PackageName} недоступен: {status.Message}";
            if (!status.Available)
                selector.IsChecked = false;
            else if (wasUnavailable)
                selector.IsChecked = false;
        }
    }

    private void OpenTradingGpuDirectoryTreeClick(object sender, RoutedEventArgs e)
    {
        EnsureTradingDirectoryTree(TradingGpuDirectoryTree, ref _tradingGpuDirectoryTreeInitialized);
        TradingGpuDirectoryPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => SelectDirectoryTreePath(TradingGpuDirectoryTree, TradingGpuDirectory.Text)));
    }

    private void OpenTradingTorchDirectoryTreeClick(object sender, RoutedEventArgs e)
    {
        EnsureTradingDirectoryTree(TradingTorchDirectoryTree, ref _tradingTorchDirectoryTreeInitialized);
        TradingTorchDirectoryPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => SelectDirectoryTreePath(TradingTorchDirectoryTree, TradingTorchDirectory.Text)));
    }

    private void OpenTradingEncogDirectoryTreeClick(object sender, RoutedEventArgs e)
    {
        EnsureTradingDirectoryTree(TradingEncogDirectoryTree, ref _tradingEncogDirectoryTreeInitialized);
        TradingEncogDirectoryPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => SelectDirectoryTreePath(TradingEncogDirectoryTree, TradingEncogDirectory.Text)));
    }

    private void EnsureTradingDirectoryTree(TreeView tree, ref bool initialized)
    {
        if (initialized)
            return;
        initialized = true;
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            TreeViewItem item = CreateDirectoryTreeItem(drive.RootDirectory.FullName, drive.Name);
            tree.Items.Add(item);
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

    private void SelectDirectoryTreePath(TreeView tree, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return; }
        string rootPath = Path.GetPathRoot(fullPath) ?? string.Empty;
        TreeViewItem? current = tree.Items.OfType<TreeViewItem>()
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

    private void SelectTradingGpuDirectoryClick(object sender, RoutedEventArgs e) =>
        ApplySelectedTradingDirectory(TradingGpuDirectoryTree, TradingGpuDirectory, TradingGpuDirectoryPopup);

    private void SelectTradingTorchDirectoryClick(object sender, RoutedEventArgs e) =>
        ApplySelectedTradingDirectory(TradingTorchDirectoryTree, TradingTorchDirectory, TradingTorchDirectoryPopup);

    private void SelectTradingEncogDirectoryClick(object sender, RoutedEventArgs e) =>
        ApplySelectedTradingDirectory(TradingEncogDirectoryTree, TradingEncogDirectory, TradingEncogDirectoryPopup);

    private void TradingGpuDirectoryTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplySelectedTradingDirectory(TradingGpuDirectoryTree, TradingGpuDirectory, TradingGpuDirectoryPopup);
        e.Handled = true;
    }

    private void TradingTorchDirectoryTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplySelectedTradingDirectory(TradingTorchDirectoryTree, TradingTorchDirectory, TradingTorchDirectoryPopup);
        e.Handled = true;
    }

    private void TradingEncogDirectoryTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplySelectedTradingDirectory(TradingEncogDirectoryTree, TradingEncogDirectory, TradingEncogDirectoryPopup);
        e.Handled = true;
    }

    private void CloseTradingGpuDirectoryTreeClick(object sender, RoutedEventArgs e) =>
        TradingGpuDirectoryPopup.IsOpen = false;

    private void CloseTradingTorchDirectoryTreeClick(object sender, RoutedEventArgs e) =>
        TradingTorchDirectoryPopup.IsOpen = false;

    private void CloseTradingEncogDirectoryTreeClick(object sender, RoutedEventArgs e) =>
        TradingEncogDirectoryPopup.IsOpen = false;

    private void ApplySelectedTradingDirectory(TreeView tree, TextBox pathBox, System.Windows.Controls.Primitives.Popup popup)
    {
        if (tree.SelectedItem is not TreeViewItem item || item.Tag is not string path)
            return;
        pathBox.Text = path;
        popup.IsOpen = false;
        InvalidateTradingEnsembleResults();
        if (ReferenceEquals(BreadthTabs.SelectedItem, TradingEnsemblesTab))
            StartTradingEnsembleCalculationIfNeeded();
    }

    private void TradingEnsembleOptionsChanged(object sender, RoutedEventArgs e) =>
        ApplyTradingEnsembleOptions();

    private void DecreaseTradingForecastClick(object sender, RoutedEventArgs e)
    {
        CommitTradingForecast();
        SetTradingForecastDays(_tradingForecastDays - 5);
    }

    private void IncreaseTradingForecastClick(object sender, RoutedEventArgs e)
    {
        CommitTradingForecast();
        SetTradingForecastDays(_tradingForecastDays + 5);
    }

    private void TradingForecastLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitTradingForecast();

    private void TradingForecastKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTradingForecast(); TradingForecastValue.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            TradingForecastValue.Text = _tradingForecastDays.ToString(CultureInfo.InvariantCulture);
            TradingForecastValue.SelectAll();
            e.Handled = true;
        }
    }

    private void CommitTradingForecast()
    {
        if (int.TryParse(TradingForecastValue.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            SetTradingForecastDays(value);
        TradingForecastValue.Text = _tradingForecastDays.ToString(CultureInfo.InvariantCulture);
    }

    private void SetTradingForecastDays(int value)
    {
        int normalized = Math.Clamp(value, 0, 100);
        if (normalized == _tradingForecastDays)
            return;

        _tradingForecastDays = normalized;
        TradingForecastValue.Text = normalized.ToString(CultureInfo.InvariantCulture);
        InvalidateTradingEnsembleResults();
        TradingEnsembleChart.SetMessage(normalized == 0
            ? "Forecast отключён. Пересчёт основных ансамблей …"
            : $"Forecast: {normalized} дней. Пересчёт ансамблей …");
        SetTradingCalculationStatus(
            normalized == 0 ? "Ожидайте — расчёт ансамблей…" : $"Ожидайте — Forecast {normalized}…",
            BreadthProgressLevel.Waiting);
        // Repeated presses update the number immediately, but calculate only after input settles.
        if (_tradingForecastTimer is null)
        {
            _tradingForecastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _tradingForecastTimer.Tick += (_, _) =>
            {
                _tradingForecastTimer.Stop();
                if (IsLoaded && ReferenceEquals(BreadthTabs.SelectedItem, TradingEnsemblesTab))
                    StartTradingEnsembleCalculationIfNeeded();
            };
        }
        _tradingForecastTimer.Stop();
        _tradingForecastTimer.Start();
    }

    private void ApplyTradingEnsembleOptions()
    {
        if (TradingEnsembleChart is null || TradingShowGpuZz1 is null) return;
        IReadOnlyList<string> selected = SelectedTradingPackageKeys();
        TradingEnsembleChart.SetPackages(selected);
        TradingEnsembleChart.SetRecentPerformanceBars(_tradingPerformanceBars);
        TradingEnsembleChart.SetShowEma(TradingShowEma.IsChecked == true);
        TradingActiveMode activeMode = SelectedTradingActiveMode();
        TradingEnsembleChart.SetActiveMode(activeMode);
        TradingEnsembleChart.SetShowPrice(ShowSp500Price.IsChecked == true);
        _tradingTargetActivePositions = CalculateTradingTargetActivePositions(selected, activeMode);
        _tradingChandelierStops = CalculateTradingChandelierStops(
            selected, activeMode, out _tradingChandelierExecutions,
            out _tradingActualActivePositions);
        TradingEnsembleChart.SetChandelierStops(_tradingChandelierStops);
        TradingEnsembleChart.SetChandelierExecutions(_tradingChandelierExecutions);
        TradingEnsembleChart.SetActualActivePositions(_tradingActualActivePositions);
        TradingEnsembleChart.SetTargetActivePositions(_tradingTargetActivePositions);
        UpdateTradingAnalysis(selected);
        UpdateTradingAnalysisTitle(selected, activeMode);
        if (_cursorDate.HasValue)
            UpdateTradingCursorValues(_cursorDate.Value);
    }

    private TradingActiveMode SelectedTradingActiveMode() => TradingActiveMode?.SelectedIndex switch
    {
        0 => BreezeVL.TradingActiveMode.ProportionalEnsembles,
        2 => BreezeVL.TradingActiveMode.QuantumEnsembles,
        _ => BreezeVL.TradingActiveMode.IndependentDeciles
    };

    private IReadOnlyList<string> SelectedTradingPackageKeys()
    {
        var selected = new List<string>(9);
        if (TradingShowGpuZz1.IsChecked == true) selected.Add("GPU-ZZ1");
        if (TradingShowGpuZz2.IsChecked == true) selected.Add("GPU-ZZ2");
        if (TradingShowGpuZz3.IsChecked == true) selected.Add("GPU-ZZ3");
        if (TradingShowTorchZz1.IsChecked == true) selected.Add("Torch-ZZ1");
        if (TradingShowTorchZz2.IsChecked == true) selected.Add("Torch-ZZ2");
        if (TradingShowTorchZz1Jordan.IsChecked == true) selected.Add("Torch-ZZ1-Jordan");
        if (TradingShowTorchZz2Jordan.IsChecked == true) selected.Add("Torch-ZZ2-Jordan");
        if (TradingShowEncogZz1.IsChecked == true) selected.Add("Encog-ZZ1");
        if (TradingShowEncogZz2.IsChecked == true) selected.Add("Encog-ZZ2");
        return selected;
    }

    private void UpdateTradingCursorValues(DateTime date)
    {
        if (TradingCursorValuesText is null || TradingShowGpuZz1 is null)
            return;

        TradingCursorValuesText.Inlines.Clear();
        TradingCursorValuesText.Inlines.Add(new Run($"{date:yyyy-MM-dd}   "));
        string activeText = _tradingActualActivePositions.TryGetValue(date.Date,
                out TradingActivePosition actualPosition)
            ? actualPosition.Percentage.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "—";
        TradingCursorValuesText.Inlines.Add(new Run($"Active {activeText}   ")
        {
            Foreground = Brushes.DarkBlue
        });
        double? stop = TradingStopAt(date);
        TradingCursorValuesText.Inlines.Add(new Run($"Stop {(stop.HasValue ? stop.Value.ToString("0.00", CultureInfo.InvariantCulture) : "—")}   ")
        {
            Foreground = Brushes.Firebrick
        });
        IReadOnlyList<string> selected = SelectedTradingPackageKeys();
        foreach (string key in selected)
        {
            TradingEnsemblePoint? point = null;
            if (_tradingEnsembles.TryGetValue(key, out TradingEnsembleSeries? series))
            {
                point = FindTradingPoint(series, date);
            }

            string value = point is null
                ? "—"
                : point.EnsemblePercent.ToString("0.0", CultureInfo.InvariantCulture);
            TradingCursorValuesText.Inlines.Add(new Run($"{TradingPackageLabel(key)} {value}")
            {
                Foreground = TradingEnsembleChart.PackageBrush(key)
            });
            TradingCursorValuesText.Inlines.Add(new Run("   "));
        }

    }

    private static string TradingActiveLabel(TradingActiveMode mode) => mode switch
    {
        BreezeVL.TradingActiveMode.ProportionalEnsembles => "Active Ensembles",
        BreezeVL.TradingActiveMode.QuantumEnsembles => "Active Ensembles 0/10",
        _ => "Active Deciles"
    };

    internal static (double Value, double Percentage)? TradingActiveMetrics(
        IReadOnlyList<(TradingEnsemblePoint Point, int ModelCount)> packages,
        TradingActiveMode mode)
    {
        if (packages.Count == 0) return null;
        double total = packages.Sum(item => TradingDesiredQuantity(item.Point, mode, item.ModelCount));
        double percentage = total * 100.0 / (packages.Count * 10.0);
        double value = mode == BreezeVL.TradingActiveMode.IndependentDeciles
            ? total
            : total / packages.Count;
        return (value, percentage);
    }

    private IReadOnlyDictionary<DateTime, TradingActivePosition> CalculateTradingTargetActivePositions(
        IReadOnlyList<string> selected, TradingActiveMode mode)
    {
        TradingEnsembleSeries[] available = selected
            .Where(_tradingEnsembles.ContainsKey)
            .Select(key => _tradingEnsembles[key])
            .Where(series => series.Points.Count > 0)
            .ToArray();
        if (available.Length == 0)
            return new Dictionary<DateTime, TradingActivePosition>();

        DateTime[] priceDates = _sp500PriceSeries?.Points
            .Select(point => point.Date.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToArray() ?? [];
        var nextPriceDate = new Dictionary<DateTime, DateTime>();
        for (int index = 0; index + 1 < priceDates.Length; index++)
            nextPriceDate[priceDates[index]] = priceDates[index + 1];

        var positions = new Dictionary<DateTime, TradingActivePosition>();
        foreach (DateTime signalDate in available
                     .SelectMany(series => series.Points.Select(point => point.Date.Date))
                     .Distinct()
                     .OrderBy(date => date))
        {
            var packages = available
                .Select(series => (Point: FindTradingPoint(series, signalDate), series.ModelCount))
                .Where(item => item.Point is not null)
                .Select(item => (item.Point!, item.ModelCount))
                .ToList();
            (double Value, double Percentage)? metrics = TradingActiveMetrics(packages, mode);
            if (metrics.HasValue)
            {
                DateTime executionDate = nextPriceDate.TryGetValue(signalDate, out DateTime nextDate)
                    ? nextDate
                    : NextTradingTargetDate(available, signalDate);
                positions[executionDate] = new TradingActivePosition(
                    metrics.Value.Value, metrics.Value.Percentage);
            }
        }
        return positions;
    }

    private static DateTime NextTradingTargetDate(
        IReadOnlyList<TradingEnsembleSeries> available,
        DateTime signalDate)
    {
        DateTime? forecastDate = available
            .SelectMany(series => series.Forecasts)
            .Where(point => point.IsNextBar && point.Date.Date > signalDate.Date)
            .Select(point => (DateTime?)point.Date.Date)
            .Min();
        if (forecastDate.HasValue)
            return forecastDate.Value;
        DateTime next = signalDate.Date.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            next = next.AddDays(1);
        return next;
    }

    private void UpdateTradingAnalysisTitle(IReadOnlyList<string> selected, TradingActiveMode mode)
    {
        if (TradingAnalysisTitle is null) return;
        var available = selected
            .Where(_tradingEnsembles.ContainsKey)
            .Select(key => _tradingEnsembles[key])
            .Where(series => series.Points.Count > 0)
            .ToArray();
        if (available.Length == 0)
        {
            TradingAnalysisTitle.Text = "Активные ансамбли: Last — · Stop —";
            return;
        }
        DateTime lastDate = available.Max(series => series.Points[^1].Date.Date);
        var points = available
            .Select(series => (Point: FindTradingPoint(series, lastDate), series.ModelCount))
            .Where(item => item.Point is not null)
            .Select(item => (item.Point!, item.ModelCount))
            .ToList();
        TradingActivePosition? held = _tradingActualActivePositions.TryGetValue(lastDate,
            out TradingActivePosition actualPosition) ? actualPosition : null;
        double? stop = TradingLatestStop(lastDate);
        string stopText = stop.HasValue ? stop.Value.ToString("0.00", CultureInfo.InvariantCulture) : "—";
        string heldText = held.HasValue
            ? held.Value.Percentage.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "—";
        TradingAnalysisTitle.Text = $"Активные ансамбли: Last {heldText} · Stop {stopText}";
    }

    private double? TradingStopAt(DateTime date)
    {
        if (!_tradingChandelierStops.TryGetValue(date.Date, out IReadOnlyList<double>? stops))
            return null;
        double[] finite = stops.Where(double.IsFinite).ToArray();
        return finite.Length == 0 ? null : finite.Max();
    }

    private double? TradingLatestStop(DateTime lastDataDate)
    {
        foreach (DateTime date in _tradingChandelierStops.Keys
                     .Where(date => date.Date >= lastDataDate.Date)
                     .OrderByDescending(date => date))
        {
            double? stop = TradingStopAt(date);
            if (stop.HasValue)
                return stop;
        }
        return null;
    }

    private IReadOnlyDictionary<DateTime, IReadOnlyList<double>> CalculateTradingChandelierStops(
        IReadOnlyList<string> selected, TradingActiveMode activeMode,
        out IReadOnlyDictionary<DateTime, IReadOnlyList<double>> executions,
        out IReadOnlyDictionary<DateTime, TradingActivePosition> activePositions)
    {
        if (_sp500PriceSeries?.Points is not { Count: > 2 } prices)
        {
            executions = new Dictionary<DateTime, IReadOnlyList<double>>();
            activePositions = new Dictionary<DateTime, TradingActivePosition>();
            return new Dictionary<DateTime, IReadOnlyList<double>>();
        }
        var combined = new Dictionary<DateTime, List<double>>();
        var combinedExecutions = new Dictionary<DateTime, List<double>>();
        var combinedHeldQuantities = new Dictionary<DateTime, List<int>>();
        foreach (string key in selected)
        {
            if (!_tradingEnsembles.TryGetValue(key, out TradingEnsembleSeries? series) ||
                series.Points.Count < 2)
                continue;
            DateTime firstDate = series.Points[0].Date.Date;
            DateTime lastDate = series.Points[^1].Date.Date;
            int startIndex = prices.ToList().FindIndex(point => point.Date.Date >= firstDate);
            int endIndex = prices.ToList().FindLastIndex(point => point.Date.Date <= lastDate);
            if (startIndex < 0 || endIndex <= startIndex)
                continue;
            TradingChandelierResult result = TradingChandelierSimulation.Run(
                prices, series, startIndex, endIndex, activeMode, TradingEfficiencyStartingCapital);
            foreach ((DateTime date, IReadOnlyList<double> stops) in result.StopsByDate)
            {
                if (!combined.TryGetValue(date, out List<double>? target))
                    combined[date] = target = [];
                target.AddRange(stops.Where(double.IsFinite));
            }
            foreach ((DateTime date, IReadOnlyList<double> fills) in result.ExecutedStopsByDate)
            {
                if (!combinedExecutions.TryGetValue(date, out List<double>? target))
                    combinedExecutions[date] = target = [];
                target.AddRange(fills.Where(double.IsFinite));
            }
            foreach ((DateTime date, int quantity) in result.HeldQuantityByDate)
            {
                if (!combinedHeldQuantities.TryGetValue(date, out List<int>? target))
                    combinedHeldQuantities[date] = target = [];
                target.Add(Math.Clamp(quantity, 0, 10));
            }
            if (result.NextBarStops.Count > 0)
            {
                DateTime nextDate = NextTradingDate(series, prices, endIndex, lastDate);
                if (!combined.TryGetValue(nextDate, out List<double>? target))
                    combined[nextDate] = target = [];
                target.AddRange(result.NextBarStops.Where(double.IsFinite));
            }
        }
        // The simulator keeps every position-level chandelier for profit accounting.
        // The chart needs only the highest (closest protective) level for each date.
        // Collapsing here prevents navigation from creating one marker per open lot.
        executions = CollapseMaximumTradingLevels(combinedExecutions);
        activePositions = combinedHeldQuantities.ToDictionary(item => item.Key, item =>
        {
            double total = item.Value.Sum();
            double percentage = item.Value.Count == 0
                ? double.NaN
                : total * 100.0 / (item.Value.Count * 10.0);
            double value = activeMode == BreezeVL.TradingActiveMode.IndependentDeciles
                ? total
                : total / item.Value.Count;
            return new TradingActivePosition(value, percentage);
        });
        return CollapseMaximumTradingLevels(combined);
    }

    private static DateTime NextTradingDate(TradingEnsembleSeries series,
        IReadOnlyList<PricePoint> prices, int endIndex, DateTime lastDate)
    {
        DateTime? forecastDate = series.Forecasts
            .Where(point => point.IsNextBar && point.Date.Date > lastDate.Date)
            .Select(point => (DateTime?)point.Date.Date)
            .Min();
        if (forecastDate.HasValue)
            return forecastDate.Value;
        if (endIndex + 1 < prices.Count && prices[endIndex + 1].Date.Date > lastDate.Date)
            return prices[endIndex + 1].Date.Date;
        DateTime next = lastDate.Date.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            next = next.AddDays(1);
        return next;
    }

    private static IReadOnlyDictionary<DateTime, IReadOnlyList<double>> CollapseMaximumTradingLevels(
        IReadOnlyDictionary<DateTime, List<double>> levels)
    {
        return levels
            .Where(item => item.Value.Any(double.IsFinite))
            .ToDictionary(item => item.Key,
                item => (IReadOnlyList<double>)new[] { item.Value.Where(double.IsFinite).Max() });
    }

    private static TradingEnsemblePoint? FindTradingPoint(TradingEnsembleSeries series, DateTime date)
    {
        int low = 0;
        int high = series.Points.Count - 1;
        DateTime target = date.Date;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            DateTime current = series.Points[middle].Date.Date;
            int comparison = current.CompareTo(target);
            if (comparison == 0)
                return series.Points[middle];
            if (comparison < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return null;
    }

    private void UpdateTradingAnalysis(IReadOnlyList<string> selected)
    {
        if (TradingAnalysisCards is null)
            return;
        TradingAnalysisCards.Children.Clear();
        if (selected.Count == 0)
        {
            _tradingAnalysisReportText =
                "Ансамбли не выбраны. Включите необходимые ансамбли на верхней панели.";
            TradingAnalysisCards.Children.Add(new TextBlock
            {
                Text = _tradingAnalysisReportText,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(64, 74, 84))
            });
            return;
        }

        var report = new StringBuilder();
        foreach (IGrouping<string, string> group in selected.GroupBy(TradingGroupKey))
        {
            string[] keys = group.ToArray();
            DateTime[] dates = keys
                .Select(key => _tradingEnsembles.TryGetValue(key, out TradingEnsembleSeries? series)
                    ? series.Points.LastOrDefault()?.Date.Date
                    : null)
                .Where(date => date.HasValue)
                .Select(date => date!.Value)
                .ToArray();
            DateTime? groupDate = dates.Length == 0 ? null : dates.Max();
            string heading = TradingGroupName(group.Key) +
                (groupDate.HasValue ? $" · {groupDate:yyyy-MM-dd}" : string.Empty);

            var contents = new StackPanel();
            contents.Children.Add(new TextBlock
            {
                Text = heading,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(38, 53, 68)),
                Margin = new Thickness(0, 0, 0, 7)
            });
            report.AppendLine(heading);

            foreach (string key in keys)
            {
                string packageLabel = TradingPackageLabel(key);
                _tradingEnsembles.TryGetValue(key, out TradingEnsembleSeries? series);
                var entry = new StringBuilder();
                entry.Append("• ").Append(packageLabel).Append(" — ")
                    .AppendLine(TradingTargetDescription(key));
                entry.Append("  Состав: ");
                if (series is not null)
                    AppendArchitectureCounts(entry, series.ArchitectureCounts);
                else
                    entry.Append("ожидается анализ пакета");
                TradingEnsemblePoint? lastPoint = series?.Points.LastOrDefault(point =>
                    double.IsFinite(point.EnsemblePercent));
                if (lastPoint is not null)
                {
                    entry.Append(". Последнее значение: ")
                        .Append(lastPoint.EnsemblePercent.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")))
                        .Append('%');
                }
                entry.Append('.');
                string entryText = entry.ToString();
                var entryBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(48, 58, 68)),
                    Margin = new Thickness(0, 0, 0, 7)
                };
                entryBlock.Inlines.Add(new Run("• "));
                entryBlock.Inlines.Add(new Run(packageLabel)
                {
                    Foreground = TradingEnsembleChart.PackageBrush(key),
                    FontWeight = FontWeights.SemiBold
                });
                entryBlock.Inlines.Add(new Run(entryText[(2 + packageLabel.Length)..]));
                contents.Children.Add(entryBlock);
                report.AppendLine(entryText);

                if (series is not null && !string.IsNullOrWhiteSpace(series.Warning))
                {
                    string warning = $"Предупреждение: {series.Warning}.";
                    contents.Children.Add(new TextBlock
                    {
                        Text = warning,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(170, 65, 55)),
                        Margin = new Thickness(14, -4, 0, 7)
                    });
                    report.AppendLine(warning);
                }
            }

            TradingAnalysisCards.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(242, 250, 241)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(168, 213, 162)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 7, 9, 4),
                Margin = new Thickness(0, 0, 0, 9),
                Child = contents
            });
            report.AppendLine();
        }

        TradingActiveMode activeMode = SelectedTradingActiveMode();
        TradingEfficiencyReport? efficiency = CalculateTradingEfficiency(selected, activeMode);
        if (efficiency is not null)
        {
            TradingAnalysisCards.Children.Add(BuildTradingEfficiencyCard(efficiency, activeMode));
            AppendTradingEfficiencyReport(report, efficiency, activeMode);
        }
        _tradingAnalysisReportText = report.ToString().TrimEnd();
    }

    private Border BuildTradingEfficiencyCard(TradingEfficiencyReport efficiency, TradingActiveMode activeMode)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Эффективность к benchmark SPY",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 53, 68)),
            Margin = new Thickness(0, 0, 0, 7)
        });
        panel.Children.Add(new TextBlock
        {
            Text = TradingModeDescription(activeMode),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(58, 90, 68)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -3, 0, 5)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Защита: люстра 3,75% от максимального High; повторный вход через один торговый бар",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(145, 50, 45)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -2, 0, 5)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Str%: исходное среднее по отдельным моделям; от режима торговли не зависит",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(83, 93, 103)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -2, 0, 5)
        });
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        AddEfficiencyRow(table, 0, "Ансамбль", "Профит, весь период",
            $"Str%, {_tradingPerformanceBars} баров", true);
        int row = 1;
        foreach (TradingEfficiencyRow item in efficiency.Rows)
            AddEfficiencyRow(table, row++, TradingEfficiencyLabel(item.Key),
                FormatRelativeEfficiency(item.FullPeriod), FormatRelativeEfficiency(item.RecentPerformance), false);
        panel.Children.Add(table);
        panel.Children.Add(new TextBlock
        {
            Text = $"Период: {efficiency.FullStart:yyyy-MM-dd} — {efficiency.FullEnd:yyyy-MM-dd}\n" +
                   $"Последние {_tradingPerformanceBars} торговых баров: " +
                   $"{efficiency.RecentStart:yyyy-MM-dd} — {efficiency.RecentEnd:yyyy-MM-dd}",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(83, 93, 103)),
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(168, 213, 162)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 7, 9, 7),
            Child = panel
        };
    }

    private static void AddEfficiencyRow(Grid table, int row, string name, string full, string recent, bool header)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] values = [name, full, recent];
        for (int column = 0; column < values.Length; column++)
        {
            var cell = new TextBlock
            {
                Text = values[column],
                FontSize = 11,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(column == 0 ? 0 : 5, 3, 0, 3)
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            table.Children.Add(cell);
        }
    }

    private TradingEfficiencyReport? CalculateTradingEfficiency(
        IReadOnlyList<string> selected, TradingActiveMode activeMode)
    {
        if (_sp500PriceSeries?.Points is not { Count: > 2 } prices)
            return null;
        TradingEnsembleSeries[] available = selected
            .Where(_tradingEnsembles.ContainsKey)
            .Select(key => _tradingEnsembles[key])
            .Where(series => series.Points.Count > 1)
            .ToArray();
        if (available.Length == 0)
            return null;

        DateTime commonEnd = available.Min(series => series.Points[^1].Date.Date);
        List<PricePoint> priceList = prices.ToList();
        int fullStartIndex = priceList.FindIndex(point => point.Date.Date >= TradingEfficiencyStartDate);
        int endIndex = priceList.FindLastIndex(point => point.Date.Date <= commonEnd);
        if (fullStartIndex < 0 || endIndex <= fullStartIndex + 1)
            return null;
        int recentStartIndex = Math.Max(fullStartIndex, endIndex - _tradingPerformanceBars);

        var rows = new List<TradingEfficiencyRow>();
        foreach (string key in selected)
        {
            if (!_tradingEnsembles.TryGetValue(key, out TradingEnsembleSeries? series))
                continue;
            double recentPerformance = double.NaN;
            if (series.BenchmarkPercentByLookback?.TryGetValue(
                    _tradingPerformanceBars, out double calculated) == true)
                recentPerformance = calculated;
            else if (_tradingPerformanceBars == 125)
                recentPerformance = series.Last125BenchmarkPercent;
            rows.Add(new TradingEfficiencyRow(key,
                CalculateModeProfitPercent(prices, series, fullStartIndex, endIndex, activeMode),
                recentPerformance));
        }
        return rows.Count == 0 ? null : new TradingEfficiencyReport(rows,
            TradingEfficiencyStartDate, prices[endIndex].Date.Date,
            prices[recentStartIndex].Date.Date, prices[endIndex].Date.Date);
    }

    private static double CalculateModeProfitPercent(
        IReadOnlyList<PricePoint> prices,
        TradingEnsembleSeries series,
        int startIndex,
        int endIndex,
        TradingActiveMode activeMode)
    {
        if (startIndex < 0 || endIndex <= startIndex || endIndex >= prices.Count)
            return double.NaN;
        return TradingChandelierSimulation.Run(prices, series, startIndex, endIndex,
            activeMode, TradingEfficiencyStartingCapital).ProfitPercent;
    }

    internal static int TradingDesiredQuantity(
        TradingEnsemblePoint point, TradingActiveMode activeMode, int modelCount = 0)
    {
        if (activeMode == BreezeVL.TradingActiveMode.IndependentDeciles)
            return Math.Clamp(point.Deciles.Count(value => value >= 50.0), 0, 10);
        if (activeMode == BreezeVL.TradingActiveMode.QuantumEnsembles)
            return point.EnsemblePercent >= TradingEnsembleChart.BaseClassificationThreshold ? 10 : 0;
        // Match Execute in the reference "что если" strategy, including its
        // raw model-count division and midpoint rounding (not all-in/all-out).
        if (modelCount > 0)
        {
            double activeModels = Math.Round(point.EnsemblePercent * modelCount / 100.0);
            if (modelCount <= 10) return Math.Clamp((int)activeModels, 0, 10);
            return Math.Clamp((int)Math.Round(activeModels / (modelCount / 10.0),
                MidpointRounding.AwayFromZero), 0, 10);
        }
        return Math.Clamp((int)Math.Round(point.EnsemblePercent / 10.0,
            MidpointRounding.AwayFromZero), 0, 10);
    }

    private void AppendTradingEfficiencyReport(
        StringBuilder report, TradingEfficiencyReport efficiency, TradingActiveMode activeMode)
    {
        report.AppendLine("СРАВНИТЕЛЬНАЯ ЭФФЕКТИВНОСТЬ К BENCHMARK SPY");
        report.AppendLine(TradingModeDescription(activeMode));
        report.AppendLine("Защита: люстра 3,75% от максимального High; повторный вход через один торговый бар");
        report.AppendLine("Str%: исходное среднее по отдельным моделям; от режима торговли не зависит");
        report.AppendLine($"Ансамбль\tПрофит, весь период\tStr%, {_tradingPerformanceBars} баров");
        foreach (TradingEfficiencyRow row in efficiency.Rows)
            report.AppendLine($"{TradingEfficiencyLabel(row.Key)}\t{FormatRelativeEfficiency(row.FullPeriod)}\t{FormatRelativeEfficiency(row.RecentPerformance)}");
        report.AppendLine($"Период: {efficiency.FullStart:yyyy-MM-dd} — {efficiency.FullEnd:yyyy-MM-dd}");
        report.AppendLine($"Последние {_tradingPerformanceBars} торговых баров: " +
            $"{efficiency.RecentStart:yyyy-MM-dd} — {efficiency.RecentEnd:yyyy-MM-dd}");
    }

    private static string TradingModeDescription(TradingActiveMode mode) => mode switch
    {
        BreezeVL.TradingActiveMode.ProportionalEnsembles =>
            "Режим: ансамбль, пропорциональная позиция 0–10 (режим 0)",
        BreezeVL.TradingActiveMode.QuantumEnsembles =>
            "Режим: ансамбль, квантовая позиция 0/10 по порогу 50%",
        _ => "Режим: независимые децели"
    };

    private static string FormatRelativeEfficiency(double value) => double.IsFinite(value)
        ? value.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + "%"
        : "—";

    private static string TradingEfficiencyLabel(string key) =>
        $"{(key.StartsWith("GPU-", StringComparison.Ordinal) ? "GPU" : key.StartsWith("Torch-", StringComparison.Ordinal) ? "TorchSharp" : "Encog")} · {TradingPackageLabel(key)}";

    private void CopyTradingEnsemblesClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_tradingAnalysisReportText))
        {
            SetStatus("Нет отчёта об активных ансамблях для копирования.", BreadthProgressLevel.Error);
            return;
        }
        try
        {
            Clipboard.SetText(_tradingAnalysisReportText);
            SetStatus("Отчёт об активных ансамблях скопирован в буфер обмена.");
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось скопировать отчёт: {ex.Message}", BreadthProgressLevel.Error);
        }
    }

    private static void AppendArchitectureCounts(
        StringBuilder text, IReadOnlyDictionary<int, int>? architectureCounts)
    {
        if (architectureCounts is null || architectureCounts.Count == 0)
        {
            text.Append("не определён");
            return;
        }

        bool first = true;
        foreach ((int architecture, int count) in architectureCounts.OrderBy(pair => pair.Key))
        {
            if (!first)
                text.Append(", ");
            text.Append(TradingArchitectureName(architecture));
            text.Append(" — ");
            text.Append(count.ToString(CultureInfo.InvariantCulture));
            first = false;
        }
    }

    private static string TradingArchitectureName(int architecture) => architecture switch
    {
        1 => "Classic MLP",
        2 => "Jordan",
        3 => "Elman",
        4 => "Jordan–Elman",
        _ => $"неизвестная архитектура {architecture}"
    };

    private static string TradingGroupKey(string key) => key.StartsWith("GPU-", StringComparison.Ordinal)
        ? "GPU"
        : key.StartsWith("Torch-", StringComparison.Ordinal) ? "Torch" : "Encog";

    private static string TradingGroupName(string group) => group switch
    {
        "GPU" => "TorchSharp for GPT / GPU · видеокарта",
        "Torch" => "Библиотека TorchSharp",
        _ => "Библиотека Encog"
    };

    private static string TradingPackageLabel(string key) => key switch
    {
        "GPU-ZZ1" => "ZZ1",
        "GPU-ZZ2" => "ZZ2",
        "GPU-ZZ3" => "ZZ3",
        "Torch-ZZ1" => "ZZ1",
        "Torch-ZZ2" => "ZZ2",
        "Torch-ZZ1-Jordan" => "ZZ1_J",
        "Torch-ZZ2-Jordan" => "ZZ2_J",
        "Encog-ZZ1" => "ZZ1",
        "Encog-ZZ2" => "ZZ2",
        _ => key
    };

    private static string TradingTargetDescription(string key)
    {
        if (key.Contains("ZZ3", StringComparison.Ordinal))
            return "ATR-цель: состояние по движению цены, нормированному относительно ATR.";
        if (key.Contains("ZZ2", StringComparison.Ordinal))
            return "Трендовая цель: разметка Peak/Trough между подтверждёнными вершинами и основаниями.";
        return "Осцилляторная цель на базе SuperSmoother(8).";
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
        SentimentSpChart?.SetPriceSeries(series);
        MarketSetupSpChart?.SetPriceSeries(series);
        SetPriceVisibility(ShowSp500Price.IsChecked == true);
        SetPriceCandleMode(ShowSp500Candles.IsChecked == true);
    }

    private void SetPriceVisibility(bool show)
    {
        if (Sp50Chart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetShowPriceSeries(show);
        SentimentSpChart?.SetShowPriceSeries(show);
        MarketSetupSpChart?.SetShowPriceSeries(show);
        TradingEnsembleChart?.SetShowPrice(show);
    }

    private void SetPriceCandleMode(bool showCandles)
    {
        if (Sp50Chart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetShowPriceCandles(showCandles);
        SentimentSpChart?.SetShowPriceCandles(showCandles);
        MarketSetupSpChart?.SetShowPriceCandles(showCandles);
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
        if (_scoreSelectionReady)
        {
            _layoutState.ScoreProfile = Math.Clamp(PositionProfile.SelectedIndex, 0, 2);
            SavePersistentLayout();
        }
        if (_fuzzyStates is null || _technicalIndicators is null || _zweigSeries is null)
            return;
        RefreshHoldConfidence();
    }

    private void ScoreModelChanged(object sender, RoutedEventArgs e)
    {
        if (!_scoreSelectionReady || Rules1Selector is null || Rules2Selector is null)
            return;
        _layoutState.SelectedScoreModel = Rules2Selector.IsChecked == true ? "Rules2" : "Rules1";
        SavePersistentLayout();
        ApplySelectedScoreModel();
        PopulateMarketSetupRuleTree();
        RefreshMarketSetupData();
        if (_rules1Confidence is not null)
            QueueScoreExports();
    }

    private void ApplySelectedScoreModel()
    {
        bool rules2 = Rules2Selector.IsChecked == true;
        _holdConfidence = rules2 ? _rules2Confidence : _rules1Confidence;
        FuzzyChart.SetHoldConfidence(_holdConfidence);
        FuzzyChart.SetScoreComparison(_rules1Confidence, _rules2Confidence);
        TitanicChart.SetHoldConfidence(_holdConfidence);
        TitanicChart.SetScoreModelName(rules2 ? "Rules2" : "Rules1");
        ZweigChart.SetHoldConfidence(_holdConfidence);
        ZweigChart.SetScoreModelName(rules2 ? "Rules2" : "Rules1");
        if (_cursorDate.HasValue)
            ApplyCursorValues(_cursorDate.Value);
    }

    private void QueueScoreExports()
    {
        if (_rules1Confidence is null && _rules2Confidence is null) return;
        int generation = Interlocked.Increment(ref _holdConfidenceExportGeneration);
        _ = ExportHoldConfidenceAsync(_rules1Confidence, _rules2Confidence,
            _holdConfidence, CurrentScoreModel, generation);
    }

    private void SetActiveZweigMode(bool exportConfidence = true)
    {
        _zweigSeries = UseWorkingZweig.IsChecked == true ? _zweigWorkingSeries : _zweigStrongSeries;
        if (_zweigSeries is null)
            return;
        ZweigChart.SetData(_sp500PriceSeries, _zweigSeries);
        ZweigChart.SetShowCandles(ShowSp500Candles.IsChecked == true);
        UpdateZweigModeText();
        if (exportConfidence)
            RefreshHoldConfidence();
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
        var issues = new List<string>();
        bool modelError = false;
        if (!ResearchHoldConfidenceCalculator.TryCalculate(
                profile, out HoldConfidenceSeries research, out string researchError,
                out ResearchCalculationFailure failure))
        {
            modelError = true;
            string message = failure == ResearchCalculationFailure.SnapshotNotReady
                ? $"Rules1 пока недоступна: {researchError}"
                : $"Rules1 недоступна: {researchError}";
            _rules1Confidence = ModelSeriesState.Unavailable(_rules1Confidence, message);
            issues.Add(message + (_rules1Confidence is null ? string.Empty :
                $"; показан последний корректный ряд до {_rules1Confidence.LastDate:yyyy-MM-dd} как устаревший"));
        }
        else _rules1Confidence = ModelSeriesState.Available(research);
        if (Rules2HoldConfidenceCalculator.TryCalculate(profile, out HoldConfidenceSeries rules2, out string rules2Error))
        {
            _rules2Confidence = ModelSeriesState.Available(rules2, rules2Error);
            if (!string.IsNullOrEmpty(rules2Error)) issues.Add(rules2Error);
        }
        else
        {
            modelError = true;
            string message = string.IsNullOrWhiteSpace(rules2Error) ? "Rules2 недоступна." : rules2Error;
            _rules2Confidence = ModelSeriesState.Unavailable(_rules2Confidence, message);
            issues.Add(message + (_rules2Confidence is null ? string.Empty :
                $"; показан последний корректный ряд до {_rules2Confidence.LastDate:yyyy-MM-dd} как устаревший"));
        }
        _modelStatusMessage = issues.Count == 0
            ? $"Rules1 и Rules2 рассчитаны по текущему снимку до {new[] { _rules1Confidence?.LastDate, _rules2Confidence?.LastDate }.Where(d => d.HasValue).Min():yyyy-MM-dd}."
            : string.Join(" ", issues);
        _modelStatusLevel = modelError ? BreadthProgressLevel.Error :
            issues.Count == 0 ? BreadthProgressLevel.Normal : BreadthProgressLevel.Waiting;
        ApplySelectedScoreModel();
        SetStatus(_modelStatusMessage, _modelStatusLevel);

        if (!export)
            return;
        QueueScoreExports();
    }

    private async Task ExportHoldConfidenceAsync(HoldConfidenceSeries? rules1, HoldConfidenceSeries? rules2,
        HoldConfidenceSeries? selected, string selectedModel, int generation)
    {
        try
        {
            await Task.Run(() => ExportHoldConfidenceSnapshot(rules1, rules2, selected, selectedModel, generation));
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
        UpdateTradingCursorValues(date);
        SentimentSpChart.SetCursorDate(date);
        SentimentIndicatorChart.SetCursorDate(date);
        MarketSetupSpChart.SetCursorDate(date);
        MarketSetupIndicatorChart.SetCursorDate(date);
        MarketSetupAtrpChart.SetCursorDate(date);
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
                  : $"   {hold.Model}: {(double.IsFinite(hold.Value) ? hold.Value.ToString("F1") : "нет данных")} " +
                    $"({(ModelPositionSizing.TargetContracts(hold) == 0 ? "вне рынка" : "позиция открыта")})");
        TechnicalIndicatorPoint? technical = FindPoint(_technicalIndicators, date);
        SentimentPoint? sentiment = FindPoint(_sentimentSeries, date);
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
            : $"   Кривая VIX New: {HumanLanguage.CurveState(curve.State)}; " +
              $"участок 30–60 дней {curve.Contango30To60Pct:+0.00;-0.00;0.00}%; " +
               $"оценка формы {curve.CurveRegimeScore:+0.00;-0.00;0.00}" +
               (curve.LegacyState == curve.State
                   ? string.Empty
                   : $"; Old: {HumanLanguage.CurveState(curve.LegacyState)}");
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
        if (ReferenceEquals(BreadthTabs.SelectedItem, SentimentTab))
        {
            CursorValuesText.Text = sentiment is null
                ? $"{date:yyyy-MM-dd}   Sentiment: no value"
                : $"{date:yyyy-MM-dd}   CNN Fear Greed: {FormatSentimentValue(sentiment.CnnFearGreed)}   " +
                  $"Risk Measure: {FormatSentimentValue(sentiment.RiskMeasure)}   " +
                  $"Panic Euphoria: {FormatSentimentValue(sentiment.PanicEuphoria)}   " +
                  $"ARSI: {FormatSentimentValue(sentiment.Arsi)}   " +
                  $"MyRSI 14,8: {FormatSentimentValue(sentiment.MyRsi)}   " +
                  $"Trend Score: {FormatSentimentValue(sentiment.TrendScore)}   " +
                  $"Hurst: {FormatSentimentValue(sentiment.Hurst)}";
            CursorIndicatorsText.Text = sentiment is null
                ? "Sentiment indicators: no value"
                : $"CMF: {FormatSentimentValue(sentiment.Cmf)}   " +
                  $"Decibel: {FormatSentimentValue(sentiment.Decibel)}   " +
                  $"Jocker: {FormatSentimentValue(sentiment.Jocker)}   " +
                  $"Market Breadth: {FormatSentimentValue(sentiment.MarketBreadth)}   " +
                  $"Dumb Money: {FormatSentimentValue(sentiment.DumbMoney)}   " +
                  $"Smart Money: {FormatSentimentValue(sentiment.SmartMoney)}   " +
                  (technical is null
                      ? "EMA: no value"
                      : $"EMA20: {technical.Ema20:F1}   EMA50: {technical.Ema50:F1}   " +
                        $"EMA100: {technical.Ema100:F1}   EMA200: {technical.Ema200:F1}");
            return;
        }
        if (ReferenceEquals(BreadthTabs.SelectedItem, MarketSetupTab))
        {
            MarketSetupPoint? setupPoint = _marketSetupSeries is null ? null : FindPoint(_marketSetupSeries, date);
            double setupValue = _selectedMarketSetupRule is null || _marketSetupSeries is null
                ? double.NaN
                : setupPoint?.Value(_selectedMarketSetupRule.Id) ?? double.NaN;
            string ruleText = _selectedMarketSetupEntry is null
                ? "Выберите правило"
                : $"{_selectedMarketSetupEntry.Id} {_selectedMarketSetupEntry.NameRu}: " +
                  (_selectedMarketSetupRule is null ? "исследовательская запись; рабочий детектор не подключён" : FormatSentimentValue(setupValue));
            if (_selectedMarketSetupEntry is not null)
            {
                if (_selectedMarketSetupRule?.Id == "T02" && double.IsFinite(setupValue))
                    ruleText += $" — растут {setupValue * 4:0} из 4 EMA";
                MarketSetupRuleDescription.Text = (_selectedMarketSetupRule is null ? string.Empty :
                    MarketSetupDescription.AtCursor(_selectedMarketSetupRule, setupPoint)) + DescribeMarketSetupEntry(_selectedMarketSetupEntry);
            }
            CursorValuesText.Text = $"{date:yyyy-MM-dd}   {ruleText}";
            CursorIndicatorsText.Text = sentiment is null
                ? "Нет значений выбранных индикаторов"
                : BuildSelectedMarketSetupValues(sentiment, technical);
            return;
        }

        CursorValuesText.Text =
            $"{date:yyyy-MM-dd}   S&P SMA50: {sp50.Value:F1}%   " +
            $"S&P SMA100: {sp100.Value:F1}%   S&P SMA200: {sp200.Value:F1}%   " +
            $"Nasdaq SMA100: {nasdaq100.Value:F1}%{fuzzyText}";
        CursorIndicatorsText.Text = technical is null
            ? "MyRSI_rep + AGC: no value"
            : $"MyRSI 14,8: {technical.MyRsiRep:F3}   MyRSI 10,8: {technical.MyRsiRep10:F3}   " +
              $"ATRP(1): {technical.Atrp1:F2}%" +
              $"{curveText}{svxyRecommendationText}{zweigText}{titanicText}";
    }

    private static string FormatSentimentValue(double value) =>
        double.IsFinite(value) ? value.ToString("F3", CultureInfo.InvariantCulture) : "—";

    private string BuildSelectedMarketSetupValues(SentimentPoint sentiment, TechnicalIndicatorPoint? technical)
    {
        var values = new List<string>();
        void Add(CheckBox? checkBox, SentimentIndicator indicator)
        {
            if (checkBox?.IsChecked == true)
                values.Add($"{SentimentIndicatorCatalog.DisplayName(indicator)}: " +
                           FormatSentimentValue(sentiment.Value(indicator)));
        }

        Add(MarketSetupCnnFearGreed, SentimentIndicator.CnnFearGreed);
        Add(MarketSetupRiskMeasure, SentimentIndicator.RiskMeasure);
        Add(MarketSetupPanicEuphoria, SentimentIndicator.PanicEuphoria);
        Add(MarketSetupArsi, SentimentIndicator.Arsi);
        Add(MarketSetupMyRsi, SentimentIndicator.MyRsi);
        Add(MarketSetupMyRsiFast, SentimentIndicator.MyRsiFast);
        Add(MarketSetupTrendScore, SentimentIndicator.TrendScore);
        foreach ((SentimentIndicator indicator, CheckBox selector) in _marketSetupExtraSelectors)
            Add(selector, indicator);
        Add(MarketSetupHurst, SentimentIndicator.Hurst);
        Add(MarketSetupCmf, SentimentIndicator.Cmf);
        Add(MarketSetupDecibel, SentimentIndicator.Decibel);
        Add(MarketSetupJocker, SentimentIndicator.Jocker);
        Add(MarketSetupMarketBreadth, SentimentIndicator.MarketBreadth);
        Add(MarketSetupDumbMoney, SentimentIndicator.DumbMoney);
        Add(MarketSetupSmartMoney, SentimentIndicator.SmartMoney);
        if (MarketSetupEma?.IsChecked == true && technical is not null)
        {
            values.Add($"EMA20: {technical.Ema20:F1}");
            values.Add($"EMA50: {technical.Ema50:F1}");
            values.Add($"EMA100: {technical.Ema100:F1}");
            values.Add($"EMA200: {technical.Ema200:F1}");
        }
        if (_cursorDate.HasValue)
        {
            void AddBreadth(CheckBox? selector, BreadthSeries? series, string label)
            {
                if (selector?.IsChecked == true)
                {
                    BreadthPoint? point = FindPoint(series, _cursorDate.Value);
                    values.Add($"{label}: {(point is null ? "—" : point.Value.ToString("F1", CultureInfo.InvariantCulture) + "%")}");
                }
            }
            AddBreadth(MarketSetupSp50, _sp50Series, "S&P SMA50");
            AddBreadth(MarketSetupSp100, _sp100Series, "S&P SMA100");
            AddBreadth(MarketSetupSp200, _sp200Series, "S&P SMA200");
            AddBreadth(MarketSetupNasdaq100, _nasdaq100Series, "Nasdaq SMA100");
        }
        if (MarketSetupAtrp?.IsChecked == true && technical is not null)
            values.Add($"ATRP(1): {technical.Atrp1.ToString("F2", CultureInfo.InvariantCulture)}%");
        return values.Count == 0 ? "Индикаторы не выбраны" : string.Join("   ", values);
    }

    private void UpdateOperationalPosition(
        DateTime date,
        HoldConfidencePoint? hold,
        VixCurveStatePoint? curve)
    {
        if (OperationalPositionTitle is null || OperationalPositionText is null ||
            OperationalPositionBorder is null)
            return;

        OperationalPositionTitle.Text = $"{(Rules2Selector.IsChecked == true ? "Rules2" : "Rules1")} · {date:yyyy-MM-dd}";
        UpdateExpirationWarning(date);
        if (hold is null)
        {
            string unavailable = Rules2Selector.IsChecked == true
                ? _rules2Confidence?.Status ?? "Rules2 недоступна: достоверный ряд оценки отсутствует."
                : _rules1Confidence?.Status ?? "Rules1 недоступна: достоверный ряд оценки отсутствует.";
            OperationalPositionText.Text = $"Расчёт модели: данных нет\nЦелевая позиция: данных нет\nДействие: ждать\n{unavailable}";
            OperationalPositionBorder.Background = new SolidColorBrush(Color.FromRgb(245, 247, 249));
            OperationalPositionBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(185, 200, 213));
            if (TopContributionsBorder is not null)
                TopContributionsBorder.Visibility = Visibility.Collapsed;
            return;
        }

        int target = ModelPositionSizing.TargetContracts(hold);
        HoldConfidencePoint? previous = FindPreviousPoint(_holdConfidence, hold.Date);
        int previousTarget = previous is null ? 0 : ModelPositionSizing.TargetContracts(previous);
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
            : $"{previous.Date:yyyy-MM-dd}: оценка {(double.IsFinite(previous.Value) ? previous.Value.ToString("F1") : "нет данных")}, позиция {previousTarget} из 10";
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
        string thresholdExplanation = hold.EntryThreshold == hold.ExitThreshold
            ? $"Порог срабатывания: {hold.EntryThreshold:0.##}"
            : $"Вход: {hold.EntryThreshold:0.##}; выход: {hold.ExitThreshold:0.##}";
        OperationalPositionTitle.Text =
            $"{hold.Model} · {model} · {date:yyyy-MM-dd}";
        OperationalPositionText.Inlines.Clear();
        OperationalPositionText.Inlines.Add(new Run("Оценка модели: "));
        OperationalPositionText.Inlines.Add(new Run(double.IsFinite(hold.Value) ? $"{hold.Value:F1} балла" : "нет данных") { FontWeight = FontWeights.Bold });
        OperationalPositionText.Inlines.Add(new Run($" из 100. {thresholdExplanation}.\nПозиция на следующем открытии: "));
        OperationalPositionText.Inlines.Add(new Run(target.ToString()) { FontWeight = FontWeights.Bold });
        OperationalPositionText.Inlines.Add(new Run($" из 10 контрактов.\n" +
            (_holdConfidence?.IsStale == true
                ? $"ВНИМАНИЕ: показан последний корректный ряд до {_holdConfidence.LastDate:yyyy-MM-dd}; текущий расчёт недоступен. {_holdConfidence.Status}\n"
                : string.Empty) +
            (!double.IsFinite(hold.Value) ? "Новой достоверной оценки нет; сохраняется прежняя позиция.\n" : string.Empty) +
            $"Действие: {action}.\n" +
            $"Предыдущий торговый день: {yesterday}.\n" +
            $"Наиболее сильный положительный фактор: {MainHoldSupport(hold)}.\n" +
            $"Наиболее сильный отрицательный фактор: {MainHoldRisk(hold)}.\n" +
            $"Кривая фьючерсов VIX: {curveState}.\n" +
            $"SVXY: {svxy}."));
        OperationalPositionText.ToolTip = hold.Model == "Rules2"
            ? $"Нормированная шкала Rules2 0–100. Исходная оценка: {(double.IsFinite(hold.RawValue) ? hold.RawValue.ToString("F4") : "нет данных")}."
            : "Исходная оценка Rules1 по шкале 0–100.";

        if (TopContributionsBorder is not null && TopContributionsTitle is not null && TopContributionsText is not null)
        {
            bool hasContributions = !string.IsNullOrWhiteSpace(hold.TopContributions);
            TopContributionsBorder.Visibility = hasContributions ? Visibility.Visible : Visibility.Collapsed;
            if (hasContributions)
            {
                TopContributionsTitle.Text =
                    $"ТОП-10 ФАКТОРОВ {hold.Model} · {HumanLanguage.Profile(hold.Profile)} " +
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
        if (Rules2Selector.IsChecked == true)
        {
            if (hold is null) return $"{date:yyyy-MM-dd} · Rules2\nРасчёт Rules2 пока недоступен. Проверьте исходные данные и строку состояния.";
            string score = double.IsFinite(hold.Value) ? $"{hold.Value:F1} из 100" : "нет достоверного значения";
            return $"{date:yyyy-MM-dd} · Rules2 {hold.Profile}\n" +
                (_rules2Confidence?.IsStale == true
                    ? $"ВНИМАНИЕ: ряд устарел; последняя корректная дата {_rules2Confidence.LastDate:yyyy-MM-dd}. {_rules2Confidence.Status}\n"
                    : string.Empty) +
                $"Нормированная оценка: {score}. Вход {hold.EntryThreshold:0.##}, выход {hold.ExitThreshold:0.##}.\n" +
                $"Целевая позиция: {ModelPositionSizing.TargetContracts(hold)} из 10 на следующем открытии.\n" +
                "Между порогами и при отсутствии новой оценки сохраняется прежняя позиция.\n\n" +
                $"Поддержка: {hold.MainSupport}.\nРиск: {hold.MainRisk}.\n\n" +
                "Оценка складывается из раннего разворота и срыва восстановления, состояния широт, порядка и наклонов EMA, " +
                "ценового стресса при контанго VIX и относительного объёма SPY.\n" +
                "Полосы Rules1 и Rules2 показывают обе модели на одной временной шкале. " +
                "Phase, Contango и SVXY остаются отдельными характеристиками рынка.\n\n" +
                DescribeContangoAssessment(curve);
        }
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
        string vixText = DescribeContangoAssessment(curve);
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
        if (_rules1Confidence?.IsStale == true)
            text.AppendLine($"ВНИМАНИЕ: ряд Rules1 устарел; последняя корректная дата {_rules1Confidence.LastDate:yyyy-MM-dd}. {_rules1Confidence.Status}");
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

    private static string DescribeContangoAssessment(VixCurveStatePoint? curve)
    {
        if (curve is null)
            return "Кривая фьючерсов VIX ещё не загружена.";

        var text = new StringBuilder();
        text.Append($"Кривая фьючерсов VIX (New): {HumanLanguage.CurveState(curve.State)}; " +
                    $"оценка формы {curve.CurveRegimeScore:+0.00;-0.00;0.00}, участок 30–60 дней " +
                    $"{curve.Contango30To60Pct:+0.00;-0.00;0.00}%.");
        if (curve.LegacyState != curve.State ||
            Math.Abs(curve.LegacyCurveRegimeScore - curve.CurveRegimeScore) > 0.000001)
        {
            text.Append($" До исправления Old: {HumanLanguage.CurveState(curve.LegacyState)}, " +
                        $"оценка {curve.LegacyCurveRegimeScore:+0.00;-0.00;0.00}.");
        }
        if (!string.IsNullOrWhiteSpace(curve.AdjustmentExplanation))
            text.Append($" Исправление: {curve.AdjustmentExplanation}.");
        else
            text.Append(" Дополнительная коррекция формы и стресса не потребовалась.");
        text.Append(' ').Append(DescribeSvxyRecommendation(curve));
        return text.ToString();
    }

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
            WriteSentimentCsvFiles(stagingDirectory, snapshot.Sentiment);
            WriteTitanicCsv(Path.Combine(stagingDirectory, "titanic_sentiment.csv"), snapshot.Titanic, false);
            WriteTitanicCsv(Path.Combine(stagingDirectory, "titanic_sentiment_events.csv"), snapshot.Titanic, true);
            WriteZweigCsv(
                Path.Combine(stagingDirectory, "zweig_breadth_thrust.csv"),
                snapshot.ZweigStrong,
                snapshot.ZweigWorking);
            PromoteStagedFiles(stagingDirectory, ExportDirectory);
            ArchiveLegacySentimentFiles(ExportDirectory);
            string svxyStatus = snapshot.SvxyPrice is null
                ? " SVXY history was unavailable, so SVXY.csv was not written."
                : " SVXY OHLCV saved to SVXY.csv.";
            return $"CSV, Sentiment series, phase states, Titanic, Zweig density and both MyRSI/AGC series saved.{svxyStatus}";
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

    private void ExportHoldConfidenceSnapshot(HoldConfidenceSeries? rules1, HoldConfidenceSeries? rules2,
        HoldConfidenceSeries? selected, string selectedModel, int generation)
    {
        ExportGate.Wait();
        string? stagingDirectory = null;
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            stagingDirectory = Path.Combine(ExportDirectory, $".staging-hold-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            // Preserve the legacy Rules1 feed used by existing strategy consumers.
            WriteScoreFile(Path.Combine(stagingDirectory, "hold_confidence.csv"), rules1, displayBands: false, "Rules1");
            WriteScoreFile(Path.Combine(stagingDirectory, "rules1_score.csv"), rules1, displayBands: true, "Rules1");
            WriteScoreFile(Path.Combine(stagingDirectory, "rules2_score.csv"), rules2, displayBands: true, "Rules2");
            WriteScoreFile(Path.Combine(stagingDirectory, "selected_model.csv"), selected, displayBands: true, selectedModel);
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

    private static void ArchiveLegacySentimentFiles(string directory)
    {
        foreach (string fileName in new[]
                 {
                     "sentiment_myrsi.csv", "sentiment_rsi.csv", "sentiment_chaikin_money_flow.csv"
                 })
        {
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                string archive = Path.Combine(directory, "legacy-sentiment", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(archive);
                File.Move(path, Path.Combine(archive, fileName));
            }
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

    private static void WriteScoreFile(string path, HoldConfidenceSeries? series, bool displayBands, string model)
    {
        if (series is null)
        {
            File.WriteAllText(path,
                "Date,HoldConfidencePct,Band,Trend,Breadth,Phase,VolatilityCalm,Contango,ZweigSupport,TitanicPenalty,ShockPenalty,Rationale,Profile,MainSupport,MainRisk,ActiveEvents,Model,RawScore,EntryThreshold,ExitThreshold,TargetContracts,ScoreKnown,DataState,Status\n" +
                $",,,,,,,,,,,,,,,,,,,,,0,Unavailable,\"{model} недоступна\"\n", new UTF8Encoding(false));
            return;
        }
        WriteHoldConfidenceCsv(path, series, displayBands);
    }

    private static void WriteHoldConfidenceCsv(string path, HoldConfidenceSeries series, bool displayBands = false)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,HoldConfidencePct,Band,Trend,Breadth,Phase,VolatilityCalm,Contango,ZweigSupport,TitanicPenalty,ShockPenalty,Rationale,Profile,MainSupport,MainRisk,ActiveEvents,Model,RawScore,EntryThreshold,ExitThreshold,TargetContracts,ScoreKnown,DataState,Status");
        foreach (HoldConfidencePoint point in series.Points)
        {
            csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(double.IsFinite(point.Value) ? point.Value.ToString("0.########", CultureInfo.InvariantCulture) : string.Empty).Append(',')
                .Append(double.IsFinite(point.Value)
                    ? displayBands ? ScorePalette.Label(point.Value) : HoldConfidenceCalculator.Band(point.Value)
                    : "Unknown").Append(',')
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
                .Append('"').Append(point.ActiveEvents.Replace("\"", "\"\"")).Append("\",")
                .Append(point.Model).Append(',')
                .Append(double.IsFinite(point.Value) ? (double.IsFinite(point.RawValue) ? point.RawValue : point.Value).ToString("0.########", CultureInfo.InvariantCulture) : string.Empty).Append(',')
                .Append(point.EntryThreshold.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.ExitThreshold.ToString("0.########", CultureInfo.InvariantCulture)).Append(',')
                .Append(ModelPositionSizing.TargetContracts(point)).Append(',')
                .Append(double.IsFinite(point.Value) ? "1" : "0").Append(',')
                .Append(series.IsStale ? "Stale" : "Current").Append(',')
                .Append('"').Append(series.Status.Replace("\"", "\"\"")).AppendLine("\"");
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

    private static void WriteSentimentCsvFiles(string directory, SentimentSeries series)
    {
        var combined = new StringBuilder();
        combined.AppendLine("Date," + string.Join(",", SentimentIndicatorCatalog.All.Select(SentimentIndicatorCatalog.FileStem)));
        foreach (SentimentPoint point in series.Points)
        {
            combined.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            foreach (SentimentIndicator indicator in SentimentIndicatorCatalog.All)
                combined.Append(',').Append(FormatCsvValue(point.Value(indicator)));
            combined.AppendLine();
        }
        File.WriteAllText(
            Path.Combine(directory, "sentiment_indicators.csv"),
            combined.ToString(),
            new UTF8Encoding(false));

        foreach (SentimentIndicator indicator in SentimentIndicatorCatalog.All)
        {
            var csv = new StringBuilder();
            csv.AppendLine("Date,Value");
            foreach (SentimentPoint point in series.Points)
            {
                double value = point.Value(indicator);
                if (!double.IsFinite(value))
                    continue;
                csv.Append(point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                    .AppendLine(value.ToString("0.########", CultureInfo.InvariantCulture));
            }
            File.WriteAllText(
                Path.Combine(directory, $"sentiment_{SentimentIndicatorCatalog.FileStem(indicator)}.csv"),
                csv.ToString(),
                new UTF8Encoding(false));
        }
    }

    private static string FormatCsvValue(double value) =>
        double.IsFinite(value) ? value.ToString("0.########", CultureInfo.InvariantCulture) : string.Empty;

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

    private static SentimentPoint? FindPoint(SentimentSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;
        return FindNearest(series.Points, date, point => point.Date);
    }

    private static MarketSetupPoint? FindPoint(MarketSetupSeries? series, DateTime date)
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
                current is SentimentChart ||
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
        SentimentSpChart.SetCursorPinned(pinned);
        SentimentIndicatorChart.SetCursorPinned(pinned);
        MarketSetupSpChart.SetCursorPinned(pinned);
        MarketSetupIndicatorChart.SetCursorPinned(pinned);
        MarketSetupAtrpChart.SetCursorPinned(pinned);
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
            bool refreshMarketSetup = _marketSetupRefreshScheduled && ReferenceEquals(BreadthTabs.SelectedItem, MarketSetupTab);
            _marketSetupRefreshScheduled = false;
            if (refreshMarketSetup) MarketSetupChartGrid.UpdateLayout();
            ApplyHistoryViewport();
            if (refreshMarketSetup)
            {
                if (_viewportMaster is null || _viewportMaster.Points.Count == 0)
                {
                    MarketSetupSpChart.Refresh();
                    MarketSetupIndicatorChart.Refresh();
                    MarketSetupAtrpChart.Refresh();
                }
                if (_cursorDate.HasValue) ApplyCursorValues(_cursorDate.Value);
            }
        }));
    }

    private bool TryGetHistoryViewport(out DateTime start, out DateTime end)
    {
        start = end = default;
        if (_viewportMaster is null || _viewportMaster.Points.Count == 0)
            return false;

        int startIndex = Math.Clamp((int)Math.Round(HistorySlider.Value), 0, Math.Max(0, _viewportMaster.Points.Count - 1));
        int endIndex = Math.Min(_viewportMaster.Points.Count - 1, startIndex + _visibleBarCount - 1);
        start = _viewportMaster.Points[startIndex].Date;
        end = _viewportMaster.Points[endIndex].Date;
        return true;
    }

    private void ApplyHistoryViewport()
    {
        if (!TryGetHistoryViewport(out DateTime start, out DateTime end)) return;

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
        SentimentSpChart.SetViewport(start, end);
        SentimentIndicatorChart.SetViewport(start, end);
        MarketSetupSpChart.SetViewport(start, end);
        MarketSetupIndicatorChart.SetViewport(start, end);
        MarketSetupAtrpChart.SetViewport(start, end);
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
        TradingEnsembleChart.SetMessage("После завершения общего расчёта откройте эту вкладку: расчёт девяти ансамблей TorchSharp4GPU/TorchSharp/Jordan/Encog запустится один раз автоматически.");
        SentimentSpChart.SetMessage("S&P 500 (SPY)", "Waiting for calculation");
        SentimentIndicatorChart.SetMessage("Waiting for calculation");
        MarketSetupSpChart.SetMessage("S&P 500 (SPY) — selected market setup", "Waiting for calculation");
        MarketSetupIndicatorChart.SetMessage("Waiting for calculation");
        MarketSetupAtrpChart.SetMessage("Waiting for calculation");
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
