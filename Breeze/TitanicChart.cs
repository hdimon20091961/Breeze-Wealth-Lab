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
    private bool _showActive = true;
    private bool _showPersistent = true;
    private bool _showNewHighs = true;
    private bool _showNewLows = true;
    private bool _showTitanicValue = true;
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
    private double _fullPlotHeight;

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
    public void SetLayerVisibility(bool active, bool persistent, bool newHighs, bool newLows, bool titanicValue)
    {
        _showActive = active;
        _showPersistent = persistent;
        _showNewHighs = newHighs;
        _showNewLows = newLows;
        _showTitanicValue = titanicValue;
        if (IsVisible) RenderSeries();
    }
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
        if (_series is null || ActualWidth < 100 || ActualHeight < 180)
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
        _left = 76;
        _top = 50;
        const double right = 60;
        const double bottom = 10;
        double paneGap = _showHoldConfidence ? 24 : 8;
        _width = Math.Max(1, ActualWidth - _left - right);
        double availableHeight = Math.Max(1, ActualHeight - _top - bottom - paneGap);
        double breadthHeight = Math.Max(90, availableHeight * 0.32);
        _height = Math.Max(90, availableHeight - breadthHeight);
        double breadthTop = _top + _height + paneGap;
        _fullPlotHeight = breadthTop + breadthHeight - _top;
        double slot = _width / Math.Max(1, _rendered.Count - 1);
        double min = _rendered.Min(point => point.QqqLow);
        double max = _rendered.Max(point => point.QqqHigh);
        double range = Math.Max(0.01, max - min);
        min -= range * 0.06;
        max += range * 0.06;
        double X(int index) => _left + _width * index / Math.Max(1, _rendered.Count - 1);
        double Y(double value) => _top + _height * (1.0 - (value - min) / (max - min));
        double[] visibleBreadth = _rendered
            .SelectMany(point => new[]
            {
                _showNewHighs ? point.NasdaqNewHighs : double.NaN,
                _showNewLows ? point.NasdaqNewLows : double.NaN
            })
            .Where(double.IsFinite)
            .ToArray();
        double breadthMax = visibleBreadth.Length == 0 ? 1.0 : Math.Max(1.0, visibleBreadth.Max());
        breadthMax *= 1.05;
        double BreadthY(double value) => breadthTop + breadthHeight * (1.0 - Math.Clamp(value, 0.0, breadthMax) / breadthMax);
        double TitanicY(double value) => breadthTop + breadthHeight * (1.0 - Math.Clamp(value, 0.0, 1.0));

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
            if ((persistent && !_showPersistent) || (!persistent && !_showActive))
                continue;
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
            double bodyWidth = ChartDrawingMetrics.CandleBodyWidth(slot);
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
            AddText("Оценка", 5, _top + _height + 3, 9,
                Color.FromRgb(90, 92, 95), FontWeights.SemiBold);
        }

        var breadthBackground = new Rectangle
        {
            Width = _width,
            Height = breadthHeight,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(205, 215, 220)),
            StrokeThickness = 0.8,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(breadthBackground, _left);
        Canvas.SetTop(breadthBackground, breadthTop);
        _canvas.Children.Add(breadthBackground);

        bool showBreadthScale = _showNewHighs || _showNewLows;
        Brush breadthGrid = new SolidColorBrush(Color.FromRgb(225, 233, 236));
        if (showBreadthScale)
        {
            for (int tick = 0; tick <= 4; tick++)
            {
                double value = breadthMax * (4 - tick) / 4.0;
                double y = breadthTop + breadthHeight * tick / 4.0;
                _canvas.Children.Add(new Line
                {
                    X1 = _left, X2 = _left + _width, Y1 = y, Y2 = y,
                    Stroke = breadthGrid, StrokeThickness = 0.65, IsHitTestVisible = false
                });
                string label = value.ToString("F0");
                AddText(label, 7, y - 7, 9, Color.FromRgb(82, 94, 100), FontWeights.Normal);
                AddText(label, _left + _width + 34, y - 7, 9, Color.FromRgb(82, 94, 100), FontWeights.Normal);
            }
        }

        Color highsColor = Color.FromRgb(35, 145, 82);
        Color lowsColor = Color.FromRgb(196, 55, 55);
        Color titanicColor = Color.FromRgb(41, 88, 176);
        double legendX = _left + 36;
        if (_showNewHighs)
        {
            AddText("New High", legendX, breadthTop + 3, 10, highsColor, FontWeights.SemiBold);
            legendX += 61;
        }
        if (_showNewLows)
        {
            AddText("New Low", legendX, breadthTop + 3, 10, lowsColor, FontWeights.SemiBold);
            legendX += 58;
        }
        if (_showTitanicValue)
            AddText("Titanic NH/(NH+NL)", legendX, breadthTop + 3, 10, titanicColor, FontWeights.SemiBold);

        if (_showNewHighs)
        {
            var highsLine = new Polyline
            {
                Stroke = new SolidColorBrush(highsColor), StrokeThickness = 1.65, IsHitTestVisible = false
            };
            for (int index = 0; index < _rendered.Count; index++)
            {
                if (double.IsFinite(_rendered[index].NasdaqNewHighs))
                    highsLine.Points.Add(new Point(X(index), BreadthY(_rendered[index].NasdaqNewHighs)));
            }
            _canvas.Children.Add(highsLine);
        }
        if (_showNewLows)
        {
            var lowsLine = new Polyline
            {
                Stroke = new SolidColorBrush(lowsColor), StrokeThickness = 1.65, IsHitTestVisible = false
            };
            for (int index = 0; index < _rendered.Count; index++)
            {
                if (double.IsFinite(_rendered[index].NasdaqNewLows))
                    lowsLine.Points.Add(new Point(X(index), BreadthY(_rendered[index].NasdaqNewLows)));
            }
            _canvas.Children.Add(lowsLine);
        }
        if (_showTitanicValue)
        {
            for (int tick = 0; tick <= 4; tick++)
            {
                double value = 1.0 - tick * 0.25;
                double y = TitanicY(value);
                bool threshold = Math.Abs(value - 0.5) < 0.000001;
                var scaleLine = new Line
                {
                    X1 = _left, X2 = _left + _width, Y1 = y, Y2 = y,
                    Stroke = threshold
                        ? new SolidColorBrush(Color.FromRgb(202, 48, 48))
                        : new SolidColorBrush(Color.FromArgb(60, titanicColor.R, titanicColor.G, titanicColor.B)),
                    StrokeThickness = threshold ? 1.0 : 0.65,
                    IsHitTestVisible = false
                };
                if (threshold)
                    scaleLine.StrokeDashArray = new DoubleCollection { 4, 4 };
                _canvas.Children.Add(scaleLine);
                string label = value.ToString("0.00");
                Color labelColor = threshold ? Color.FromRgb(202, 48, 48) : titanicColor;
                AddText(label, _left - 31, y - 7, 9, labelColor, FontWeights.SemiBold);
                AddText(label, _left + _width + 5, y - 7, 9, labelColor, FontWeights.SemiBold);
            }

            var titanicLine = new Polyline
            {
                Stroke = new SolidColorBrush(titanicColor),
                StrokeThickness = 2.0,
                StrokeDashArray = new DoubleCollection { 8, 4 },
                IsHitTestVisible = false
            };
            for (int index = 0; index < _rendered.Count; index++)
            {
                double value = _rendered[index].NasdaqHighLowRatio;
                if (double.IsFinite(value))
                    titanicLine.Points.Add(new Point(X(index), TitanicY(value)));
            }
            _canvas.Children.Add(titanicLine);
        }

        TitanicPoint last = _rendered[^1];
        bool lastPersistent = last.Signal && last.RunLength >= _persistentDays;
        string lastState = last.Signal
            ? $"ACTIVE · {last.RunLength} days" + (lastPersistent ? " · persistent" : string.Empty)
            : "inactive";
        string lastRatio = double.IsFinite(last.NasdaqHighLowRatio) ? last.NasdaqHighLowRatio.ToString("F3") : "n/a";
        AddText($"Last: {lastState}   Titanic {lastRatio}   NH {last.NasdaqNewHighs:F0} / NL {last.NasdaqNewLows:F0}",
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
            X1 = x, X2 = x, Y1 = _top, Y2 = _top + _fullPlotHeight,
            Stroke = ChartCursorStyle.Stroke(_cursorPinned, new SolidColorBrush(Color.FromRgb(55, 70, 76))),
            StrokeThickness = ChartCursorStyle.Thickness(_cursorPinned, 1.2),
            Opacity = ChartCursorStyle.Opacity(_cursorPinned, 1.0),
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_cursorLine, 1000);
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
