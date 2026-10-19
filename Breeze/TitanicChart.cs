using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal sealed class TitanicChart : Border
{
    private static readonly Color EarlyColor = Color.FromRgb(88, 190, 228);
    private static readonly Color PersistentColor = Color.FromRgb(47, 166, 105);
    private readonly Canvas _canvas = new();
    private TitanicSeries? _series;
    private HoldConfidenceSeries? _holdConfidence;
    private bool _showCandles;
    private bool _showHoldConfidence = true;
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private IReadOnlyList<TitanicPoint> _rendered = [];
    private IReadOnlyList<HoldConfidencePoint?> _renderedHoldConfidence = [];
    private DateTime? _cursorDate;
    private int _persistentDays = TitanicSyndromeCalculator.PersistentDays;
    private Line? _cursorLine;
    private bool _cursorPinned;
    private double _left;
    private double _top;
    private double _width;
    private double _height;

    public event Action<DateTime>? CursorDateChanged;
    public event Action<int>? PersistentDaysChanged;

    public TitanicChart()
    {
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(190, 210, 218));
        BorderThickness = new Thickness(1);
        Margin = new Thickness(4);
        Child = _canvas;
        SizeChanged += (_, _) => RenderSeries();
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(RenderSeries)); };
        MouseMove += ChartMouseMove;
    }

    public void SetData(TitanicSeries? series)
    {
        _series = series;
        if (series is not null)
            _persistentDays = series.PersistentDays;
        if (IsVisible) RenderSeries();
    }
    public void SetShowCandles(bool showCandles) { _showCandles = showCandles; if (IsVisible) RenderSeries(); }
    public void SetHoldConfidence(HoldConfidenceSeries? series) { _holdConfidence = series; if (IsVisible) RenderSeries(); }
    public void SetShowHoldConfidence(bool show) { _showHoldConfidence = show; if (IsVisible) RenderSeries(); }
    public void SetViewport(DateTime start, DateTime end) { _viewportStart = start; _viewportEnd = end; if (IsVisible) RenderSeries(); }
    public void SetCursorDate(DateTime? date) { _cursorDate = date; if (IsVisible) DrawCursor(); }
    public void SetCursorPinned(bool pinned) { if (_cursorPinned == pinned) return; _cursorPinned = pinned; if (IsVisible) DrawCursor(); }
    public void Refresh() => RenderSeries();

    public void SetMessage(string title, string message)
    {
        _canvas.Children.Clear();
        AddText(title, 12, 9, 15, Color.FromRgb(35, 55, 65), FontWeights.SemiBold);
        AddText(message, 12, 38, 13, Color.FromRgb(90, 105, 112), FontWeights.Normal);
    }

    private void RenderSeries()
    {
        if (_series is null || ActualWidth < 100 || ActualHeight < 120)
            return;

        _rendered = _series.Points
            .Where(point => !_viewportStart.HasValue || point.Date >= _viewportStart.Value)
            .Where(point => !_viewportEnd.HasValue || point.Date <= _viewportEnd.Value)
            .ToList();
        if (_rendered.Count < 2)
            return;

        var holdByDate = _holdConfidence?.Points.ToDictionary(point => point.Date) ?? [];
        _renderedHoldConfidence = _rendered
            .Select(point => holdByDate.TryGetValue(point.Date, out HoldConfidencePoint? hold) ? hold : null)
            .ToList();

        _canvas.Children.Clear();
        _cursorLine = null;
        _left = 58;
        _top = 50;
        double right = 20;
        double bottom = 10;
        double holdSpace = _showHoldConfidence ? 22 : 0;
        _width = Math.Max(1, ActualWidth - _left - right);
        _height = Math.Max(1, ActualHeight - _top - bottom - holdSpace);
        double slot = _width / Math.Max(1, _rendered.Count - 1);
        double min = _rendered.Min(point => point.QqqLow);
        double max = _rendered.Max(point => point.QqqHigh);
        double range = Math.Max(0.01, max - min);
        min -= range * 0.06;
        max += range * 0.06;
        double X(int index) => _left + _width * index / Math.Max(1, _rendered.Count - 1);
        double Y(double value) => _top + _height * (1.0 - (value - min) / (max - min));

        AddText("SentimenTrader Titanic — Nasdaq", 12, 8, 16, Color.FromRgb(26, 73, 94), FontWeights.SemiBold);
        AddText("QQQ within 3% of 252-day high in last 7 sessions AND Nasdaq New Lows > New Highs", 12, 29, 11, Color.FromRgb(78, 99, 108), FontWeights.Normal);

        for (int tick = 0; tick < 5; tick++)
        {
            double y = _top + _height * tick / 4.0;
            var grid = new Line { X1 = _left, X2 = _left + _width, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(222, 232, 235)), StrokeThickness = 1 };
            _canvas.Children.Add(grid);
            double value = max - (max - min) * tick / 4.0;
            string label = value.ToString("F0");
            AddText(label, 7, y - 8, 10, Color.FromRgb(85, 95, 100), FontWeights.Normal);
            AddText(label, _left + _width + 5, y - 8, 10, Color.FromRgb(85, 95, 100), FontWeights.Normal);
        }

        for (int index = 0; index < _rendered.Count; index++)
        {
            TitanicPoint point = _rendered[index];
            if (!point.Signal)
                continue;
            bool persistent = point.Signal && point.RunLength >= _persistentDays;
            Color color = persistent ? PersistentColor : EarlyColor;
            var zone = new Rectangle
            {
                Width = Math.Max(1.0, slot + 0.8), Height = _height,
                Fill = new SolidColorBrush(color), Opacity = persistent ? 0.25 : 0.16,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(zone, X(index) - slot / 2);
            Canvas.SetTop(zone, _top);
            _canvas.Children.Add(zone);
        }

        if (_showCandles)
        {
            double bodyWidth = Math.Clamp(slot * 0.65, 1.0, 7.0);
            Brush risingBrush = new SolidColorBrush(Color.FromRgb(37, 147, 91));
            Brush fallingBrush = new SolidColorBrush(Color.FromRgb(48, 112, 158));
            Brush wickBrush = new SolidColorBrush(Color.FromRgb(38, 66, 79));
            for (int index = 0; index < _rendered.Count; index++)
            {
                TitanicPoint point = _rendered[index];
                double x = X(index);
                bool rising = point.QqqClose >= point.QqqOpen;
                Brush candleBrush = rising ? risingBrush : fallingBrush;
                _canvas.Children.Add(new Line
                {
                    X1 = x, X2 = x, Y1 = Y(point.QqqHigh), Y2 = Y(point.QqqLow),
                    Stroke = wickBrush, StrokeThickness = 0.8
                });
                double openY = Y(point.QqqOpen);
                double closeY = Y(point.QqqClose);
                var body = new Rectangle
                {
                    Width = bodyWidth,
                    Height = Math.Max(1.0, Math.Abs(closeY - openY)),
                    Fill = rising ? Brushes.White : fallingBrush,
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
            var line = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(31, 91, 125)), StrokeThickness = 2.0, IsHitTestVisible = false };
            for (int index = 0; index < _rendered.Count; index++)
                line.Points.Add(new Point(X(index), Y(_rendered[index].QqqClose)));
            _canvas.Children.Add(line);
        }

        if (_showHoldConfidence)
        {
            for (int index = 0; index < _renderedHoldConfidence.Count; index++)
            {
                HoldConfidencePoint? hold = _renderedHoldConfidence[index];
                var ribbon = new Rectangle
                {
                    Width = Math.Max(1.0, slot + 0.8),
                    Height = 14,
                    Fill = new SolidColorBrush(hold is null
                        ? Color.FromRgb(190, 190, 190)
                        : FuzzyStateChart.ColorForHoldConfidence(hold.Value)),
                    Opacity = hold is null ? 0.45 : 0.96,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ribbon, X(index) - slot / 2);
                Canvas.SetTop(ribbon, _top + _height + 5);
                _canvas.Children.Add(ribbon);
            }
            AddText("Hold %", 5, _top + _height + 3, 9,
                Color.FromRgb(90, 92, 95), FontWeights.SemiBold);
        }

        TitanicPoint last = _rendered[^1];
        bool lastPersistent = last.Signal && last.RunLength >= _persistentDays;
        string lastState = last.Signal
            ? $"ACTIVE · {last.RunLength} days" + (lastPersistent ? " · persistent" : string.Empty)
            : "inactive";
        AddText($"Last: {lastState}   NH {last.NasdaqNewHighs:F0} / NL {last.NasdaqNewLows:F0}",
            Math.Max(500, ActualWidth - 385), 10, 11,
            lastPersistent ? PersistentColor : last.Signal ? EarlyColor : Color.FromRgb(90, 105, 112),
            FontWeights.SemiBold);
        AddPersistentControls(Math.Max(310, ActualWidth - 565), 6);
        DrawCursor();
    }

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_cursorPinned || _rendered.Count == 0 || _width <= 0)
            return;
        double x = Math.Clamp(e.GetPosition(_canvas).X, _left, _left + _width);
        int index = Math.Clamp((int)Math.Round((x - _left) / _width * (_rendered.Count - 1)), 0, _rendered.Count - 1);
        _cursorDate = _rendered[index].Date;
        DrawCursor();
        CursorDateChanged?.Invoke(_cursorDate.Value);
    }

    private void DrawCursor()
    {
        if (_cursorLine is not null)
            _canvas.Children.Remove(_cursorLine);
        if (!_cursorDate.HasValue || _rendered.Count == 0)
            return;
        int index = FindNearestIndex(_rendered, _cursorDate.Value);
        double x = _left + _width * index / Math.Max(1, _rendered.Count - 1);
        _cursorLine = new Line
        {
            X1 = x, X2 = x, Y1 = _top, Y2 = _top + _height + (_showHoldConfidence ? 19 : 0),
            Stroke = ChartCursorStyle.Stroke(_cursorPinned, new SolidColorBrush(Color.FromRgb(55, 70, 76))),
            StrokeThickness = ChartCursorStyle.Thickness(_cursorPinned, 1.2),
            Opacity = ChartCursorStyle.Opacity(_cursorPinned, 1.0),
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_cursorLine);
    }

    private static int FindNearestIndex(IReadOnlyList<TitanicPoint> points, DateTime date)
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

    private void AddText(string text, double x, double y, double size, Color color, FontWeight weight)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = new SolidColorBrush(color), FontWeight = weight, IsHitTestVisible = false };
        Canvas.SetLeft(block, x); Canvas.SetTop(block, y); _canvas.Children.Add(block);
    }

    private void AddPersistentControls(double x, double y)
    {
        var minus = new Button { Content = "−", Width = 25, Height = 22, Padding = new Thickness(0), ToolTip = "Persistent one day earlier" };
        minus.Click += (_, _) => ChangePersistentDays(-1);
        Canvas.SetLeft(minus, x); Canvas.SetTop(minus, y); _canvas.Children.Add(minus);

        AddText($"{_persistentDays} days", x + 31, y + 3, 11, Color.FromRgb(47, 120, 82), FontWeights.SemiBold);

        var plus = new Button { Content = "+", Width = 25, Height = 22, Padding = new Thickness(0), ToolTip = "Persistent one day later" };
        plus.Click += (_, _) => ChangePersistentDays(1);
        Canvas.SetLeft(plus, x + 84); Canvas.SetTop(plus, y); _canvas.Children.Add(plus);
    }

    private void ChangePersistentDays(int delta)
    {
        int updated = Math.Clamp(_persistentDays + delta, 1, 30);
        if (updated == _persistentDays)
            return;
        _persistentDays = updated;
        PersistentDaysChanged?.Invoke(updated);
        RenderSeries();
    }
}
