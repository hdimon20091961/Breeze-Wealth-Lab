using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal sealed class BreadthChart : Border
{
    private const int RightPaddingSlots = 2;
    private readonly Canvas _canvas = new();
    private BreadthSeries? _series;
    private PriceSeries? _priceSeries;
    private bool _showPriceSeries;
    private bool _showPriceCandles;
    private bool _useLogBreadthScale;
    private bool _autoScaleBreadth;
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private IReadOnlyList<BreadthPoint> _renderedPoints = [];
    private double _plotLeft;
    private double _plotWidth;
    private double _horizontalSlots = 1;

    public event Action<DateTime>? CursorDateChanged;

    public BreadthChart()
    {
        Background = new SolidColorBrush(Color.FromRgb(255, 250, 236));
        BorderBrush = new SolidColorBrush(Color.FromRgb(205, 195, 171));
        BorderThickness = new Thickness(1);
        Margin = new Thickness(4);
        Padding = new Thickness(2);
        Child = _canvas;
        SizeChanged += (_, _) => RenderSeries();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(new Action(RenderSeries));
        };
        MouseMove += ChartMouseMove;
    }

    public void SetSeries(BreadthSeries series)
    {
        _series = series;
        RenderSeries();
    }

    public void SetViewport(DateTime start, DateTime end)
    {
        _viewportStart = start;
        _viewportEnd = end;
        RenderSeries();
    }

    public void SetPriceSeries(PriceSeries? series)
    {
        _priceSeries = series;
        RenderSeries();
    }

    public void SetShowPriceSeries(bool show)
    {
        _showPriceSeries = show;
        RenderSeries();
    }

    public void SetShowPriceCandles(bool show)
    {
        _showPriceCandles = show;
        RenderSeries();
    }

    public void SetLogBreadthScale(bool useLogarithmicScale)
    {
        _useLogBreadthScale = useLogarithmicScale;
        RenderSeries();
    }

    public void SetAutoScaleBreadth(bool autoScale)
    {
        _autoScaleBreadth = autoScale;
        RenderSeries();
    }

    public void Refresh() => RenderSeries();

    public void SetMessage(string title, string message)
    {
        _series = null;
        _renderedPoints = [];
        _canvas.Children.Clear();
        AddText(title, 12, 8, 15, new SolidColorBrush(Color.FromRgb(37, 48, 61)), FontWeights.SemiBold);
        AddText(message, 12, 36, 13, new SolidColorBrush(Color.FromRgb(95, 97, 100)), FontWeights.Normal);
    }

    private void RenderSeries()
    {
        if (_series is null || ActualWidth < 80 || ActualHeight < 80)
            return;

        _renderedPoints = [];
        _canvas.Children.Clear();
        if (_series.Points.Count == 0)
        {
            AddText(_series.Name, 12, 8, 15, new SolidColorBrush(Color.FromRgb(37, 48, 61)), FontWeights.SemiBold);
            AddText("No breadth data is available", 12, 36, 13,
                new SolidColorBrush(Color.FromRgb(95, 97, 100)), FontWeights.Normal);
            return;
        }

        IReadOnlyList<BreadthPoint> visiblePoints = _viewportStart.HasValue && _viewportEnd.HasValue
            ? _series.Points.Where(point => point.Date >= _viewportStart.Value && point.Date <= _viewportEnd.Value).ToList()
            : _series.Points;
        if (visiblePoints.Count == 0)
            return;

        IReadOnlyList<PricePoint> visiblePricePoints = _showPriceSeries && _priceSeries is not null
            ? _priceSeries.Points.Where(point => point.Date >= visiblePoints[0].Date && point.Date <= visiblePoints[^1].Date).ToList()
            : [];
        if (visiblePricePoints.Count > 0)
        {
            var breadthDates = visiblePoints.Select(point => point.Date).ToHashSet();
            visiblePricePoints = visiblePricePoints.Where(point => breadthDates.Contains(point.Date)).ToList();
        }
        bool showPrice = visiblePricePoints.Count > 1;

        double left = showPrice ? 58 : 12;
        const double top = 34;
        const double right = 46;
        const double bottom = 24;
        double width = Math.Max(1, ActualWidth - left - right);
        double height = Math.Max(1, ActualHeight - top - bottom);
        double rightAxisX = left + width;
        Brush darkBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        Brush axisBrush = new SolidColorBrush(Color.FromRgb(103, 105, 107));
        (double breadthMinimum, double breadthMaximum) = GetBreadthScaleBounds(visiblePoints);

        AddText(_series.Name, 10, 6, 14, new SolidColorBrush(Color.FromRgb(37, 48, 61)), FontWeights.SemiBold);
        double[] breadthLevels = GetBreadthScaleLevels(breadthMinimum, breadthMaximum);
        foreach (double level in breadthLevels)
        {
            double y = top + height * (1.0 - BreadthScaleRatio(level, breadthMinimum, breadthMaximum));
            _canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = rightAxisX,
                Y1 = y,
                Y2 = y,
                Stroke = Math.Abs(level - 50.0) < 0.001
                    ? new SolidColorBrush(Color.FromRgb(125, 137, 148))
                    : new SolidColorBrush(Color.FromRgb(224, 216, 196)),
                StrokeThickness = Math.Abs(level - 50.0) < 0.001 ? 1.2 : 0.7
            });
            AddText(FormatBreadthLevel(level), rightAxisX + 5, y - 9, 10,
                axisBrush, FontWeights.Normal);
        }

        _canvas.Children.Add(new Line
        {
            X1 = rightAxisX,
            X2 = rightAxisX,
            Y1 = top,
            Y2 = top + height,
            Stroke = darkBrush,
            StrokeThickness = 0.9
        });

        var breadthLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(25, 112, 174)),
            StrokeThickness = 1.7
        };
        int count = visiblePoints.Count;
        double horizontalSlots = Math.Max(1, count - 1 + RightPaddingSlots);
        _renderedPoints = visiblePoints;
        _plotLeft = left;
        _plotWidth = width;
        _horizontalSlots = horizontalSlots;
        for (int index = 0; index < count; index++)
        {
            double x = left + width * index / horizontalSlots;
            double y = top + height * (1.0 - BreadthScaleRatio(
                visiblePoints[index].Value, breadthMinimum, breadthMaximum));
            breadthLine.Points.Add(new Point(x, y));
        }
        _canvas.Children.Add(breadthLine);

        if (showPrice)
        {
            double priceMin = visiblePricePoints.Min(point => point.Low);
            double priceMax = visiblePricePoints.Max(point => point.High);
            if (Math.Abs(priceMax - priceMin) < 0.000001)
            {
                priceMin -= 1;
                priceMax += 1;
            }

            var breadthIndexByDate = visiblePoints
                .Select((point, index) => (point.Date, index))
                .ToDictionary(item => item.Date, item => item.index);
            double PriceY(double value) => top + height * (1.0 - (value - priceMin) / (priceMax - priceMin));
            double PriceX(DateTime date) => left + width * breadthIndexByDate[date] / horizontalSlots;

            if (_showPriceCandles)
            {
                double bodyWidth = Math.Clamp(width / Math.Max(visiblePricePoints.Count, 1) * 0.65, 1.0, 7.0);
                Brush risingBrush = new SolidColorBrush(Color.FromRgb(40, 164, 64));
                Brush fallingBrush = new SolidColorBrush(Color.FromRgb(183, 52, 45));
                Brush hollowFill = new SolidColorBrush(Color.FromRgb(255, 250, 236));
                foreach (PricePoint point in visiblePricePoints)
                {
                    double x = PriceX(point.Date);
                    bool rising = point.Close >= point.Open;
                    Brush candleBrush = rising ? risingBrush : fallingBrush;
                    _canvas.Children.Add(new Line
                    {
                        X1 = x,
                        X2 = x,
                        Y1 = PriceY(point.High),
                        Y2 = PriceY(point.Low),
                        Stroke = darkBrush,
                        StrokeThickness = 0.8
                    });
                    double openY = PriceY(point.Open);
                    double closeY = PriceY(point.Close);
                    var body = new Rectangle
                    {
                        Width = bodyWidth,
                        Height = Math.Max(1, Math.Abs(closeY - openY)),
                        Fill = rising ? hollowFill : fallingBrush,
                        Stroke = candleBrush,
                        StrokeThickness = 1.0
                    };
                    Canvas.SetLeft(body, x - bodyWidth / 2);
                    Canvas.SetTop(body, Math.Min(openY, closeY));
                    _canvas.Children.Add(body);
                }
            }
            else
            {
                var priceLine = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(188, 82, 35)),
                    StrokeThickness = 1.35,
                    Opacity = 0.9
                };
                foreach (PricePoint point in visiblePricePoints)
                    priceLine.Points.Add(new Point(PriceX(point.Date), PriceY(point.Close)));
                _canvas.Children.Add(priceLine);
            }

            _canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left,
                Y1 = top,
                Y2 = top + height,
                Stroke = darkBrush,
                StrokeThickness = 0.9
            });
            AddText(priceMax.ToString("N0", CultureInfo.InvariantCulture), 5, top - 7, 10, darkBrush, FontWeights.Normal);
            AddText(((priceMin + priceMax) / 2).ToString("N0", CultureInfo.InvariantCulture), 5, top + height / 2 - 7, 10, darkBrush, FontWeights.Normal);
            AddText(priceMin.ToString("N0", CultureInfo.InvariantCulture), 5, top + height - 12, 10, darkBrush, FontWeights.Normal);
        }

        BreadthPoint first = visiblePoints[0];
        BreadthPoint last = visiblePoints[^1];
        AddText(first.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture), left, top + height + 3, 10, axisBrush, FontWeights.Normal);
        double lastDataX = left + width * (count - 1) / horizontalSlots;
        AddText(last.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture), Math.Max(left, lastDataX - 46), top + height + 3, 10, axisBrush, FontWeights.Normal);
        AddText($"Last: {last.Value:F1}%  (n={last.EligibleCount})", Math.Max(left + 120, ActualWidth - 215), 7, 11,
            new SolidColorBrush(Color.FromRgb(75, 78, 82)), FontWeights.Normal);
    }

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_renderedPoints.Count == 0 || _plotWidth <= 0)
            return;

        double x = e.GetPosition(_canvas).X;
        double relative = Math.Clamp((x - _plotLeft) / _plotWidth, 0, 1);
        int index = (int)Math.Round(relative * _horizontalSlots);
        index = Math.Clamp(index, 0, _renderedPoints.Count - 1);
        CursorDateChanged?.Invoke(_renderedPoints[index].Date);
    }

    private (double Minimum, double Maximum) GetBreadthScaleBounds(IReadOnlyList<BreadthPoint> points)
    {
        if (!_autoScaleBreadth)
            return _useLogBreadthScale ? (10.0, 100.0) : (0.0, 100.0);

        double dataMinimum = points.Min(point => point.Value);
        double dataMaximum = points.Max(point => point.Value);
        if (_useLogBreadthScale)
        {
            const double positiveFloor = 0.1;
            double logMinimum = Math.Log10(Math.Max(positiveFloor, dataMinimum));
            double logMaximum = Math.Log10(Math.Max(positiveFloor, dataMaximum));
            if (logMaximum - logMinimum < 0.01)
            {
                logMinimum -= 0.05;
                logMaximum += 0.05;
            }

            double logPadding = (logMaximum - logMinimum) / 8.0;
            double minimum = Math.Max(positiveFloor, Math.Pow(10.0, logMinimum - logPadding));
            double maximum = Math.Min(100.0, Math.Pow(10.0, logMaximum + logPadding));
            return (minimum, Math.Max(minimum * 1.01, maximum));
        }

        double range = dataMaximum - dataMinimum;
        if (range < 0.1)
            range = Math.Max(1.0, Math.Abs(dataMaximum) * 0.05);
        double linearPadding = range / 8.0;
        double linearMinimum = Math.Max(0.0, dataMinimum - linearPadding);
        double linearMaximum = Math.Min(100.0, dataMaximum + linearPadding);
        return (linearMinimum, Math.Max(linearMinimum + 0.1, linearMaximum));
    }

    private double[] GetBreadthScaleLevels(double minimum, double maximum)
    {
        if (!_autoScaleBreadth)
            return _useLogBreadthScale
                ? [10.0, 25.0, 50.0, 75.0, 100.0]
                : [0.0, 25.0, 50.0, 75.0, 100.0];

        var levels = new double[5];
        if (_useLogBreadthScale)
        {
            double logMinimum = Math.Log10(minimum);
            double logMaximum = Math.Log10(maximum);
            for (int index = 0; index < levels.Length; index++)
                levels[index] = Math.Pow(10.0, logMinimum + (logMaximum - logMinimum) * index / 4.0);
        }
        else
        {
            for (int index = 0; index < levels.Length; index++)
                levels[index] = minimum + (maximum - minimum) * index / 4.0;
        }

        return levels;
    }

    private double BreadthScaleRatio(double value, double minimum, double maximum)
    {
        double bounded = Math.Clamp(value, minimum, maximum);
        if (_useLogBreadthScale)
        {
            return (Math.Log10(bounded) - Math.Log10(minimum)) /
                   (Math.Log10(maximum) - Math.Log10(minimum));
        }

        return (bounded - minimum) / (maximum - minimum);
    }

    private static string FormatBreadthLevel(double value)
    {
        string format = value < 1.0 ? "0.##" : value < 10.0 ? "0.#" : "0";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private void AddText(string text, double x, double y, double size, Brush brush, FontWeight weight)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        _canvas.Children.Add(block);
    }
}
