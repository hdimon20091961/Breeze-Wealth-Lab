using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal enum SentimentBreadth { Sp50, Sp100, Sp200, Nasdaq100 }

internal sealed class SentimentChart : Border
{
    private sealed record BreadthLayer(IReadOnlyDictionary<DateTime, double> Values, Color Color);
    private const int RightPaddingSlots = 2;
    private readonly Canvas _canvas = new();
    private readonly HashSet<SentimentIndicator> _selected = [SentimentIndicator.CnnFearGreed];
    private readonly HashSet<SentimentBreadth> _selectedBreadths = [];
    private readonly Dictionary<SentimentBreadth, BreadthLayer> _breadthLayers = new();
    private SentimentSeries? _series;
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private DateTime? _cursorDate;
    private IReadOnlyList<SentimentPoint> _renderedPoints = [];
    private Line? _cursorLine;
    private bool _cursorPinned;
    private double _plotLeft;
    private double _plotTop;
    private double _plotWidth;
    private double _plotHeight;
    private double _horizontalSlots = 1.0;

    public event Action<DateTime>? CursorDateChanged;

    public SentimentChart()
    {
        Background = new SolidColorBrush(Color.FromRgb(255, 252, 244));
        BorderBrush = new SolidColorBrush(Color.FromRgb(205, 195, 171));
        BorderThickness = new Thickness(1);
        Margin = new Thickness(4);
        Padding = new Thickness(2);
        Child = _canvas;
        _canvas.ClipToBounds = true;
        _canvas.SizeChanged += (_, _) => RenderSeries();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(new Action(RenderSeries));
        };
        MouseMove += ChartMouseMove;
    }

    public void SetSeries(SentimentSeries? series)
    {
        _series = series;
        if (IsVisible)
            RenderSeries();
    }

    public void SetSelectedIndicators(IEnumerable<SentimentIndicator> indicators)
    {
        _selected.Clear();
        foreach (SentimentIndicator indicator in indicators)
            _selected.Add(indicator);
        if (IsVisible)
            RenderSeries();
    }

    public void SetBreadthSeries(BreadthSeries sp50, BreadthSeries sp100, BreadthSeries sp200, BreadthSeries nasdaq100)
    {
        _breadthLayers.Clear();
        void Add(SentimentBreadth key, BreadthSeries series, Color color) =>
            _breadthLayers.Add(key, new BreadthLayer(
                series.Points.ToDictionary(point => point.Date.Date, point => point.Value), color));
        Add(SentimentBreadth.Sp50, sp50, Color.FromRgb(25, 118, 210));
        Add(SentimentBreadth.Sp100, sp100, Color.FromRgb(46, 125, 50));
        Add(SentimentBreadth.Sp200, sp200, Color.FromRgb(142, 68, 173));
        Add(SentimentBreadth.Nasdaq100, nasdaq100, Color.FromRgb(230, 126, 34));
        if (IsVisible) RenderSeries();
    }

    public void SetSelection(IEnumerable<SentimentIndicator> indicators, IEnumerable<SentimentBreadth> breadths)
    {
        _selected.Clear();
        _selected.UnionWith(indicators);
        _selectedBreadths.Clear();
        _selectedBreadths.UnionWith(breadths);
        if (IsVisible) RenderSeries();
    }

    public void SetViewport(DateTime start, DateTime end)
    {
        _viewportStart = start;
        _viewportEnd = end;
        if (IsVisible)
            RenderSeries();
    }

    public void SetCursorDate(DateTime? date)
    {
        _cursorDate = date;
        if (IsVisible)
            DrawCursorLine();
    }

    public void SetCursorPinned(bool pinned)
    {
        if (_cursorPinned == pinned)
            return;
        _cursorPinned = pinned;
        if (IsVisible)
            DrawCursorLine();
    }

    public void SetMessage(string message)
    {
        _series = null;
        _renderedPoints = [];
        _canvas.Children.Clear();
        AddText(message, 10, 10, 12, new SolidColorBrush(Color.FromRgb(95, 97, 100)), FontWeights.Normal);
    }

    public void Refresh() => RenderSeries();

    public static Color ColorFor(SentimentIndicator indicator) => indicator switch
    {
        SentimentIndicator.CnnFearGreed => Color.FromRgb(35, 92, 158),
        SentimentIndicator.RiskMeasure => Color.FromRgb(196, 54, 54),
        SentimentIndicator.PanicEuphoria => Color.FromRgb(126, 75, 174),
        SentimentIndicator.Arsi => Color.FromRgb(23, 137, 126),
        SentimentIndicator.MyRsi => Color.FromRgb(45, 78, 112),
        SentimentIndicator.MyRsiFast => Color.FromRgb(198, 88, 24),
        SentimentIndicator.TrendScore => Color.FromRgb(214, 126, 22),
        SentimentIndicator.AroonUp => Color.FromRgb(41, 134, 78),
        SentimentIndicator.AroonDown => Color.FromRgb(190, 62, 70),
        SentimentIndicator.DiMinus => Color.FromRgb(156, 70, 117),
        SentimentIndicator.DiPlus => Color.FromRgb(28, 132, 153),
        SentimentIndicator.CorrelationAngle => Color.FromRgb(104, 83, 165),
        SentimentIndicator.CorrelationTrend => Color.FromRgb(142, 122, 33),
        SentimentIndicator.Hurst => Color.FromRgb(98, 106, 116),
        SentimentIndicator.Cmf => Color.FromRgb(34, 139, 69),
        SentimentIndicator.Decibel => Color.FromRgb(179, 66, 138),
        SentimentIndicator.Jocker => Color.FromRgb(122, 88, 44),
        SentimentIndicator.MarketBreadth => Color.FromRgb(43, 123, 146),
        SentimentIndicator.DumbMoney => Color.FromRgb(194, 102, 28),
        SentimentIndicator.SmartMoney => Color.FromRgb(27, 126, 65),
        _ => Colors.Black
    };

    private void RenderSeries()
    {
        _canvas.Children.Clear();
        _cursorLine = null;
        if (_series is null || _canvas.ActualWidth < 100 || _canvas.ActualHeight < 24)
            return;
        _renderedPoints = _series.Points
            .Where(point => !_viewportStart.HasValue || point.Date >= _viewportStart.Value)
            .Where(point => !_viewportEnd.HasValue || point.Date <= _viewportEnd.Value)
            .ToList();
        if (_renderedPoints.Count < 2)
            return;

        const double left = 50;
        const double top = 10;
        const double right = 50;
        const double bottom = 9;
        double width = Math.Max(1.0, _canvas.ActualWidth - left - right);
        double height = Math.Max(1.0, _canvas.ActualHeight - top - bottom);
        double rightAxisX = left + width;
        double slots = Math.Max(1.0, _renderedPoints.Count - 1 + RightPaddingSlots);
        double X(int index) => left + width * index / slots;
        double[] visibleValues = SentimentIndicatorCatalog.All
            .Where(_selected.Contains)
            .SelectMany(indicator => _renderedPoints.Select(point => point.Value(indicator)))
            .Where(double.IsFinite)
            .Select(value => Math.Clamp(value, -1.0, 1.0))
            .ToArray();
        double scaleMinimum = visibleValues.Length == 0 ? -1.0 : visibleValues.Min();
        double scaleMaximum = visibleValues.Length == 0 ? 1.0 : visibleValues.Max();
        if (scaleMaximum - scaleMinimum < 0.000001)
        {
            double center = scaleMinimum;
            scaleMinimum = Math.Max(-1.0, center - 0.1);
            scaleMaximum = Math.Min(1.0, center + 0.1);
            if (scaleMaximum - scaleMinimum < 0.000001)
            {
                scaleMinimum = -1.0;
                scaleMaximum = 1.0;
            }
        }
        double Y(double value) => top + height *
            (1.0 - (Math.Clamp(value, scaleMinimum, scaleMaximum) - scaleMinimum) /
                (scaleMaximum - scaleMinimum));

        bool hasIndicators = _selected.Count > 0;
        bool hasBreadths = _selectedBreadths.Count > 0;
        double[] breadthValues = _selectedBreadths
            .Where(_breadthLayers.ContainsKey)
            .SelectMany(key => _renderedPoints.Select(point =>
                _breadthLayers[key].Values.TryGetValue(point.Date.Date, out double value) ? value : double.NaN))
            .Where(double.IsFinite).ToArray();
        double breadthMinimum = 0.0, breadthMaximum = 100.0;
        if (breadthValues.Length > 0)
        {
            double minimum = breadthValues.Min(), maximum = breadthValues.Max();
            double padding = Math.Max(1.0, (maximum - minimum) * 0.05);
            breadthMinimum = Math.Max(0.0, minimum - padding);
            breadthMaximum = Math.Min(100.0, maximum + padding);
        }
        double BreadthY(double value) => top + height *
            (1.0 - (value - breadthMinimum) / (breadthMaximum - breadthMinimum));

        Brush gridBrush = new SolidColorBrush(Color.FromRgb(224, 216, 196));
        Brush zeroBrush = new SolidColorBrush(Color.FromRgb(145, 138, 124));
        Brush axisBrush = new SolidColorBrush(Color.FromRgb(90, 92, 95));
        for (int tick = 0; tick < 5; tick++)
        {
            double level = scaleMaximum - (scaleMaximum - scaleMinimum) * tick / 4.0;
            double breadthLevel = breadthMaximum - (breadthMaximum - breadthMinimum) * tick / 4.0;
            double y = top + height * tick / 4.0;
            _canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = rightAxisX,
                Y1 = y,
                Y2 = y,
                Stroke = hasIndicators && Math.Abs(level) < 0.000001 ? zeroBrush : gridBrush,
                StrokeThickness = hasIndicators && Math.Abs(level) < 0.000001 ? 1.0 : 0.65,
                StrokeDashArray = tick is 1 or 3 ? new DoubleCollection { 3, 3 } : null,
                IsHitTestVisible = false
            });
            string label = level.ToString("0.###", CultureInfo.InvariantCulture);
            if (hasIndicators || !hasBreadths)
                AddText(label, 10, y - 7, 9, axisBrush, FontWeights.Normal);
            if (hasBreadths)
                AddText(breadthLevel.ToString("0.#", CultureInfo.InvariantCulture) + "%",
                    rightAxisX + 5, y - 7, 9, axisBrush, FontWeights.Normal);
            else
                AddText(label, rightAxisX + 5, y - 7, 9, axisBrush, FontWeights.Normal);
        }

        if (hasIndicators && scaleMinimum < 0.0 && scaleMaximum > 0.0)
        {
            double zeroY = Y(0.0);
            _canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = rightAxisX,
                Y1 = zeroY,
                Y2 = zeroY,
                Stroke = zeroBrush,
                StrokeThickness = 1.0,
                IsHitTestVisible = false
            });
        }

        _canvas.Children.Add(new Line
        {
            X1 = rightAxisX,
            X2 = rightAxisX,
            Y1 = top,
            Y2 = top + height,
            Stroke = Brushes.Black,
            StrokeThickness = 0.9,
            IsHitTestVisible = false
        });

        if (hasIndicators)
            _canvas.Children.Add(new Line
            {
                X1 = left, X2 = left, Y1 = top, Y2 = top + height,
                Stroke = Brushes.Black, StrokeThickness = 0.9, IsHitTestVisible = false
            });

        foreach (SentimentBreadth key in _selectedBreadths)
        {
            if (!_breadthLayers.TryGetValue(key, out BreadthLayer? layer)) continue;
            Polyline? line = null;
            for (int index = 0; index < _renderedPoints.Count; index++)
            {
                if (!layer.Values.TryGetValue(_renderedPoints[index].Date.Date, out double value) || !double.IsFinite(value))
                {
                    line = null;
                    continue;
                }
                if (line is null)
                {
                    line = new Polyline
                    {
                        Stroke = new SolidColorBrush(layer.Color), StrokeThickness = 1.6,
                        Opacity = 0.9, IsHitTestVisible = false
                    };
                    _canvas.Children.Add(line);
                }
                line.Points.Add(new Point(X(index), BreadthY(value)));
            }
        }

        foreach (SentimentIndicator indicator in SentimentIndicatorCatalog.All.Where(_selected.Contains))
            DrawSeriesLine(indicator, X, Y, new SolidColorBrush(ColorFor(indicator)));

        _plotLeft = left;
        _plotTop = top;
        _plotWidth = width;
        _plotHeight = height;
        _horizontalSlots = slots;
        DrawCursorLine();
    }

    private void DrawSeriesLine(
        SentimentIndicator indicator,
        Func<int, double> x,
        Func<double, double> y,
        Brush brush)
    {
        Polyline? line = null;
        for (int index = 0; index < _renderedPoints.Count; index++)
        {
            double value = _renderedPoints[index].Value(indicator);
            if (!double.IsFinite(value))
            {
                line = null;
                continue;
            }
            if (line is null)
            {
                line = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = indicator == SentimentIndicator.CnnFearGreed ? 1.9 : 1.45,
                    Opacity = 0.94,
                    IsHitTestVisible = false
                };
                _canvas.Children.Add(line);
            }
            line.Points.Add(new Point(x(index), y(value)));
        }
    }

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_cursorPinned || _renderedPoints.Count == 0 || _plotWidth <= 0.0)
            return;
        double relative = Math.Clamp((e.GetPosition(_canvas).X - _plotLeft) / _plotWidth, 0.0, 1.0);
        int index = Math.Clamp((int)Math.Round(relative * _horizontalSlots), 0, _renderedPoints.Count - 1);
        CursorDateChanged?.Invoke(_renderedPoints[index].Date);
    }

    private void DrawCursorLine()
    {
        if (_cursorLine is not null)
            _canvas.Children.Remove(_cursorLine);
        _cursorLine = null;
        if (!_cursorDate.HasValue || _renderedPoints.Count == 0 || _plotWidth <= 0.0)
            return;

        int index = FindNearestIndex(_renderedPoints, _cursorDate.Value);
        double x = _plotLeft + _plotWidth * index / _horizontalSlots;
        _cursorLine = new Line
        {
            X1 = x,
            X2 = x,
            Y1 = _plotTop,
            Y2 = _plotTop + _plotHeight,
            Stroke = ChartCursorStyle.Stroke(_cursorPinned, Brushes.Black),
            StrokeThickness = ChartCursorStyle.Thickness(_cursorPinned, 1.1),
            Opacity = ChartCursorStyle.Opacity(_cursorPinned, 0.85),
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_cursorLine, 1000);
        _canvas.Children.Add(_cursorLine);
    }

    private static int FindNearestIndex(IReadOnlyList<SentimentPoint> points, DateTime date)
    {
        int low = 0;
        int high = points.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            DateTime candidate = points[middle].Date;
            if (candidate == date) return middle;
            if (candidate < date) low = middle + 1; else high = middle - 1;
        }
        if (low <= 0) return 0;
        if (low >= points.Count) return points.Count - 1;
        return Math.Abs((points[low - 1].Date - date).Ticks) <= Math.Abs((points[low].Date - date).Ticks)
            ? low - 1 : low;
    }

    private void AddText(string text, double x, double y, double size, Brush brush, FontWeight weight)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        _canvas.Children.Add(block);
    }
}
