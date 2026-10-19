using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal sealed class ZweigChart : Border
{
    private readonly Canvas _canvas = new();
    private PriceSeries? _priceSeries;
    private ZweigSeries? _series;
    private HoldConfidenceSeries? _holdConfidence;
    private bool _showCandles;
    private bool _showHoldConfidence = true;
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private IReadOnlyList<(PricePoint Price, ZweigPoint Zweig)> _rendered = [];
    private IReadOnlyList<HoldConfidencePoint?> _renderedHoldConfidence = [];
    private DateTime? _cursorDate;
    private Line? _cursorLine;
    private bool _cursorPinned;
    private double _left;
    private double _top;
    private double _width;
    private double _fullPlotHeight;

    public event Action<DateTime>? CursorDateChanged;

    public ZweigChart()
    {
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(190, 210, 198));
        BorderThickness = new Thickness(1);
        Margin = new Thickness(4);
        Child = _canvas;
        SizeChanged += (_, _) => RenderSeries();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(new Action(RenderSeries));
        };
        MouseMove += ChartMouseMove;
    }

    public void SetData(PriceSeries? priceSeries, ZweigSeries? series)
    {
        _priceSeries = priceSeries;
        _series = series;
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
        _rendered = [];
        _canvas.Children.Clear();
        AddText(title, 12, 9, 15, Color.FromRgb(32, 82, 49), FontWeights.SemiBold);
        AddText(message, 12, 38, 13, Color.FromRgb(90, 105, 96), FontWeights.Normal);
    }

    private void RenderSeries()
    {
        if (_priceSeries is null || _series is null || ActualWidth < 100 || ActualHeight < 180)
            return;

        var priceByDate = _priceSeries.Points.ToDictionary(point => point.Date);
        _rendered = _series.Points
            .Where(point => priceByDate.ContainsKey(point.Date))
            .Where(point => !_viewportStart.HasValue || point.Date >= _viewportStart.Value)
            .Where(point => !_viewportEnd.HasValue || point.Date <= _viewportEnd.Value)
            .Select(point => (Price: priceByDate[point.Date], Zweig: point))
            .ToList();
        if (_rendered.Count < 2)
            return;

        var holdByDate = _holdConfidence?.Points.ToDictionary(point => point.Date) ?? [];
        _renderedHoldConfidence = _rendered
            .Select(item => holdByDate.TryGetValue(item.Price.Date, out HoldConfidencePoint? hold) ? hold : null)
            .ToList();

        _canvas.Children.Clear();
        _cursorLine = null;
        _left = 58;
        _top = 42;
        const double right = 20;
        const double bottom = 10;
        double paneGap = _showHoldConfidence ? 24 : 8;
        _width = Math.Max(1, ActualWidth - _left - right);
        double availableHeight = Math.Max(1, ActualHeight - _top - bottom - paneGap);
        double zbtHeight = Math.Max(90, availableHeight * 0.32);
        double priceHeight = Math.Max(90, availableHeight - zbtHeight);
        double zbtTop = _top + priceHeight + paneGap;
        _fullPlotHeight = zbtTop + zbtHeight - _top;
        double slots = Math.Max(1, _rendered.Count - 1);
        double slotWidth = _width / slots;
        double X(int index) => _left + _width * index / slots;

        double priceMin = _rendered.Min(item => item.Price.Low);
        double priceMax = _rendered.Max(item => item.Price.High);
        double priceRange = Math.Max(0.01, priceMax - priceMin);
        priceMin -= priceRange * 0.04;
        priceMax += priceRange * 0.04;
        double PriceY(double value) => _top + priceHeight * (1.0 - (value - priceMin) / (priceMax - priceMin));
        double[] visibleZbt = _rendered
            .Select(item => item.Zweig.Zbt)
            .Where(double.IsFinite)
            .ToArray();
        double zbtMin = visibleZbt.Length == 0 ? 0.0 : visibleZbt.Min();
        double zbtMax = visibleZbt.Length == 0 ? 100.0 : visibleZbt.Max();
        double zbtRange = Math.Max(0.01, zbtMax - zbtMin);
        zbtMin -= zbtRange * 0.05;
        zbtMax += zbtRange * 0.05;
        double ZbtY(double value) => zbtTop + zbtHeight * (1.0 - (value - zbtMin) / (zbtMax - zbtMin));

        string mode = _series.Mode == ZweigMode.Strong ? "Strong" : "Working";
        AddText($"Zweig Breadth Thrust — {mode} {_series.TriggerLevel}/{_series.TransitionDays}/{_series.MinimumMove}",
            10, 7, 15, Color.FromRgb(32, 82, 49), FontWeights.SemiBold);
        AddText("density 30: 0–50 bars   15: 51–65   5: 66–85",
            Math.Max(390, ActualWidth - 340), 10, 10, Color.FromRgb(35, 122, 65), FontWeights.Normal);

        Brush gridBrush = new SolidColorBrush(Color.FromRgb(225, 231, 226));
        Brush axisBrush = new SolidColorBrush(Color.FromRgb(82, 92, 85));
        for (int tick = 0; tick < 5; tick++)
        {
            double ratio = tick / 4.0;
            double value = priceMax - (priceMax - priceMin) * ratio;
            double y = _top + priceHeight * ratio;
            _canvas.Children.Add(new Line { X1 = _left, X2 = _left + _width, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 0.75 });
            string label = value.ToString("N0", CultureInfo.InvariantCulture);
            AddText(label, 7, y - 7, 9, Color.FromRgb(82, 92, 85), FontWeights.Normal);
            AddText(label, _left + _width + 5, y - 7, 9, Color.FromRgb(82, 92, 85), FontWeights.Normal);
        }

        int bandStart = 0;
        while (bandStart < _rendered.Count)
        {
            int bandEnd = bandStart;
            int density = _rendered[bandStart].Zweig.Density;
            while (bandEnd + 1 < _rendered.Count && _rendered[bandEnd + 1].Zweig.Density == density)
                bandEnd++;

            if (density > 0)
            {
                var band = new Rectangle
                {
                    Width = Math.Max(1.0, X(bandEnd) - X(bandStart) + slotWidth + 0.8),
                    Height = priceHeight,
                    Fill = new SolidColorBrush(Color.FromArgb((byte)density, 0, 128, 0)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(band, X(bandStart) - slotWidth / 2);
                Canvas.SetTop(band, _top);
                _canvas.Children.Add(band);
            }

            bandStart = bandEnd + 1;
        }

        for (int index = 0; index < _rendered.Count; index++)
        {
            ZweigPoint point = _rendered[index].Zweig;
            if (point.ClassicSignal)
            {
                double x = X(index);
                _canvas.Children.Add(new Line
                {
                    X1 = x, X2 = x, Y1 = _top, Y2 = _top + priceHeight,
                    Stroke = Brushes.Red, StrokeThickness = 2.0, IsHitTestVisible = false
                });
            }
        }

        if (_showCandles)
        {
            double bodyWidth = Math.Clamp(slotWidth * 0.65, 1.0, 7.0);
            Brush risingBrush = new SolidColorBrush(Color.FromRgb(42, 145, 70));
            Brush fallingBrush = new SolidColorBrush(Color.FromRgb(185, 47, 47));
            for (int index = 0; index < _rendered.Count; index++)
            {
                PricePoint price = _rendered[index].Price;
                double x = X(index);
                bool rising = price.Close >= price.Open;
                Brush brush = rising ? risingBrush : fallingBrush;
                _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = PriceY(price.High), Y2 = PriceY(price.Low), Stroke = brush, StrokeThickness = 0.8 });
                double openY = PriceY(price.Open);
                double closeY = PriceY(price.Close);
                var body = new Rectangle
                {
                    Width = bodyWidth, Height = Math.Max(1.0, Math.Abs(closeY - openY)),
                    Fill = rising ? Brushes.White : fallingBrush, Stroke = brush, StrokeThickness = 1.0
                };
                Canvas.SetLeft(body, x - bodyWidth / 2);
                Canvas.SetTop(body, Math.Min(openY, closeY));
                _canvas.Children.Add(body);
            }
        }
        else
        {
            var priceLine = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(38, 47, 42)), StrokeThickness = 1.5, IsHitTestVisible = false };
            for (int index = 0; index < _rendered.Count; index++)
                priceLine.Points.Add(new Point(X(index), PriceY(_rendered[index].Price.Close)));
            _canvas.Children.Add(priceLine);
        }

        if (_showHoldConfidence)
        {
            for (int index = 0; index < _renderedHoldConfidence.Count; index++)
            {
                HoldConfidencePoint? hold = _renderedHoldConfidence[index];
                var ribbon = new Rectangle
                {
                    Width = Math.Max(1.0, slotWidth + 0.8),
                    Height = 14,
                    Fill = new SolidColorBrush(hold is null
                        ? Color.FromRgb(190, 190, 190)
                        : FuzzyStateChart.ColorForHoldConfidence(hold.Value)),
                    Opacity = hold is null ? 0.45 : 0.96,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ribbon, X(index) - slotWidth / 2);
                Canvas.SetTop(ribbon, _top + priceHeight + 5);
                _canvas.Children.Add(ribbon);
            }
            AddText("Hold %", 5, _top + priceHeight + 3, 9,
                Color.FromRgb(90, 92, 85), FontWeights.SemiBold);
        }

        var zbtBackground = new Rectangle
        {
            Width = _width, Height = zbtHeight, Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(205, 213, 207)), StrokeThickness = 0.8,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(zbtBackground, _left);
        Canvas.SetTop(zbtBackground, zbtTop);
        _canvas.Children.Add(zbtBackground);

        for (int tick = 0; tick <= 5; tick++)
        {
            double level = zbtMin + (zbtMax - zbtMin) * tick / 5.0;
            double y = ZbtY(level);
            _canvas.Children.Add(new Line
            {
                X1 = _left, X2 = _left + _width, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 0.65
            });
            if (Math.Abs(level - _series.TriggerLevel) > 0.05)
            {
                string label = level.ToString("F1", CultureInfo.InvariantCulture);
                AddText(label, 7, y - 7, 9, Color.FromRgb(82, 92, 85), FontWeights.Normal);
                AddText(label, _left + _width + 5, y - 7, 9, Color.FromRgb(82, 92, 85), FontWeights.Normal);
            }
        }
        if (_series.TriggerLevel >= zbtMin && _series.TriggerLevel <= zbtMax)
        {
            double triggerY = ZbtY(_series.TriggerLevel);
            _canvas.Children.Add(new Line
            {
                X1 = _left, X2 = _left + _width, Y1 = triggerY, Y2 = triggerY,
                Stroke = Brushes.Red, StrokeThickness = 1.2
            });
        }
        AddText($"Breadth Trigger Level {_series.TriggerLevel}", _left + 5, zbtTop + 3, 10, Colors.Red, FontWeights.Normal);
        AddText("ZBT NYSE", _left + 155, zbtTop + 3, 10, Colors.Black, FontWeights.SemiBold);
        var zbtLine = new Polyline { Stroke = Brushes.Black, StrokeThickness = 1.65, IsHitTestVisible = false };
        for (int index = 0; index < _rendered.Count; index++)
        {
            if (double.IsFinite(_rendered[index].Zweig.Zbt))
                zbtLine.Points.Add(new Point(X(index), ZbtY(_rendered[index].Zweig.Zbt)));
        }
        _canvas.Children.Add(zbtLine);

        DrawCursor();
    }

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_cursorPinned || _rendered.Count == 0 || _width <= 0)
            return;
        double x = Math.Clamp(e.GetPosition(_canvas).X, _left, _left + _width);
        int index = Math.Clamp((int)Math.Round((x - _left) / _width * (_rendered.Count - 1)), 0, _rendered.Count - 1);
        _cursorDate = _rendered[index].Zweig.Date;
        DrawCursor();
        CursorDateChanged?.Invoke(_cursorDate.Value);
    }

    private void DrawCursor()
    {
        if (_cursorLine is not null)
            _canvas.Children.Remove(_cursorLine);
        if (!_cursorDate.HasValue || _rendered.Count == 0 || _width <= 0)
            return;
        int index = FindNearestIndex(_rendered, _cursorDate.Value);
        double x = _left + _width * index / Math.Max(1, _rendered.Count - 1);
        _cursorLine = new Line
        {
            X1 = x, X2 = x, Y1 = _top, Y2 = _top + _fullPlotHeight,
            Stroke = ChartCursorStyle.Stroke(_cursorPinned, new SolidColorBrush(Color.FromRgb(50, 63, 54))),
            StrokeThickness = ChartCursorStyle.Thickness(_cursorPinned, 1.1),
            Opacity = ChartCursorStyle.Opacity(_cursorPinned, 0.85), IsHitTestVisible = false
        };
        Panel.SetZIndex(_cursorLine, 1000);
        _canvas.Children.Add(_cursorLine);
    }

    private static int FindNearestIndex(
        IReadOnlyList<(PricePoint Price, ZweigPoint Zweig)> points,
        DateTime date)
    {
        int low = 0;
        int high = points.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            DateTime candidate = points[middle].Zweig.Date;
            if (candidate == date) return middle;
            if (candidate < date) low = middle + 1; else high = middle - 1;
        }
        if (low <= 0) return 0;
        if (low >= points.Count) return points.Count - 1;
        return Math.Abs((points[low - 1].Zweig.Date - date).Ticks) <=
               Math.Abs((points[low].Zweig.Date - date).Ticks) ? low - 1 : low;
    }

    private void AddText(string text, double x, double y, double size, Color color, FontWeight weight)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, Foreground = new SolidColorBrush(color),
            FontWeight = weight, IsHitTestVisible = false
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        _canvas.Children.Add(block);
    }
}
