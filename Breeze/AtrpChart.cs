using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal sealed class AtrpChart : Border
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
    private double _horizontalSlots = 1.0;
    private bool _showDateLabels = true;
    private double _leftMargin = 58;
    private double _rightMargin = 46;
    private double? _referenceLevel;
    private DateTime[]? _masterDates;
    private DateTime[] _renderedDates = [];
    private Dictionary<DateTime, TechnicalIndicatorPoint> _byDate = [];

    public event Action<DateTime>? CursorDateChanged;

    public AtrpChart()
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

    public void SetSeries(TechnicalIndicatorSeries? series)
    {
        _series = series;
        _byDate = series?.Points.ToDictionary(point => point.Date.Date) ?? [];
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

    public void SetMasterDates(IEnumerable<DateTime> dates)
    {
        _masterDates = dates.Select(date => date.Date).Distinct().OrderBy(date => date).ToArray();
        if (IsVisible) RenderSeries();
    }

    public void SetHorizontalPlotMargins(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right) || left < 0 || right < 0) return;
        _leftMargin = left;
        _rightMargin = right;
        if (IsVisible) RenderSeries();
    }

    public void SetReferenceLevel(double? value)
    {
        _referenceLevel = value is double level && double.IsFinite(level) && level >= 0 ? level : null;
        if (IsVisible) RenderSeries();
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

    public void SetShowDateLabels(bool show)
    {
        _showDateLabels = show;
        if (IsVisible)
            RenderSeries();
    }

    public void SetMessage(string message)
    {
        _series = null;
        _renderedPoints = [];
        _renderedDates = [];
        _byDate.Clear();
        _canvas.Children.Clear();
        AddText("ATRP(1), %", 10, 6, 12, Brushes.Black, FontWeights.SemiBold);
        AddText(message, 10, 31, 11, new SolidColorBrush(Color.FromRgb(95, 97, 100)), FontWeights.Normal);
    }

    public void Refresh() => RenderSeries();

    private void RenderSeries()
    {
        _canvas.Children.Clear();
        _cursorLine = null;
        if (_series is null || _canvas.ActualWidth < 100 || _canvas.ActualHeight < 40)
            return;
        _renderedPoints = _series.Points
            .Where(point => !_viewportStart.HasValue || point.Date >= _viewportStart.Value)
            .Where(point => !_viewportEnd.HasValue || point.Date <= _viewportEnd.Value)
            .ToList();
        _renderedDates = (_masterDates ?? _series.Points.Select(point => point.Date.Date).ToArray())
            .Where(date => !_viewportStart.HasValue || date >= _viewportStart.Value)
            .Where(date => !_viewportEnd.HasValue || date <= _viewportEnd.Value).ToArray();
        if (_renderedDates.Length < 2)
            return;

        double left = _leftMargin;
        const double top = 27;
        double right = _rightMargin;
        double bottom = _showDateLabels ? 22 : 8;
        double width = Math.Max(1.0, _canvas.ActualWidth - left - right);
        double height = Math.Max(1.0, _canvas.ActualHeight - top - bottom);
        double rightAxisX = left + width;
        double slots = Math.Max(1.0, _renderedDates.Length - 1 + RightPaddingSlots);
        double visibleMaximum = _renderedPoints.Where(point => double.IsFinite(point.Atrp1))
            .Select(point => point.Atrp1).DefaultIfEmpty(0).Max();
        if (_referenceLevel.HasValue) visibleMaximum = Math.Max(visibleMaximum, _referenceLevel.Value);
        double maximum = Math.Max(0.01, visibleMaximum * 1.05);
        double X(int index) => left + width * index / slots;
        double Y(double value) => top + height * (maximum - Math.Clamp(value, 0.0, maximum)) / maximum;

        Brush gridBrush = new SolidColorBrush(Color.FromRgb(224, 216, 196));
        Brush axisBrush = new SolidColorBrush(Color.FromRgb(90, 92, 95));
        Brush atrpBrush = new SolidColorBrush(Color.FromRgb(103, 80, 164));
        AddText("ATRP(1), %", 10, 5, 11, atrpBrush, FontWeights.SemiBold);

        foreach (double level in new[] { 0.0, maximum / 2.0, maximum })
        {
            double y = Y(level);
            _canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = rightAxisX,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 0.7,
                IsHitTestVisible = false
            });
            AddText(level.ToString("0.00", CultureInfo.InvariantCulture), rightAxisX + 5, y - 7, 9,
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

        if (_referenceLevel is double reference)
        {
            double y = Y(reference);
            _canvas.Children.Add(new Line { X1 = left, X2 = rightAxisX, Y1 = y, Y2 = y,
                Stroke = Brushes.DarkOrange, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false });
            AddText($"Порог: {reference:0.##}%", Math.Max(left, rightAxisX - 95), 5, 10,
                Brushes.DarkOrange, FontWeights.Normal);
        }
        Polyline? line = null;
        for (int index = 0; index < _renderedDates.Length; index++)
        {
            if (!_byDate.TryGetValue(_renderedDates[index], out TechnicalIndicatorPoint? point) ||
                !double.IsFinite(point.Atrp1)) { line = null; continue; }
            if (line is null)
            {
                line = new Polyline { Stroke = atrpBrush, StrokeThickness = 1.55, IsHitTestVisible = false };
                _canvas.Children.Add(line);
            }
            line.Points.Add(new Point(X(index), Y(point.Atrp1)));
        }

        if (_showDateLabels)
        {
            AddText(_renderedDates[0].ToString("yyyy-MM", CultureInfo.InvariantCulture),
                left, top + height + 2, 10, axisBrush, FontWeights.Normal);
            double lastDataX = X(_renderedDates.Length - 1);
            AddText(_renderedDates[^1].ToString("yyyy-MM", CultureInfo.InvariantCulture),
                Math.Max(left, lastDataX - 46), top + height + 2, 10, axisBrush, FontWeights.Normal);
        }

        _plotLeft = left;
        _plotTop = top;
        _plotWidth = width;
        _plotHeight = height;
        _horizontalSlots = slots;
        DrawCursorLine();
    }

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_cursorPinned || _renderedDates.Length == 0 || _plotWidth <= 0.0)
            return;
        double relative = Math.Clamp((e.GetPosition(_canvas).X - _plotLeft) / _plotWidth, 0.0, 1.0);
        int index = Math.Clamp((int)Math.Round(relative * _horizontalSlots), 0, _renderedDates.Length - 1);
        CursorDateChanged?.Invoke(_renderedDates[index]);
    }

    private void DrawCursorLine()
    {
        if (_cursorLine is not null)
            _canvas.Children.Remove(_cursorLine);
        _cursorLine = null;
        if (!_cursorDate.HasValue || _renderedDates.Length == 0 || _plotWidth <= 0.0)
            return;

        int index = FindNearestIndex(_renderedDates, _cursorDate.Value);
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

    private static int FindNearestIndex(IReadOnlyList<DateTime> points, DateTime date)
    {
        int low = 0;
        int high = points.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            DateTime candidate = points[middle];
            if (candidate == date) return middle;
            if (candidate < date) low = middle + 1; else high = middle - 1;
        }
        if (low <= 0) return 0;
        if (low >= points.Count) return points.Count - 1;
        return Math.Abs((points[low - 1] - date).Ticks) <= Math.Abs((points[low] - date).Ticks)
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
