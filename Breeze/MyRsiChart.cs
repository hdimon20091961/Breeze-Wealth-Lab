using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal sealed class MyRsiChart : Border
{
    private const int RightPaddingSlots = 2;
    private readonly Canvas _canvas = new();
    private TechnicalIndicatorSeries? _series;
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private DateTime? _cursorDate;
    private IReadOnlyList<TechnicalIndicatorPoint> _renderedPoints = [];
    private Line? _cursorLine;
    private bool _cursorPinned;
    private double _plotLeft;
    private double _plotTop;
    private double _plotWidth;
    private double _plotHeight;
    private double _horizontalSlots = 1;

    public event Action<DateTime>? CursorDateChanged;

    public MyRsiChart()
    {
        Background = new SolidColorBrush(Color.FromRgb(255, 252, 244));
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

    public void SetSeries(TechnicalIndicatorSeries? series)
    {
        _series = series;
        if (IsVisible)
            RenderSeries();
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
        AddText("MyRSI", 10, 6, 14, Brushes.Black, FontWeights.SemiBold);
        AddText(message, 10, 32, 12, new SolidColorBrush(Color.FromRgb(95, 97, 100)), FontWeights.Normal);
    }

    public void Refresh() => RenderSeries();

    private void RenderSeries()
    {
        if (_series is null || ActualWidth < 100 || ActualHeight < 90)
            return;

        _canvas.Children.Clear();
        _cursorLine = null;
        _renderedPoints = _series.Points
            .Where(point => !_viewportStart.HasValue || point.Date >= _viewportStart.Value)
            .Where(point => !_viewportEnd.HasValue || point.Date <= _viewportEnd.Value)
            .ToList();
        if (_renderedPoints.Count < 2)
            return;

        const double left = 12;
        const double top = 31;
        const double right = 42;
        const double bottom = 23;
        double width = Math.Max(1, ActualWidth - left - right);
        double height = Math.Max(1, ActualHeight - top - bottom);
        double rightAxisX = left + width;
        double slots = Math.Max(1, _renderedPoints.Count - 1 + RightPaddingSlots);
        double X(int index) => left + width * index / slots;
        double Y(double value) => top + height * (1.0 - (Math.Clamp(value, -1.0, 1.0) + 1.0) / 2.0);

        Brush gridBrush = new SolidColorBrush(Color.FromRgb(224, 216, 196));
        Brush axisBrush = new SolidColorBrush(Color.FromRgb(90, 92, 95));
        Brush myRsi14Brush = new SolidColorBrush(Color.FromRgb(25, 66, 110));
        Brush myRsi10Brush = new SolidColorBrush(Color.FromRgb(196, 43, 43));

        AddText("MyRSI 14,8", 10, 5, 11, myRsi14Brush, FontWeights.SemiBold);
        AddText("MyRSI 10,8", 92, 5, 11, myRsi10Brush, FontWeights.SemiBold);
        foreach (double level in new[] { -1.0, -0.5, 0.0, 0.5, 1.0 })
        {
            double y = Y(level);
            _canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = rightAxisX,
                Y1 = y,
                Y2 = y,
                Stroke = level == 0.0 ? new SolidColorBrush(Color.FromRgb(145, 138, 124)) : gridBrush,
                StrokeThickness = level == 0.0 ? 1.0 : 0.65,
                StrokeDashArray = Math.Abs(level) == 0.5 ? new DoubleCollection { 3, 3 } : null,
                IsHitTestVisible = false
            });
            AddText(level.ToString("0.0", CultureInfo.InvariantCulture), rightAxisX + 5, y - 7, 9,
                axisBrush, FontWeights.Normal);
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
        AddSeriesLine(_renderedPoints.Select(point => point.MyRsiRep), X, Y, myRsi14Brush, 1.65);
        AddSeriesLine(_renderedPoints.Select(point => point.MyRsiRep10), X, Y, myRsi10Brush, 1.45);

        AddText(_renderedPoints[0].Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            left, top + height + 3, 10, axisBrush, FontWeights.Normal);
        double lastDataX = X(_renderedPoints.Count - 1);
        AddText(_renderedPoints[^1].Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Math.Max(left, lastDataX - 46), top + height + 3, 10, axisBrush, FontWeights.Normal);

        _plotLeft = left;
        _plotTop = top;
        _plotWidth = width;
        _plotHeight = height;
        _horizontalSlots = slots;
        DrawCursorLine();
    }

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_cursorPinned || _renderedPoints.Count == 0 || _plotWidth <= 0)
            return;
        double relative = Math.Clamp((e.GetPosition(_canvas).X - _plotLeft) / _plotWidth, 0.0, 1.0);
        int index = Math.Clamp((int)Math.Round(relative * _horizontalSlots), 0, _renderedPoints.Count - 1);
        CursorDateChanged?.Invoke(_renderedPoints[index].Date);
    }

    private void DrawCursorLine()
    {
        if (_cursorLine is not null)
        {
            _canvas.Children.Remove(_cursorLine);
            _cursorLine = null;
        }
        if (!_cursorDate.HasValue || _renderedPoints.Count == 0 || _plotWidth <= 0)
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

    private static int FindNearestIndex(IReadOnlyList<TechnicalIndicatorPoint> points, DateTime date)
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

    private void AddSeriesLine(
        IEnumerable<double> values,
        Func<int, double> x,
        Func<double, double> y,
        Brush brush,
        double thickness)
    {
        var line = new Polyline
        {
            Stroke = brush,
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        int index = 0;
        foreach (double value in values)
        {
            if (double.IsFinite(value))
                line.Points.Add(new Point(x(index), y(value)));
            index++;
        }
        _canvas.Children.Add(line);
    }

    private void AddText(string text, double x, double y, double size, Brush brush, FontWeight weight)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        _canvas.Children.Add(block);
    }
}
