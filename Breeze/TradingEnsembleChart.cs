using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BreezeVL;

internal sealed class TradingEnsembleChart : Border
{
    private const int RightPaddingSlots = 2;
    private const int FutureGapSlots = 1;
    private const double PanelGap = 11.0;
    private const double PanelInset = 4.0;
    private const double PanelHeaderHeight = TradingPaneLayout.HeaderHeight;
    private const double PanelBottomInset = 8.0;
    internal const double BaseClassificationThreshold = 50.0;
    private static readonly Brush PanelBackgroundBrush =
        new SolidColorBrush(Color.FromRgb(255, 252, 244));
    private static readonly Brush PanelBorderBrush =
        new SolidColorBrush(Color.FromRgb(205, 195, 171));
    private static readonly Brush DividerBrush =
        new SolidColorBrush(Color.FromRgb(215, 207, 187));
    private static readonly Brush TitleBrush =
        new SolidColorBrush(Color.FromRgb(37, 48, 61));
    private static readonly Brush DarkAxisBrush =
        new SolidColorBrush(Color.FromRgb(30, 30, 30));
    private static readonly Brush LastValueBrush =
        new SolidColorBrush(Color.FromRgb(75, 78, 82));
    private readonly Canvas _canvas = new();
    private IReadOnlyDictionary<string, TradingEnsembleSeries> _series =
        new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
    private PriceSeries? _prices;
    private TechnicalIndicatorSeries? _technical;
    private IReadOnlyDictionary<DateTime, IReadOnlyList<double>> _chandelierStops =
        new Dictionary<DateTime, IReadOnlyList<double>>();
    private IReadOnlyDictionary<DateTime, IReadOnlyList<double>> _chandelierExecutions =
        new Dictionary<DateTime, IReadOnlyList<double>>();
    private IReadOnlyDictionary<DateTime, TradingActivePosition> _actualActivePositions =
        new Dictionary<DateTime, TradingActivePosition>();
    private IReadOnlyDictionary<DateTime, TradingActivePosition> _targetActivePositions =
        new Dictionary<DateTime, TradingActivePosition>();
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private DateTime? _cursorDate;
    private IReadOnlyList<DateTime> _dates = [];
    private Line? _cursorLine;
    private bool _cursorPinned;
    private HashSet<string> _selectedPackages = new(StringComparer.OrdinalIgnoreCase);
    private bool _showEma;
    private TradingActiveMode _activeMode = TradingActiveMode.IndependentDeciles;
    private int _recentPerformanceBars = 150;
    private bool _showPrice = true, _showCandles = true;
    private double _plotLeft, _plotTop, _plotWidth, _plotHeight, _slots = 1.0;
    private int _futureDateIndex = -1;
    private DateTime? _lastVisibleRealDate;
    private readonly double[] _dividerYs = new double[3];
    private double _priceBoundary = .52, _ensembleBoundary = .63, _decileBoundary = .91;
    private int _resizeDivider = -1;
    private int _resizeUpperPane = -1;
    private int _resizeLowerPane = -1;
    private double _lastResizeY;
    private double _panelUsableHeight = 1.0;
    private bool[] _collapsed = new bool[4];
    private double[] _paneHeights = new double[4];
    private string? _message;
    private readonly DispatcherTimer _activeLineHintTimer;
    private readonly Popup _activeLineHintPopup;
    private readonly TextBlock _activeLineHintText;
    private FrameworkElement? _activeLineHintOwner;
    private Point _activeLineHintPoint;

    public event Action<DateTime>? CursorDateChanged;
    public event Action? PaneLayoutChanged;

    public bool[] GetCollapsedPanes() => (bool[])_collapsed.Clone();
    public void SetCollapsedPanes(bool[]? value)
    {
        _collapsed = value is { Length: 4 } ? (bool[])value.Clone() : new bool[4];
        if (IsVisible) RenderSeries();
    }

    public TradingEnsembleChart()
    {
        Background = Brushes.White;
        BorderThickness = new Thickness(0);
        Margin = new Thickness(0);
        Padding = new Thickness(0);
        Child = _canvas;
        _activeLineHintText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Foreground = new SolidColorBrush(Color.FromRgb(32, 38, 44))
        };
        _activeLineHintPopup = new Popup
        {
            PlacementTarget = _canvas,
            Placement = PlacementMode.Relative,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            IsHitTestVisible = false,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(145, 145, 145)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = _activeLineHintText
            }
        };
        _activeLineHintTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _activeLineHintTimer.Tick += (_, _) => OpenActiveLineHint();
        SizeChanged += (_, _) => RenderSeries();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(new Action(RenderSeries));
            else CloseActiveLineHint();
        };
        Unloaded += (_, _) => CloseActiveLineHint();
        MouseMove += ChartMouseMove;
    }

    public void SetData(IReadOnlyDictionary<string, TradingEnsembleSeries> series,
        PriceSeries? prices, TechnicalIndicatorSeries? technical)
    {
        _series = series;
        _prices = prices;
        _technical = technical;
        _message = null;
        if (IsVisible) RenderSeries();
    }

    public void SetPackages(IEnumerable<string> packageKeys)
    {
        _selectedPackages = new HashSet<string>(packageKeys, StringComparer.OrdinalIgnoreCase);
        if (IsVisible) RenderSeries();
    }

    public void SetShowEma(bool show) { _showEma = show; if (IsVisible) RenderSeries(); }
    public void SetChandelierStops(IReadOnlyDictionary<DateTime, IReadOnlyList<double>> stops)
    {
        _chandelierStops = stops;
        if (IsVisible) RenderSeries();
    }
    public void SetChandelierExecutions(IReadOnlyDictionary<DateTime, IReadOnlyList<double>> executions)
    {
        _chandelierExecutions = executions;
        if (IsVisible) RenderSeries();
    }
    public void SetActualActivePositions(
        IReadOnlyDictionary<DateTime, TradingActivePosition> positions)
    {
        _actualActivePositions = positions;
        if (IsVisible) RenderSeries();
    }
    public void SetTargetActivePositions(
        IReadOnlyDictionary<DateTime, TradingActivePosition> positions)
    {
        _targetActivePositions = positions;
        if (IsVisible) RenderSeries();
    }
    public void SetActiveMode(TradingActiveMode mode)
    {
        _activeMode = mode;
        if (IsVisible) RenderSeries();
    }
    public void SetRecentPerformanceBars(int bars)
    {
        _recentPerformanceBars = Math.Max(10, bars);
        if (IsVisible) RenderSeries();
    }
    public void SetShowPrice(bool show) { _showPrice = show; if (IsVisible) RenderSeries(); }
    public void SetShowCandles(bool show) { _showCandles = show; if (IsVisible) RenderSeries(); }
    public void SetViewport(DateTime start, DateTime end) { _viewportStart = start; _viewportEnd = end; if (IsVisible) RenderSeries(); }
    public void SetCursorDate(DateTime? date) { _cursorDate = date; if (IsVisible) DrawCursor(); }
    public void SetCursorPinned(bool pinned) { _cursorPinned = pinned; if (IsVisible) DrawCursor(); }
    public void Refresh() => RenderSeries();
    public void SetMessage(string message) { _message = message; RenderSeries(); }

    public (double Price, double Ensemble, double Decile) GetPaneBoundaries() =>
        (_priceBoundary, _ensembleBoundary, _decileBoundary);

    public void SetPaneBoundaries(double price, double ensemble, double decile)
    {
        if (!double.IsFinite(price) || !double.IsFinite(ensemble) || !double.IsFinite(decile) ||
            price < .001 || ensemble < price + .001 || decile < ensemble + .001 || decile > .999)
            return;
        _priceBoundary = price;
        _ensembleBoundary = ensemble;
        _decileBoundary = decile;
        if (IsVisible)
            RenderSeries();
    }

    public bool IsOverPaneDivider(Point point) =>
        _dates.Count > 1 && Enumerable.Range(0, _dividerYs.Length).Any(index =>
            double.IsFinite(_dividerYs[index]) &&
            TryGetResizePair(index, out _, out _) &&
            Math.Abs(point.Y - _dividerYs[index]) <= 7.0);

    public bool TryBeginPaneResize(Point point)
    {
        if (_dates.Count < 2 || _dividerYs.All(double.IsNaN)) return false;
        int divider = Enumerable.Range(0, _dividerYs.Length)
            .Where(index => double.IsFinite(_dividerYs[index]) && TryGetResizePair(index, out _, out _))
            .OrderBy(index => Math.Abs(point.Y - _dividerYs[index]))
            .DefaultIfEmpty(-1)
            .First();
        if (divider < 0)
            return false;
        if (Math.Abs(point.Y - _dividerYs[divider]) > 7.0)
            return false;
        if (!TryGetResizePair(divider, out _resizeUpperPane, out _resizeLowerPane))
            return false;
        _resizeDivider = divider;
        _lastResizeY = point.Y;
        Cursor = Cursors.SizeNS;
        return true;
    }

    public void ResizePane(Point point)
    {
        if (_resizeDivider < 0 || _resizeUpperPane < 0 || _resizeLowerPane < 0)
            return;
        double[] weights = [_priceBoundary, _ensembleBoundary - _priceBoundary,
            _decileBoundary - _ensembleBoundary, 1 - _decileBoundary];
        int upper = _resizeUpperPane;
        int lower = _resizeLowerPane;
        double pairHeight = _paneHeights[upper] + _paneHeights[lower];
        double minimum = Math.Min(TradingPaneLayout.MinimumExpandedHeight, pairHeight / 2);
        double first = Math.Clamp(_paneHeights[upper] + point.Y - _lastResizeY, minimum, pairHeight - minimum);
        double pairWeight = weights[upper] + weights[lower];
        weights[upper] = pairWeight * first / pairHeight;
        weights[lower] = pairWeight - weights[upper];
        _priceBoundary = weights[0];
        _ensembleBoundary = weights[0] + weights[1];
        _decileBoundary = 1 - weights[3];
        _lastResizeY = point.Y;
        RenderSeries();
    }

    public void EndPaneResize()
    {
        _resizeDivider = -1;
        _resizeUpperPane = -1;
        _resizeLowerPane = -1;
        Cursor = null;
    }

    private bool TryGetResizePair(int divider, out int upper, out int lower)
    {
        upper = -1;
        lower = -1;
        for (int index = Math.Min(divider, _collapsed.Length - 1); index >= 0; index--)
            if (!_collapsed[index])
            {
                upper = index;
                break;
            }
        for (int index = Math.Max(0, divider + 1); index < _collapsed.Length; index++)
            if (!_collapsed[index])
            {
                lower = index;
                break;
            }
        return upper >= 0 && lower >= 0;
    }

    private void RenderSeries()
    {
        if (ActualWidth < 150 || ActualHeight < 180) return;
        CloseActiveLineHint();
        _canvas.Children.Clear();
        _cursorLine = null;

        var selected = SelectedSeries().ToList();
        if (selected.Count == 0)
        {
            _dates = [];
            _futureDateIndex = -1;
            _lastVisibleRealDate = null;
            AddText("Trading Ensemble — TorchSharp4GPU / TorchSharp / Jordan / Encog", 12, 9, 13, Brushes.Black, FontWeights.SemiBold);
            AddText(_message ?? "Включите хотя бы один торговый ансамбль.",
                12, 36, 11, Brushes.DimGray, FontWeights.Normal);
            return;
        }

        IEnumerable<DateTime> realDates = _prices?.Points.Select(point => point.Date.Date) ??
            selected.SelectMany(item => item.Points.Select(point => point.Date.Date));
        IEnumerable<DateTime> visibleDates = realDates
            .Where(date => !_viewportStart.HasValue || date >= _viewportStart.Value)
            .Where(date => !_viewportEnd.HasValue || date <= _viewportEnd.Value);
        DateTime? lastRealDate = realDates.Cast<DateTime?>().Max();
        bool latestBarVisible = lastRealDate.HasValue &&
            (!_viewportStart.HasValue || lastRealDate.Value >= _viewportStart.Value) &&
            (!_viewportEnd.HasValue || lastRealDate.Value <= _viewportEnd.Value);
        if (latestBarVisible)
        {
            visibleDates = visibleDates.Concat(selected.SelectMany(item => item.Forecasts)
                .Where(point => point.IsNextBar && point.Date.Date > lastRealDate!.Value.Date)
                .Select(point => point.Date.Date));
            visibleDates = visibleDates.Concat(_chandelierStops.Keys
                .Where(date => date.Date > lastRealDate!.Value.Date));
            visibleDates = visibleDates.Concat(_targetActivePositions.Keys
                .Where(date => date.Date > lastRealDate!.Value.Date));
        }
        _dates = visibleDates.Distinct().OrderBy(date => date).ToList();
        _lastVisibleRealDate = _dates
            .Where(date => !lastRealDate.HasValue || date.Date <= lastRealDate.Value.Date)
            .Cast<DateTime?>()
            .Max();
        _futureDateIndex = -1;
        if (lastRealDate.HasValue)
        {
            for (int index = 0; index < _dates.Count; index++)
                if (_dates[index] > lastRealDate.Value)
                {
                    _futureDateIndex = index;
                    break;
                }
        }
        if (_dates.Count < 2)
        {
            AddText("Trading Ensemble — TorchSharp4GPU / TorchSharp / Jordan / Encog", 12, 9, 13, Brushes.Black, FontWeights.SemiBold);
            AddText(_message ?? (selected.Count == 0 ? "Включите хотя бы один торговый ансамбль." : "Нет данных в выбранном диапазоне."),
                12, 36, 11, Brushes.DimGray, FontWeights.Normal);
            return;
        }

        double left = 54, right = 45, top = 4, bottom = 4;
        double width = Math.Max(1, ActualWidth - left - right);
        double height = Math.Max(1, ActualHeight - top - bottom);
        double gap = PanelGap;
        double usable = height - gap * 3;
        _panelUsableHeight = usable;
        _paneHeights = TradingPaneLayout.Calculate(usable,
            new[] { _priceBoundary, _ensembleBoundary - _priceBoundary, _decileBoundary - _ensembleBoundary, 1 - _decileBoundary }, _collapsed);
        double priceHeight = _paneHeights[0], ensembleHeight = _paneHeights[1], decileHeight = _paneHeights[2], activeHeight = _paneHeights[3];
        double priceTop = top, ensembleTop = priceTop + priceHeight + gap;
        double decileTop = ensembleTop + ensembleHeight + gap, activeTop = decileTop + decileHeight + gap;
        _dividerYs[0] = priceTop + priceHeight + gap / 2.0;
        _dividerYs[1] = ensembleTop + ensembleHeight + gap / 2.0;
        _dividerYs[2] = decileTop + decileHeight + gap / 2.0;
        double pricePlotTop = priceTop + PanelHeaderHeight;
        double ensemblePlotTop = ensembleTop + PanelHeaderHeight;
        double decilePlotTop = decileTop + PanelHeaderHeight;
        double activePlotTop = activeTop + PanelHeaderHeight;
        double pricePlotHeight = Math.Max(1, priceHeight - PanelHeaderHeight - PanelBottomInset);
        double ensemblePlotHeight = Math.Max(1, ensembleHeight - PanelHeaderHeight - PanelBottomInset);
        double decilePlotHeight = Math.Max(1, decileHeight - PanelHeaderHeight - PanelBottomInset);
        double activePlotHeight = Math.Max(1, activeHeight - PanelHeaderHeight - PanelBottomInset);
        _plotLeft = left;
        _plotTop = pricePlotTop;
        _plotWidth = width;
        _plotHeight = activePlotTop + activePlotHeight - pricePlotTop;
        _slots = Math.Max(1, _dates.Count - 1 + RightPaddingSlots +
            (_futureDateIndex >= 0 ? FutureGapSlots : 0));
        double X(int index) => left + width * DataCoordinate(index) / _slots;

        double panelWidth = Math.Max(1, ActualWidth - PanelInset * 2);
        DrawPanelFrame(PanelInset, panelWidth, priceTop, priceHeight);
        DrawPanelFrame(PanelInset, panelWidth, ensembleTop, ensembleHeight);
        DrawPanelFrame(PanelInset, panelWidth, decileTop, decileHeight);
        DrawPanelFrame(PanelInset, panelWidth, activeTop, activeHeight);
        DrawRecentPerformanceBoundary(selected, X, pricePlotTop,
            activePlotTop + activePlotHeight - pricePlotTop);
        DrawPaneDividers(PanelInset, panelWidth);

        if (!_collapsed[0]) DrawPrice(X, pricePlotTop, pricePlotHeight);
        if (!_collapsed[1] || !_collapsed[2]) DrawPercentPanels(selected, X, ensemblePlotTop, ensemblePlotHeight, decilePlotTop, decilePlotHeight);
        ActiveSummary? lastActive = _collapsed[3]
            ? null
            : DrawActive(selected, X, activePlotTop, activePlotHeight);
        DrawPanelTitle(priceTop, "S&P 500 (SPY)", 0);
        DrawPanelTitle(ensembleTop, "Ensemble %", 1);
        DrawPanelTitle(decileTop, "Deciles, %", 2);
        DrawPanelTitle(activeTop, ActivePaneTitle(), 3);
        double? lastClose = LatestVisibleClose();
        if (lastClose.HasValue)
        {
            double? lastStop = LatestVisibleStop();
            string lastText = $"Last: {lastClose.Value:N2}" +
                              (lastStop.HasValue ? $"   Stop: {lastStop.Value:N2}" : "   Stop: —");
            AddText(lastText, Math.Max(left + 120, ActualWidth - 245),
                priceTop + 7, 11, LastValueBrush, FontWeights.Normal);
        }
        if (lastActive.HasValue)
            AddText(FormatActiveSummary(lastActive.Value),
                Math.Max(left + 120, ActualWidth - 330),
                activeTop + 7, 11, LastValueBrush, FontWeights.Normal);
        if (_collapsed.All(c => c)) _plotHeight = 0;
        DrawCursor();
    }

    private void DrawRecentPerformanceBoundary(
        IReadOnlyList<TradingEnsembleSeries> selected,
        Func<int, double> x,
        double top,
        double height)
    {
        if (_prices?.Points is not { } prices || prices.Count <= _recentPerformanceBars ||
            selected.Count == 0 || _dates.Count == 0)
            return;

        DateTime commonEnd = selected.Min(item => item.Points.Count == 0
            ? DateTime.MaxValue
            : item.Points[^1].Date.Date);
        if (commonEnd == DateTime.MaxValue)
            return;
        int endIndex = -1;
        for (int index = prices.Count - 1; index >= 0; index--)
            if (prices[index].Date.Date <= commonEnd)
            {
                endIndex = index;
                break;
            }
        int startIndex = endIndex - _recentPerformanceBars;
        if (startIndex < 0)
            return;

        DateTime boundaryDate = prices[startIndex].Date.Date;
        if (boundaryDate < _dates[0].Date || boundaryDate > _dates[^1].Date)
            return;
        int dateIndex = Enumerable.Range(0, _dates.Count)
            .MinBy(index => Math.Abs((_dates[index].Date - boundaryDate).Ticks));
        double slotWidth = _plotWidth / Math.Max(1.0, _slots);
        var marker = new Rectangle
        {
            Width = Math.Max(3.0, slotWidth * 0.55),
            Height = Math.Max(1.0, height),
            Fill = new SolidColorBrush(Color.FromRgb(134, 220, 151)),
            Opacity = 0.5,
            IsHitTestVisible = false
        };
        System.Windows.Automation.AutomationProperties.SetName(
            marker, "Recent trading bars boundary");
        Canvas.SetLeft(marker, x(dateIndex) - marker.Width / 2.0);
        Canvas.SetTop(marker, top);
        _canvas.Children.Add(marker);
    }

    private void DrawPrice(Func<int, double> x, double top, double height)
    {
        if (_prices is null || !_showPrice) return;
        var byDate = _prices.Points.ToDictionary(point => point.Date.Date);
        int futureIndex = _futureDateIndex;
        PricePoint? referencePoint = _lastVisibleRealDate.HasValue &&
            byDate.TryGetValue(_lastVisibleRealDate.Value.Date, out PricePoint? latestPoint)
                ? latestPoint
                : null;
        var values = _dates.Select(date => byDate.TryGetValue(date, out PricePoint? point) ? point.Close : double.NaN).ToArray();
        var lines = new List<(double[] Values, Brush Brush, double Thickness)>();
        if (!_showCandles) lines.Add((values, Brushes.Black, 1.35));
        if (_showEma && _technical is not null)
        {
            var technical = _technical.Points.ToDictionary(point => point.Date.Date);
            lines.Add((_dates.Select(d => technical.TryGetValue(d, out TechnicalIndicatorPoint? p) ? p.Ema20 : double.NaN).ToArray(), Brushes.DodgerBlue, 1.0));
            lines.Add((_dates.Select(d => technical.TryGetValue(d, out TechnicalIndicatorPoint? p) ? p.Ema50 : double.NaN).ToArray(), Brushes.DarkOrange, 1.0));
            lines.Add((_dates.Select(d => technical.TryGetValue(d, out TechnicalIndicatorPoint? p) ? p.Ema100 : double.NaN).ToArray(), Brushes.MediumPurple, 1.0));
            lines.Add((_dates.Select(d => technical.TryGetValue(d, out TechnicalIndicatorPoint? p) ? p.Ema200 : double.NaN).ToArray(), Brushes.ForestGreen, 1.0));
        }

        double[] priceExtremes = _dates.SelectMany(date => byDate.TryGetValue(date, out PricePoint? point)
            ? new[] { point.Low, point.High } : Array.Empty<double>()).ToArray();
        double[] visibleStops = _dates
            .Select(date => TryGetMaximumStop(date, out double stop) ? stop : double.NaN)
            .Where(double.IsFinite)
            .ToArray();
        double[] finite = priceExtremes.Concat(lines.SelectMany(item => item.Values))
            .Concat(visibleStops).Where(double.IsFinite).ToArray();
        if (futureIndex >= 0 && referencePoint is not null && double.IsFinite(referencePoint.Close))
            finite = finite.Concat([referencePoint.Close * 0.98, referencePoint.Close * 1.02]).ToArray();
        if (finite.Length < 2) return;
        double minimum = finite.Min(), maximum = finite.Max();
        double padding = Math.Max((maximum - minimum) * .05, 1e-6); minimum -= padding; maximum += padding;
        double Y(double value) => top + height * (maximum - value) / (maximum - minimum);

        DrawScaleGrid(top, height, minimum, maximum);

        if (_showCandles)
        {
            double bodyWidth = ChartDrawingMetrics.CandleBodyWidth(_plotWidth / Math.Max(1.0, _slots));
            for (int index = 0; index < _dates.Count; index++)
            {
                if (!byDate.TryGetValue(_dates[index], out PricePoint? point)) continue;
                Brush brush = point.Close >= point.Open ? Brushes.ForestGreen : Brushes.Firebrick;
                double center = x(index), open = Y(point.Open), close = Y(point.Close);
                _canvas.Children.Add(new Line { X1=center, X2=center, Y1=Y(point.High), Y2=Y(point.Low), Stroke=brush, StrokeThickness=1, IsHitTestVisible=false });
                var body = new Rectangle { Width=bodyWidth, Height=Math.Max(1, Math.Abs(close-open)), Stroke=brush,
                    Fill=point.Close >= point.Open ? PanelBackgroundBrush : brush, StrokeThickness=1, IsHitTestVisible=false };
                Canvas.SetLeft(body, center-bodyWidth/2); Canvas.SetTop(body, Math.Min(open,close)); _canvas.Children.Add(body);
            }
        }
        if (futureIndex >= 0 && referencePoint is not null && double.IsFinite(referencePoint.Close))
        {
            double markerWidth = ChartDrawingMetrics.CandleBodyWidth(
                _plotWidth / Math.Max(1.0, _slots)) * 2.0;
            double upper = Y(referencePoint.Close * 1.02);
            double lower = Y(referencePoint.Close * 0.98);
            var forecastScale = new Rectangle
            {
                Width = markerWidth,
                Height = Math.Max(2.0, lower - upper),
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0.0),
                    EndPoint = new Point(0.5, 1.0),
                    GradientStops = new GradientStopCollection
                    {
                        new(Color.FromRgb(0, 165, 35), 0.0),
                        new(Color.FromRgb(255, 220, 20), 0.38),
                        new(Color.FromRgb(255, 220, 20), 0.62),
                        new(Color.FromArgb(179, 255, 0, 0), 1.0)
                    }
                },
                Stroke = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                StrokeThickness = .7,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(forecastScale, x(futureIndex) - markerWidth / 2.0);
            Canvas.SetTop(forecastScale, upper);
            _canvas.Children.Add(forecastScale);
        }
        foreach (var line in lines) DrawLine(line.Values, x, Y, line.Brush, line.Thickness);
        double dotSize = Math.Clamp(_plotWidth / Math.Max(1.0, _slots) * 0.18, 2.0, 4.0);
        for (int index = 0; index < _dates.Count; index++)
        {
            if (!TryGetMaximumStop(_dates[index], out double stop))
                continue;
            bool projected = index == _futureDateIndex;
            var marker = new Ellipse
            {
                Width = projected ? Math.Max(6.0, dotSize * 1.8) : dotSize,
                Height = projected ? Math.Max(6.0, dotSize * 1.8) : dotSize,
                Fill = projected ? Brushes.Transparent : Brushes.Black,
                Stroke = Brushes.Black,
                StrokeThickness = projected ? 1.5 : .35,
                IsHitTestVisible = false
            };
            if (projected)
                System.Windows.Automation.AutomationProperties.SetName(
                    marker, "Projected next-session chandelier stop");
            Canvas.SetLeft(marker, x(index) - marker.Width / 2.0);
            Canvas.SetTop(marker, Y(stop) - marker.Height / 2.0);
            _canvas.Children.Add(marker);
        }
        double crossSize = Math.Clamp(_plotWidth / Math.Max(1.0, _slots) * 0.42, 6.0, 9.0);
        for (int index = 0; index < _dates.Count; index++)
        {
            if (!TryGetMaximumExecution(_dates[index], out double execution))
                continue;
            double centerX = x(index);
            double centerY = Y(execution);
            double half = crossSize / 2.0;
            _canvas.Children.Add(new Line
            {
                X1 = centerX - half, Y1 = centerY - half,
                X2 = centerX + half, Y2 = centerY + half,
                Stroke = Brushes.Black, StrokeThickness = 1.6,
                IsHitTestVisible = false
            });
            _canvas.Children.Add(new Line
            {
                X1 = centerX - half, Y1 = centerY + half,
                X2 = centerX + half, Y2 = centerY - half,
                Stroke = Brushes.Black, StrokeThickness = 1.6,
                IsHitTestVisible = false
            });
        }
    }

    private void DrawPercentPanels(IReadOnlyList<TradingEnsembleSeries> selected, Func<int, double> x,
        double ensembleTop, double ensembleHeight, double decileTop, double decileHeight)
    {
        bool bossMode = selected.Count > 1;
        var visibleDates = _dates.ToHashSet();
        var ensembleValues = new List<double> { BaseClassificationThreshold };
        var decileValues = new List<double> { BaseClassificationThreshold };
        foreach (TradingEnsembleSeries item in selected)
        {
            foreach (TradingEnsemblePoint point in item.Points)
            {
                if (!visibleDates.Contains(point.Date.Date))
                    continue;
                ensembleValues.Add(point.EnsemblePercent);
                decileValues.AddRange(point.Deciles);
            }
            foreach (TradingForecastPoint forecast in item.Forecasts)
                if (visibleDates.Contains(forecast.Date.Date) && (!bossMode || forecast.IsNextBar))
                    ensembleValues.AddRange(forecast.ScenarioPercentages);
        }

        (double ensembleMinimum, double ensembleMaximum) = AutoRange(ensembleValues);
        (double decileMinimum, double decileMaximum) = AutoRange(decileValues);
        double EnsembleY(double value) => ensembleTop + ensembleHeight *
            (ensembleMaximum - Math.Clamp(value, ensembleMinimum, ensembleMaximum)) /
            (ensembleMaximum - ensembleMinimum);
        double DecileY(double value) => decileTop + decileHeight *
            (decileMaximum - Math.Clamp(value, decileMinimum, decileMaximum)) /
            (decileMaximum - decileMinimum);

        if (!_collapsed[1])
        {
            DrawScaleGrid(ensembleTop, ensembleHeight, ensembleMinimum, ensembleMaximum);
            DrawHorizontalThreshold(ensembleTop, ensembleHeight, ensembleMinimum, ensembleMaximum, BaseClassificationThreshold, Brushes.Green);
        }
        if (!_collapsed[2])
        {
            DrawScaleGrid(decileTop, decileHeight, decileMinimum, decileMaximum);
            DrawHorizontalThreshold(decileTop, decileHeight, decileMinimum, decileMaximum, BaseClassificationThreshold, Brushes.Green);
        }

        if (selected.Count > 0 && !_collapsed[1])
            DrawEnsembleForecastScale(selected, x, EnsembleY);

        foreach (TradingEnsembleSeries item in selected)
        {
            var byDate = item.Points.ToDictionary(point => point.Date.Date);
            Brush baseBrush = PackageBrush(item.PackageName);
            if (!_collapsed[1])
            {
                DrawLine(_dates.Select(d => byDate.TryGetValue(d, out TradingEnsemblePoint? p) ? p.EnsemblePercent : double.NaN).ToArray(),
                    x, EnsembleY, baseBrush, 1.8);
                DrawForecasts(item, x, EnsembleY, baseBrush, !bossMode);
            }
            for (int decile = 0; !_collapsed[2] && decile < 10; decile++)
            {
                int current = decile;
                Color color = DecileColors[decile];
                int packageIndex = Math.Max(0, Array.IndexOf(PackageOrder, item.PackageName));
                byte alpha = (byte)Math.Max(80, 225 - packageIndex * 25);
                var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
                DrawLine(_dates.Select(d => byDate.TryGetValue(d, out TradingEnsemblePoint? p) ? p.Deciles[current] : double.NaN).ToArray(),
                    x, DecileY, brush, decile is 0 or 9 ? 1.25 : .85);
            }
        }
    }

    private void DrawForecasts(TradingEnsembleSeries item, Func<int, double> x, Func<double, double> y,
        Brush ensembleBrush, bool showHistory)
    {
        if (item.Forecasts.Count == 0)
            return;
        Brush[] brushes =
        [
            Brushes.Red,
            new SolidColorBrush(Color.FromArgb(128, 255, 0, 0)),
            ensembleBrush,
            new SolidColorBrush(Color.FromArgb(128, 0, 128, 0)),
            Brushes.Green
        ];
        var dateIndexes = _dates.Select((date, index) => (date, index))
            .ToDictionary(item => item.date, item => item.index);
        List<TradingForecastPoint> visibleForecasts = item.Forecasts
            .Where(point => dateIndexes.ContainsKey(point.Date.Date))
            .Where(point => showHistory || point.IsNextBar)
            .OrderBy(point => point.Date)
            .ToList();

        if (showHistory)
        {
            foreach (int scenario in new[] { 0, 4 })
            {
                IEnumerable<(int Index, double Value)> path = visibleForecasts
                    .Where(point => point.ScenarioPercentages.Count > scenario &&
                                    double.IsFinite(point.ScenarioPercentages[scenario]))
                    .Select(point => (dateIndexes[point.Date.Date], point.ScenarioPercentages[scenario]));
                DrawDashedPath(path, x, y, brushes[scenario], 1.05);
            }
        }

        TradingEnsemblePoint? lastActual = item.Points
            .Where(point => dateIndexes.ContainsKey(point.Date.Date) && double.IsFinite(point.EnsemblePercent))
            .OrderBy(point => point.Date)
            .LastOrDefault();
        foreach (TradingForecastPoint future in visibleForecasts.Where(point => point.IsNextBar))
        {
            if (lastActual is not null)
            {
                int firstScenario = showHistory ? 2 : 0;
                int lastScenario = showHistory ? 2 : Math.Min(4, future.ScenarioPercentages.Count - 1);
                for (int scenario = firstScenario; scenario <= lastScenario; scenario++)
                {
                    if (!double.IsFinite(future.ScenarioPercentages[scenario]))
                        continue;
                    DrawDashedPath(
                    [
                        (dateIndexes[lastActual.Date.Date], lastActual.EnsemblePercent),
                        (dateIndexes[future.Date.Date], future.ScenarioPercentages[scenario])
                    ], x, y, brushes[scenario], 1.05);
                }
            }
        }

        foreach (TradingForecastPoint point in visibleForecasts)
        {
            int index = dateIndexes[point.Date.Date];
            for (int scenario = 0; scenario < Math.Min(5, point.ScenarioPercentages.Count); scenario++)
            {
                double value = point.ScenarioPercentages[scenario];
                if (!double.IsFinite(value))
                    continue;
                double centerX = x(index);
                double centerY = y(value);
                if (point.IsNextBar)
                {
                    const double size = 8.0;
                    var marker = new Rectangle
                    {
                        Width = size,
                        Height = size,
                        Fill = Brushes.Transparent,
                        Stroke = brushes[scenario],
                        StrokeThickness = 1.8,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(marker, centerX - size / 2.0);
                    Canvas.SetTop(marker, centerY - size / 2.0);
                    _canvas.Children.Add(marker);
                }
                else if (scenario is 1 or 3)
                {
                    const double size = 5.5;
                    var marker = new Ellipse
                    {
                        Width = size,
                        Height = size,
                        Fill = brushes[scenario],
                        Stroke = brushes[scenario],
                        StrokeThickness = 0.8,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(marker, centerX - size / 2.0);
                    Canvas.SetTop(marker, centerY - size / 2.0);
                    _canvas.Children.Add(marker);
                }
            }
        }
    }

    private void DrawEnsembleForecastScale(IReadOnlyList<TradingEnsembleSeries> selected,
        Func<int, double> x, Func<double, double> y)
    {
        if (_futureDateIndex < 0)
            return;
        List<TradingForecastPoint> futures = selected
            .SelectMany(item => item.Forecasts)
            .Where(point => point.IsNextBar && point.Date.Date == _dates[_futureDateIndex])
            .ToList();
        double[] scenarios = futures
            .SelectMany(point => point.ScenarioPercentages)
            .Where(double.IsFinite)
            .ToArray();
        double[] centers = futures
            .Where(point => point.ScenarioPercentages.Count > 2 &&
                            double.IsFinite(point.ScenarioPercentages[2]))
            .Select(point => point.ScenarioPercentages[2])
            .ToArray();
        if (scenarios.Length == 0 || centers.Length == 0)
            return;
        double lowerValue = scenarios.Min();
        double centerValue = centers.Average();
        double upperValue = scenarios.Max();

        double upper = y(upperValue);
        double center = y(centerValue);
        double lower = y(lowerValue);
        if (lower < upper)
            (upper, lower) = (lower, upper);
        double scaleHeight = lower - upper;
        if (scaleHeight < 2.0)
            return;
        double centerOffset = Math.Clamp((center - upper) / scaleHeight, 0.08, 0.92);
        double yellowStart = Math.Max(0.0, centerOffset - 0.07);
        double yellowEnd = Math.Min(1.0, centerOffset + 0.07);
        double markerWidth = ChartDrawingMetrics.CandleBodyWidth(
            _plotWidth / Math.Max(1.0, _slots)) * 2.0;
        var forecastScale = new Rectangle
        {
            Width = markerWidth,
            Height = scaleHeight,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0.0),
                EndPoint = new Point(0.5, 1.0),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromRgb(0, 165, 35), 0.0),
                    new(Color.FromRgb(255, 220, 20), yellowStart),
                    new(Color.FromRgb(255, 220, 20), yellowEnd),
                    new(Color.FromArgb(179, 255, 0, 0), 1.0)
                }
            },
            Stroke = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            StrokeThickness = .7,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(forecastScale, x(_futureDateIndex) - markerWidth / 2.0);
        Canvas.SetTop(forecastScale, upper);
        _canvas.Children.Add(forecastScale);
    }

    private readonly record struct ActiveSummary(
        TradingActivePosition? WithoutChandelier,
        TradingActivePosition? WithChandelier);

    private ActiveSummary? DrawActive(
        IReadOnlyList<TradingEnsembleSeries> selected, Func<int, double> x,
        double top, double height)
    {
        var signalValues = Enumerable.Repeat(double.NaN, _dates.Count).ToArray();
        var heldValues = Enumerable.Repeat(double.NaN, _dates.Count).ToArray();
        for (int index = 0; index < _dates.Count; index++)
        {
            if (_targetActivePositions.TryGetValue(_dates[index].Date,
                    out TradingActivePosition target))
                signalValues[index] = target.Value;
            if (_actualActivePositions.TryGetValue(_dates[index].Date,
                    out TradingActivePosition position))
                heldValues[index] = position.Value;
        }
        double[] finite = signalValues.Concat(heldValues).Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
            return null;
        double minimum = 0;
        double maximum;
        if (_activeMode != TradingActiveMode.IndependentDeciles)
        {
            maximum = 10;
        }
        else
        {
            maximum = Math.Max(10, selected.Count * 10);
        }
        DrawScaleGrid(top, height, minimum, maximum);
        double Y(double value) => top + height *
            (maximum - Math.Clamp(value, minimum, maximum)) / (maximum - minimum);
        DrawInteractiveActiveLine(signalValues, x, Y, Brushes.DarkOrange, 1.45,
            "Без люстры — оранжевая линия",
            "Позиция по сигналу ансамбля без защитной люстры и без задержки повторного входа. " +
            "Значение на дате D рассчитано после закрытия предыдущего торгового бара и относится " +
            "к открытию даты D.");
        DrawInteractiveActiveLine(heldValues, x, Y, Brushes.DarkBlue, 1.8,
            "С люстрой — синяя линия",
            "Фактически удерживаемая позиция с учётом защитной люстры 3,75%, частичных или " +
            "полных Stop-выходов и задержки повторного входа.");

        DateTime? labelDate = _lastVisibleRealDate;
        TradingActivePosition? signal = labelDate.HasValue &&
            _targetActivePositions.TryGetValue(labelDate.Value.Date, out TradingActivePosition exactSignal)
                ? exactSignal
                : null;
        TradingActivePosition? held = labelDate.HasValue &&
            _actualActivePositions.TryGetValue(labelDate.Value.Date, out TradingActivePosition exactHeld)
                ? exactHeld
                : null;
        return signal.HasValue || held.HasValue ? new ActiveSummary(signal, held) : null;
    }

    private string FormatActiveSummary(ActiveSummary summary)
    {
        if (!summary.WithChandelier.HasValue)
            return "Last: —";
        string value = _activeMode == TradingActiveMode.IndependentDeciles
            ? summary.WithChandelier.Value.Value.ToString("0", CultureInfo.InvariantCulture)
            : summary.WithChandelier.Value.Value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"Last: {value} ({summary.WithChandelier.Value.Percentage:0.0}%)";
    }

    private string ActivePaneTitle() => _activeMode switch
    {
        TradingActiveMode.ProportionalEnsembles => "Active Ensembles",
        TradingActiveMode.QuantumEnsembles => "Active Ensembles 0/10",
        _ => "Active Deciles"
    };

    private void DrawInteractiveActiveLine(
        IReadOnlyList<double> values,
        Func<int, double> x,
        Func<double, double> y,
        Brush brush,
        double thickness,
        string title,
        string explanation)
    {
        Polyline? hitLine = null;
        Polyline? visibleLine = null;
        for (int index = 0; index < values.Count; index++)
        {
            if (!double.IsFinite(values[index]))
            {
                hitLine = null;
                visibleLine = null;
                continue;
            }
            if (hitLine is null || visibleLine is null)
            {
                hitLine = new Polyline
                {
                    Stroke = Brushes.Transparent,
                    StrokeThickness = 12.0,
                    IsHitTestVisible = true
                };
                System.Windows.Automation.AutomationProperties.SetName(
                    hitLine, title + " — подсказка линии");
                ConfigureActiveLineHint(hitLine, title, explanation);
                _canvas.Children.Add(hitLine);

                visibleLine = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = thickness,
                    IsHitTestVisible = false
                };
                _canvas.Children.Add(visibleLine);
            }
            Point point = new(x(index), y(values[index]));
            hitLine.Points.Add(point);
            visibleLine.Points.Add(point);
        }
    }

    private void ConfigureActiveLineHint(
        FrameworkElement owner,
        string title,
        string explanation)
    {
        string text = $"{title}\n{explanation}";
        owner.Tag = text;
        owner.MouseEnter += (_, e) =>
            BeginActiveLineHint(owner, text, e.GetPosition(_canvas));
        owner.MouseMove += (_, e) =>
        {
            if (ReferenceEquals(_activeLineHintOwner, owner) && !_activeLineHintPopup.IsOpen)
                _activeLineHintPoint = e.GetPosition(_canvas);
        };
        owner.MouseLeave += (_, _) => CloseActiveLineHint(owner);
    }

    private void BeginActiveLineHint(
        FrameworkElement owner,
        string text,
        Point point)
    {
        CloseActiveLineHint();
        _activeLineHintOwner = owner;
        _activeLineHintText.Text = text;
        _activeLineHintPoint = point;
        _activeLineHintTimer.Start();
    }

    private void OpenActiveLineHint()
    {
        _activeLineHintTimer.Stop();
        if (_activeLineHintOwner?.IsMouseOver != true)
        {
            CloseActiveLineHint();
            return;
        }
        _activeLineHintPopup.HorizontalOffset = _activeLineHintPoint.X > ActualWidth - 410
            ? Math.Max(0, _activeLineHintPoint.X - 395)
            : _activeLineHintPoint.X + 14;
        _activeLineHintPopup.VerticalOffset = _activeLineHintPoint.Y > ActualHeight - 135
            ? Math.Max(0, _activeLineHintPoint.Y - 115)
            : _activeLineHintPoint.Y + 14;
        _activeLineHintPopup.IsOpen = true;
    }

    private void CloseActiveLineHint(FrameworkElement? owner = null)
    {
        if (owner is not null && !ReferenceEquals(owner, _activeLineHintOwner))
            return;
        _activeLineHintTimer.Stop();
        _activeLineHintPopup.IsOpen = false;
        _activeLineHintOwner = null;
    }

    private void DrawConstantScaleGrid(double top, double height, double value)
    {
        for (int tick = 1; tick <= 3; tick++)
        {
            double y = top + height * tick / 4.0;
            _canvas.Children.Add(new Line
            {
                X1 = _plotLeft,
                X2 = _plotLeft + _plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(221, 216, 205)),
                StrokeThickness = .8,
                IsHitTestVisible = false
            });
        }
        DrawScaleLabels(value.ToString("0", CultureInfo.InvariantCulture), top + height / 2.0 - 7);
        double rightAxisX = _plotLeft + _plotWidth;
        _canvas.Children.Add(new Line
        {
            X1 = rightAxisX,
            X2 = rightAxisX,
            Y1 = top,
            Y2 = top + height,
            Stroke = DarkAxisBrush,
            StrokeThickness = .9,
            IsHitTestVisible = false
        });
    }

    private void DrawPanelFrame(double left, double width, double top, double height)
    {
        _canvas.Children.Add(new Rectangle
        {
            Width = width,
            Height = height,
            Fill = PanelBackgroundBrush,
            Stroke = PanelBorderBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false
        });
        Canvas.SetLeft(_canvas.Children[^1], left); Canvas.SetTop(_canvas.Children[^1], top);
    }

    private void DrawPanelTitle(double top, string title, int index)
    {
        var button = new Button
        {
            Content = _collapsed[index] ? "▲" : "▼", Width = 18, Height = 18, FontSize = 10,
            Padding = new Thickness(0), Background = Brushes.White, BorderBrush = PanelBorderBrush,
            ToolTip = (_collapsed[index] ? "Развернуть " : "Свернуть ") + title
        };
        System.Windows.Automation.AutomationProperties.SetName(button, title + " — " + (_collapsed[index] ? "развернуть" : "свернуть"));
        button.MouseMove += (_, e) => e.Handled = true;
        button.Click += (_, e) =>
        {
            _collapsed[index] = !_collapsed[index];
            RenderSeries(); PaneLayoutChanged?.Invoke(); e.Handled = true;
        };
        Canvas.SetLeft(button, PanelInset + 5); Canvas.SetTop(button, top + 4); _canvas.Children.Add(button);
        AddText(title, PanelInset + 29, top + 5, 12, TitleBrush, FontWeights.Normal);
    }

    private void DrawPaneDividers(double left, double width)
    {
        for (int index = 0; index < _dividerYs.Length; index++)
        {
            double y = _dividerYs[index];
            if (!double.IsFinite(y)) continue;
            var divider = new Rectangle
            {
                Width = width,
                Height = 5,
                Fill = DividerBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(divider, left);
            Canvas.SetTop(divider, y - 2.5);
            System.Windows.Automation.AutomationProperties.SetName(
                divider, $"Trading pane divider {index + 1}");
            _canvas.Children.Add(divider);
        }
    }

    private void DrawScaleGrid(double top, double height, double minimum, double maximum,
        bool dashedMiddle = false, Brush? middleBrush = null)
    {
        for (int tick = 0; tick <= 4; tick++)
        {
            double fraction = tick / 4.0;
            double y = top + height * fraction;
            double value = maximum - (maximum - minimum) * fraction;
            if (tick is > 0 and < 4)
            {
                bool middle = tick == 2 && dashedMiddle;
                _canvas.Children.Add(new Line
                {
                    X1 = _plotLeft,
                    X2 = _plotLeft + _plotWidth,
                    Y1 = y,
                    Y2 = y,
                    Stroke = middle ? middleBrush ?? Brushes.Gray : new SolidColorBrush(Color.FromRgb(221, 216, 205)),
                    StrokeThickness = middle ? .9 : .8,
                    StrokeDashArray = middle ? new DoubleCollection { 4, 4 } : null,
                    IsHitTestVisible = false
                });
            }
            string label = value.ToString("0", CultureInfo.InvariantCulture);
            DrawScaleLabels(label, y - 7);
        }
        double rightAxisX = _plotLeft + _plotWidth;
        _canvas.Children.Add(new Line
        {
            X1 = rightAxisX,
            X2 = rightAxisX,
            Y1 = top,
            Y2 = top + height,
            Stroke = DarkAxisBrush,
            StrokeThickness = 0.9,
            IsHitTestVisible = false
        });
    }

    private double? LatestVisibleClose()
    {
        if (_prices is null)
            return null;
        Dictionary<DateTime, PricePoint> byDate = _prices.Points
            .ToDictionary(point => point.Date.Date);
        for (int index = _dates.Count - 1; index >= 0; index--)
            if (byDate.TryGetValue(_dates[index], out PricePoint? point) && double.IsFinite(point.Close))
                return point.Close;
        return null;
    }

    private double? LatestVisibleStop()
    {
        for (int index = _dates.Count - 1; index >= 0; index--)
        {
            if (!TryGetMaximumStop(_dates[index], out double stop))
                continue;
            return stop;
        }
        return null;
    }

    private bool TryGetMaximumStop(DateTime date, out double maximum)
    {
        maximum = double.NaN;
        if (!_chandelierStops.TryGetValue(date.Date, out IReadOnlyList<double>? stops))
            return false;
        foreach (double stop in stops)
            if (double.IsFinite(stop) && (!double.IsFinite(maximum) || stop > maximum))
                maximum = stop;
        return double.IsFinite(maximum);
    }

    private bool TryGetMaximumExecution(DateTime date, out double maximum)
    {
        maximum = double.NaN;
        if (!_chandelierExecutions.TryGetValue(date.Date, out IReadOnlyList<double>? executions))
            return false;
        foreach (double execution in executions)
            if (double.IsFinite(execution) && (!double.IsFinite(maximum) || execution > maximum))
                maximum = execution;
        return double.IsFinite(maximum);
    }

    private static (double Minimum, double Maximum) AutoRange(IEnumerable<double> values)
    {
        double[] finite = values.Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
            return (0.0, 100.0);
        double minimum = finite.Min();
        double maximum = finite.Max();
        double padding = maximum > minimum
            ? (maximum - minimum) * 0.05
            : Math.Max(Math.Abs(minimum) * 0.05, 1.0);
        return (minimum - padding, maximum + padding);
    }

    private void DrawHorizontalThreshold(double top, double height, double minimum, double maximum,
        double value, Brush brush)
    {
        if (value < minimum || value > maximum)
            return;
        double y = top + height * (maximum - value) / (maximum - minimum);
        _canvas.Children.Add(new Line
        {
            X1 = _plotLeft,
            X2 = _plotLeft + _plotWidth,
            Y1 = y,
            Y2 = y,
            Stroke = brush,
            StrokeThickness = .9,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            IsHitTestVisible = false
        });
    }

    private void DrawAutoLine(IReadOnlyList<double> values, Func<int,double> x, double top, double height, Brush brush, double thickness)
    {
        double[] finite = values.Where(double.IsFinite).ToArray(); if (finite.Length < 2) return;
        double min = finite.Min(), max = finite.Max(); double pad = Math.Max((max - min) * .05, 1e-6); min -= pad; max += pad;
        DrawLine(values, x, value => top + height * (max - value) / (max - min), brush, thickness);
    }

    private void DrawLine(IReadOnlyList<double> values, Func<int,double> x, Func<double,double> y, Brush brush, double thickness)
    {
        Polyline? line = null;
        for (int index = 0; index < values.Count; index++)
        {
            if (!double.IsFinite(values[index])) { line = null; continue; }
            if (line is null) { line = new Polyline { Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false }; _canvas.Children.Add(line); }
            line.Points.Add(new Point(x(index), y(values[index])));
        }
    }

    private void DrawDashedPath(IEnumerable<(int Index, double Value)> values,
        Func<int, double> x, Func<double, double> y, Brush brush, double thickness)
    {
        var line = new Polyline
        {
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false
        };
        foreach ((int index, double value) in values)
            if (double.IsFinite(value))
                line.Points.Add(new Point(x(index), y(value)));
        if (line.Points.Count >= 2)
            _canvas.Children.Add(line);
    }

    private IEnumerable<TradingEnsembleSeries> SelectedSeries()
    {
        foreach (string key in PackageOrder)
            if (_selectedPackages.Contains(key) && _series.TryGetValue(key, out TradingEnsembleSeries? item))
                yield return item;
    }

    internal static Brush PackageBrush(string package) => package switch
    {
        "GPU-ZZ1" => new SolidColorBrush(Color.FromRgb(32, 116, 75)),
        "GPU-ZZ2" => new SolidColorBrush(Color.FromRgb(190, 62, 116)),
        "GPU-ZZ3" => new SolidColorBrush(Color.FromRgb(100, 100, 100)),
        "Torch-ZZ1" => new SolidColorBrush(Color.FromRgb(28, 92, 190)),
        "Torch-ZZ2" => new SolidColorBrush(Color.FromRgb(210, 52, 45)),
        "Torch-ZZ1-Jordan" => new SolidColorBrush(Color.FromRgb(126, 55, 170)),
        "Torch-ZZ2-Jordan" => new SolidColorBrush(Color.FromRgb(0, 135, 120)),
        "Encog-ZZ1" => new SolidColorBrush(Color.FromRgb(230, 126, 34)),
        _ => new SolidColorBrush(Color.FromRgb(95, 75, 55))
    };

    private static readonly string[] PackageOrder =
    [
        "GPU-ZZ1", "GPU-ZZ2", "GPU-ZZ3",
        "Torch-ZZ1", "Torch-ZZ2", "Torch-ZZ1-Jordan", "Torch-ZZ2-Jordan",
        "Encog-ZZ1", "Encog-ZZ2"
    ];

    private static readonly Color[] DecileColors =
    [
        Colors.Red, Colors.OrangeRed, Colors.Orange, Colors.Gold, Colors.YellowGreen,
        Colors.GreenYellow, Colors.LimeGreen, Colors.Green, Colors.DarkGreen, Colors.Blue
    ];

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizeDivider < 0 && e.LeftButton != MouseButtonState.Pressed)
            Cursor = IsOverPaneDivider(e.GetPosition(this)) ? Cursors.SizeNS : null;
        if (_resizeDivider >= 0)
            return;
        if (_cursorPinned || _dates.Count == 0 || _plotWidth <= 0) return;
        double relative = Math.Clamp((e.GetPosition(_canvas).X - _plotLeft) / _plotWidth, 0, 1);
        double coordinate = relative * _slots;
        int index = Enumerable.Range(0, _dates.Count)
            .MinBy(i => Math.Abs(DataCoordinate(i) - coordinate));
        CursorDateChanged?.Invoke(_dates[index]);
    }

    private void DrawCursor()
    {
        if (_cursorLine is not null) _canvas.Children.Remove(_cursorLine); _cursorLine = null;
        if (!_cursorDate.HasValue || _dates.Count == 0 || _plotWidth <= 0 || _plotHeight <= 0) return;
        int index = Enumerable.Range(0, _dates.Count).MinBy(i => Math.Abs((_dates[i] - _cursorDate.Value).Ticks));
        double x = _plotLeft + _plotWidth * DataCoordinate(index) / _slots;
        _cursorLine = new Line { X1=x, X2=x, Y1=_plotTop, Y2=_plotTop+_plotHeight,
            Stroke=ChartCursorStyle.Stroke(_cursorPinned, Brushes.Black), StrokeThickness=ChartCursorStyle.Thickness(_cursorPinned, 1.1),
            Opacity=ChartCursorStyle.Opacity(_cursorPinned, .85), IsHitTestVisible=false };
        Panel.SetZIndex(_cursorLine, 1000); _canvas.Children.Add(_cursorLine);
    }

    private double DataCoordinate(int index) =>
        index + (_futureDateIndex >= 0 && index >= _futureDateIndex ? FutureGapSlots : 0);

    private void DrawScaleLabels(string text, double y)
    {
        AddText(text, Math.Max(2, _plotLeft - 7 - text.Length * 6), y, 10, DarkAxisBrush, FontWeights.Normal);
        AddText(text, _plotLeft + _plotWidth + 5, y, 10, DarkAxisBrush, FontWeights.Normal);
    }
    private void AddText(string text, double x, double y, double size, Brush brush, FontWeight weight)
    { var block = new TextBlock { Text=text, FontSize=size, Foreground=brush, FontWeight=weight }; Canvas.SetLeft(block,x); Canvas.SetTop(block,y); _canvas.Children.Add(block); }
}
