using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal sealed class TradingEnsembleChart : Border
{
    private const int RightPaddingSlots = 2;
    private const int FutureGapSlots = 1;
    private const double PanelGap = 11.0;
    private const double PanelInset = 4.0;
    private const double PanelHeaderHeight = TradingPaneLayout.HeaderHeight;
    private const double PanelBottomInset = 8.0;
    private const double BaseClassificationThreshold = 50.0;
    private const int TotalEnsembleCount = 9;
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
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private DateTime? _cursorDate;
    private IReadOnlyList<DateTime> _dates = [];
    private Line? _cursorLine;
    private bool _cursorPinned;
    private HashSet<string> _selectedPackages = new(StringComparer.OrdinalIgnoreCase);
    private bool _showEma;
    private bool _showActiveEnsembles;
    private bool _showPrice = true, _showCandles = true;
    private double _plotLeft, _plotTop, _plotWidth, _plotHeight, _slots = 1.0;
    private int _futureDateIndex = -1;
    private readonly double[] _dividerYs = new double[3];
    private double _priceBoundary = .52, _ensembleBoundary = .63, _decileBoundary = .91;
    private int _resizeDivider = -1;
    private double _lastResizeY;
    private double _panelUsableHeight = 1.0;
    private bool[] _collapsed = new bool[4];
    private double[] _paneHeights = new double[4];
    private string? _message;

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
        SizeChanged += (_, _) => RenderSeries();
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(RenderSeries)); };
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
    public void SetShowActiveEnsembles(bool show)
    {
        _showActiveEnsembles = show;
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
        _dates.Count > 1 && _dividerYs.Any(y => Math.Abs(point.Y - y) <= 7.0);

    public bool TryBeginPaneResize(Point point)
    {
        if (_dates.Count < 2 || _paneHeights.Any(h => h <= 0) || _dividerYs.All(double.IsNaN)) return false;
        int divider = Enumerable.Range(0, _dividerYs.Length)
            .Where(index => double.IsFinite(_dividerYs[index]))
            .OrderBy(index => Math.Abs(point.Y - _dividerYs[index]))
            .First();
        if (Math.Abs(point.Y - _dividerYs[divider]) > 7.0)
            return false;
        _resizeDivider = divider;
        _lastResizeY = point.Y;
        Cursor = Cursors.SizeNS;
        return true;
    }

    public void ResizePane(Point point)
    {
        if (_resizeDivider < 0)
            return;
        double[] weights = [_priceBoundary, _ensembleBoundary - _priceBoundary,
            _decileBoundary - _ensembleBoundary, 1 - _decileBoundary];
        int i = _resizeDivider;
        double pairHeight = _paneHeights[i] + _paneHeights[i + 1];
        double minimum = Math.Min(TradingPaneLayout.MinimumExpandedHeight, pairHeight / 2);
        double first = Math.Clamp(_paneHeights[i] + point.Y - _lastResizeY, minimum, pairHeight - minimum);
        double pairWeight = weights[i] + weights[i + 1];
        weights[i] = pairWeight * first / pairHeight;
        weights[i + 1] = pairWeight - weights[i];
        _priceBoundary = weights[0];
        _ensembleBoundary = weights[0] + weights[1];
        _decileBoundary = 1 - weights[3];
        _lastResizeY = point.Y;
        RenderSeries();
    }

    public void EndPaneResize()
    {
        _resizeDivider = -1;
        Cursor = null;
    }

    private void RenderSeries()
    {
        if (ActualWidth < 150 || ActualHeight < 180) return;
        _canvas.Children.Clear();
        _cursorLine = null;

        var selected = SelectedSeries().ToList();
        IEnumerable<DateTime> visibleDates = selected.SelectMany(item => item.Points.Select(point => point.Date))
            .Where(date => !_viewportStart.HasValue || date >= _viewportStart.Value)
            .Where(date => !_viewportEnd.HasValue || date <= _viewportEnd.Value);
        DateTime? lastRealDate = selected.SelectMany(item => item.Points.Select(point => point.Date))
            .Cast<DateTime?>().Max();
        bool latestBarVisible = lastRealDate.HasValue &&
            (!_viewportStart.HasValue || lastRealDate.Value >= _viewportStart.Value) &&
            (!_viewportEnd.HasValue || lastRealDate.Value <= _viewportEnd.Value);
        if (latestBarVisible)
            visibleDates = visibleDates.Concat(selected.SelectMany(item => item.Forecasts)
                .Where(point => point.IsNextBar)
                .Select(point => point.Date));
        _dates = visibleDates.Distinct().OrderBy(date => date).ToList();
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
        _dividerYs[0] = !_collapsed[0] && !_collapsed[1] ? priceTop + priceHeight + gap / 2.0 : double.NaN;
        _dividerYs[1] = !_collapsed[1] && !_collapsed[2] ? ensembleTop + ensembleHeight + gap / 2.0 : double.NaN;
        _dividerYs[2] = !_collapsed[2] && !_collapsed[3] ? decileTop + decileHeight + gap / 2.0 : double.NaN;
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
        DrawPaneDividers(PanelInset, panelWidth);

        if (!_collapsed[0]) DrawPrice(X, pricePlotTop, pricePlotHeight);
        if (!_collapsed[1] || !_collapsed[2]) DrawPercentPanels(selected, X, ensemblePlotTop, ensemblePlotHeight, decilePlotTop, decilePlotHeight);
        (double Value, double Percentage)? lastActive = _collapsed[3]
            ? null
            : DrawActive(selected, X, activePlotTop, activePlotHeight);
        DrawPanelTitle(priceTop, "S&P 500 (SPY)", 0);
        DrawPanelTitle(ensembleTop, "Ensemble %", 1);
        DrawPanelTitle(decileTop, "Deciles, %", 2);
        DrawPanelTitle(activeTop, _showActiveEnsembles ? "Active Ensembles" : "Active Deciles", 3);
        double? lastClose = LatestVisibleClose();
        if (lastClose.HasValue)
            AddText($"Last: {lastClose.Value:N2}", Math.Max(left + 120, ActualWidth - 155),
                priceTop + 7, 11, LastValueBrush, FontWeights.Normal);
        if (lastActive.HasValue)
            AddText($"Last: {lastActive.Value.Value:0} ({lastActive.Value.Percentage:0.0}%)",
                Math.Max(left + 120, ActualWidth - 190),
                activeTop + 7, 11, LastValueBrush, FontWeights.Normal);
        if (_collapsed.All(c => c)) _plotHeight = 0;
        DrawCursor();
    }

    private void DrawPrice(Func<int, double> x, double top, double height)
    {
        if (_prices is null || !_showPrice) return;
        var byDate = _prices.Points.ToDictionary(point => point.Date.Date);
        int futureIndex = -1;
        PricePoint? referencePoint = null;
        for (int index = _dates.Count - 1; index >= 0; index--)
        {
            if (!byDate.TryGetValue(_dates[index], out PricePoint? point))
                continue;
            referencePoint = point;
            if (index < _dates.Count - 1 && _dates[^1] > point.Date.Date)
                futureIndex = _dates.Count - 1;
            break;
        }
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
        double[] finite = priceExtremes.Concat(lines.SelectMany(item => item.Values)).Where(double.IsFinite).ToArray();
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

    private (double Value, double Percentage)? DrawActive(
        IReadOnlyList<TradingEnsembleSeries> selected, Func<int, double> x,
        double top, double height)
    {
        var maps = selected.Select(item => item.Points.ToDictionary(point => point.Date.Date)).ToList();
        var values = Enumerable.Repeat(double.NaN, _dates.Count).ToArray();
        var percentages = Enumerable.Repeat(double.NaN, _dates.Count).ToArray();
        for (int index = 0; index < _dates.Count; index++)
        {
            double active = 0.0;
            int availablePackages = 0;
            foreach (var map in maps)
                if (map.TryGetValue(_dates[index], out TradingEnsemblePoint? point))
                {
                    active += _showActiveEnsembles
                        ? point.EnsemblePercent >= BaseClassificationThreshold ? 1 : 0
                        : point.Deciles.Count(value => value >= BaseClassificationThreshold);
                    availablePackages++;
                }
            if (availablePackages > 0)
            {
                values[index] = active;
                int denominator = _showActiveEnsembles
                    ? TotalEnsembleCount
                    : availablePackages * 10;
                percentages[index] = denominator > 0 ? active * 100.0 / denominator : double.NaN;
            }
        }
        double[] finite = values.Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
            return null;
        double minimum;
        double maximum;
        if (_showActiveEnsembles)
        {
            minimum = 0;
            maximum = TotalEnsembleCount;
            DrawScaleGrid(top, height, minimum, maximum);
        }
        else
        {
            double dataMinimum = finite.Min();
            double dataMaximum = finite.Max();
            bool constant = Math.Abs(dataMaximum - dataMinimum) < 1e-9;
            minimum = constant ? dataMinimum - .5 : dataMinimum;
            maximum = constant ? dataMaximum + .5 : dataMaximum;
            if (constant)
                DrawConstantScaleGrid(top, height, dataMinimum);
            else
                DrawScaleGrid(top, height, minimum, maximum);
        }
        DrawLine(values, x, value => top + height *
            (maximum - Math.Clamp(value, minimum, maximum)) / (maximum - minimum), Brushes.DarkBlue, 1.7);
        for (int index = values.Length - 1; index >= 0; index--)
            if (double.IsFinite(values[index]) && double.IsFinite(percentages[index]))
                return (values[index], percentages[index]);
        return null;
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
            Content = _collapsed[index] ? "▶" : "▼", Width = 18, Height = 18, FontSize = 10,
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
        foreach (double y in _dividerYs)
        {
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

    private static Brush PackageBrush(string package) => package switch
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
