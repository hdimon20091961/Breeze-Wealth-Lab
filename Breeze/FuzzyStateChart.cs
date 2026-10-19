using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal sealed class FuzzyStateChart : Border
{
    private const int RightPaddingSlots = 2;
    private readonly Canvas _canvas = new();
    private PriceSeries? _priceSeries;
    private FuzzyMarketStateSeries? _states;
    private HoldConfidenceSeries? _holdConfidence;
    private VixCurveStateSeries? _contangoStates;
    private TechnicalIndicatorSeries? _technicalIndicators;
    private bool _showCandles;
    private bool _showHoldConfidence = true;
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private IReadOnlyList<FuzzyMarketStatePoint> _renderedStates = [];
    private IReadOnlyList<HoldConfidencePoint> _renderedHoldConfidence = [];
    private IReadOnlyList<TechnicalIndicatorPoint> _renderedIndicators = [];
    private IReadOnlyList<VixCurveStatePoint> _renderedContangoStates = [];
    private DateTime? _cursorDate;
    private Line? _cursorLine;
    private bool _cursorPinned;
    private double _plotLeft;
    private double _plotWidth;
    private double _plotTop;
    private double _plotHeight;
    private double _horizontalSlots = 1;

    public event Action<DateTime>? CursorDateChanged;

    public FuzzyStateChart()
    {
        Background = Brushes.White;
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

    public void SetData(
        PriceSeries? priceSeries,
        FuzzyMarketStateSeries? states,
        TechnicalIndicatorSeries? technicalIndicators)
    {
        _priceSeries = priceSeries;
        _states = states;
        _technicalIndicators = technicalIndicators;
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

    public void SetContangoStates(VixCurveStateSeries? states)
    {
        _contangoStates = states;
        if (IsVisible)
            RenderSeries();
    }

    public void SetHoldConfidence(HoldConfidenceSeries? series)
    {
        _holdConfidence = series;
        if (IsVisible)
            RenderSeries();
    }

    public void SetShowHoldConfidence(bool show)
    {
        _showHoldConfidence = show;
        if (IsVisible)
            RenderSeries();
    }

    public void SetShowCandles(bool showCandles)
    {
        _showCandles = showCandles;
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

    public void Refresh() => RenderSeries();

    public void SetMessage(string title, string message)
    {
        _renderedStates = [];
        _renderedHoldConfidence = [];
        _renderedIndicators = [];
        _renderedContangoStates = [];
        _cursorLine = null;
        _canvas.Children.Clear();
        AddText(title, 12, 8, 15, new SolidColorBrush(Color.FromRgb(37, 48, 61)), FontWeights.SemiBold);
        AddText(message, 12, 36, 13, new SolidColorBrush(Color.FromRgb(95, 97, 100)), FontWeights.Normal);
    }

    private void RenderSeries()
    {
        if (_priceSeries is null || _states is null || _technicalIndicators is null ||
            ActualWidth < 100 || ActualHeight < 180)
            return;

        _canvas.Children.Clear();
        _cursorLine = null;
        var stateByDate = _states.Points.ToDictionary(point => point.Date);
        var indicatorByDate = _technicalIndicators.Points.ToDictionary(point => point.Date);
        var holdByDate = _holdConfidence?.Points.ToDictionary(point => point.Date) ?? [];
        var contangoByDate = _contangoStates?.Points.ToDictionary(point => point.Date) ?? [];
        IReadOnlyList<PricePoint> prices = _priceSeries.Points
            .Where(point => stateByDate.ContainsKey(point.Date))
            .Where(point => indicatorByDate.ContainsKey(point.Date))
            .Where(point => !_viewportStart.HasValue || point.Date >= _viewportStart.Value)
            .Where(point => !_viewportEnd.HasValue || point.Date <= _viewportEnd.Value)
            .ToList();
        if (prices.Count < 2)
            return;

        _renderedStates = prices.Select(point => stateByDate[point.Date]).ToList();
        _renderedIndicators = prices.Select(point => indicatorByDate[point.Date]).ToList();
        _renderedHoldConfidence = prices.Select(point =>
            holdByDate.TryGetValue(point.Date, out HoldConfidencePoint? confidence)
                ? confidence
                : new HoldConfidencePoint(
                    point.Date, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                    "Hold confidence is unavailable.")).ToList();
        _renderedContangoStates = prices.Select(point =>
            contangoByDate.TryGetValue(point.Date, out VixCurveStatePoint? state)
                ? state
                : new VixCurveStatePoint(
                    point.Date, VixCurveState.Unknown, null, null, null, null, null,
                    0.0, 0.0, 0.0, 0, 0, 0, 0.0, 0.0, 0.0, false,
                    SvxyRecommendation.Neutral, null)).ToList();
        const double left = 58;
        const double top = 38;
        const double right = 50;
        const double bottom = 30;
        double paneGap = _showHoldConfidence ? 66 : 50;
        double width = Math.Max(1, ActualWidth - left - right);
        double availableHeight = Math.Max(1, ActualHeight - top - bottom - paneGap);
        double rsiHeight = Math.Max(72, availableHeight * 0.24);
        double priceHeight = Math.Max(80, availableHeight - rsiHeight);
        double rsiTop = top + priceHeight + paneGap;
        double horizontalSlots = Math.Max(1, prices.Count - 1 + RightPaddingSlots);
        double slotWidth = width / horizontalSlots;
        double X(int index) => left + width * index / horizontalSlots;

        IEnumerable<double> emaValues = _renderedIndicators.SelectMany(point =>
            new[] { point.Ema20, point.Ema50, point.Ema100, point.Ema200 });
        double priceMin = Math.Min(prices.Min(point => point.Low), emaValues.Min());
        double priceMax = Math.Max(prices.Max(point => point.High), emaValues.Max());
        double priceRange = Math.Max(0.01, priceMax - priceMin);
        priceMin -= priceRange * 0.04;
        priceMax += priceRange * 0.04;
        double Y(double value) => top + priceHeight * (1.0 - (value - priceMin) / (priceMax - priceMin));
        double RsiY(double value) => rsiTop + rsiHeight * (1.0 - (value + 1.0) / 2.0);

        Brush ema20Brush = new SolidColorBrush(Color.FromRgb(30, 112, 191));
        Brush ema50Brush = new SolidColorBrush(Color.FromRgb(221, 126, 28));
        Brush ema100Brush = new SolidColorBrush(Color.FromRgb(130, 76, 170));
        Brush ema200Brush = new SolidColorBrush(Color.FromRgb(31, 128, 81));
        Brush myRsiBrush = new SolidColorBrush(Color.FromRgb(25, 66, 110));
        Brush myRsi10Brush = new SolidColorBrush(Color.FromRgb(196, 43, 43));

        AddText("Fuzzy market-state map", 10, 6, 15,
            new SolidColorBrush(Color.FromRgb(37, 48, 61)), FontWeights.SemiBold);
        AddText("EMA20", 190, 9, 11, ema20Brush, FontWeights.SemiBold);
        AddText("EMA50", 242, 9, 11, ema50Brush, FontWeights.SemiBold);
        const double ema100LegendX = 294;
        const double ema200LegendX = 354;
        const double emaLegendStep = ema200LegendX - ema100LegendX;
        AddText("EMA100", ema100LegendX, 9, 11, ema100Brush, FontWeights.SemiBold);
        AddText("EMA200", ema200LegendX, 9, 11, ema200Brush, FontWeights.SemiBold);
        if (_showHoldConfidence)
        {
            // Keep two EMA100-to-EMA200 intervals after EMA200, then place Hold.
            double holdLabelX = ema200LegendX + 2.0 * emaLegendStep;
            AddHoldConfidenceLegend(holdLabelX + 34.0, 7);
        }

        for (int index = 0; index < _renderedStates.Count; index++)
        {
            FuzzyMarketStatePoint state = _renderedStates[index];
            Color color = FuzzyStatePalette.ColorFor(state.DominantZone);
            double activation = state.Degree(state.DominantZone);
            if (state.DominantZone is not (MarketZone.Neutral or MarketZone.Caution) && activation > 0.0)
            {
                var background = new Rectangle
                {
                    Width = Math.Max(1.0, slotWidth + 0.8),
                    Height = priceHeight,
                    Fill = new SolidColorBrush(color),
                    Opacity = 0.05 + 0.20 * state.Confidence,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(background, X(index) - slotWidth / 2);
                Canvas.SetTop(background, top);
                _canvas.Children.Add(background);
            }

            var ribbon = new Rectangle
            {
                Width = Math.Max(1.0, slotWidth + 0.8),
                Height = 14,
                Fill = new SolidColorBrush(color),
                Opacity = state.DominantZone is MarketZone.Neutral or MarketZone.Caution
                    ? 0.42
                    : 0.40 + 0.60 * state.Confidence,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ribbon, X(index) - slotWidth / 2);
            Canvas.SetTop(ribbon, top + priceHeight + 5);
            _canvas.Children.Add(ribbon);

            if (_showHoldConfidence)
            {
                HoldConfidencePoint hold = _renderedHoldConfidence[index];
                var holdRibbon = new Rectangle
                {
                    Width = Math.Max(1.0, slotWidth + 0.8),
                    Height = 14,
                    Fill = new SolidColorBrush(ColorForHoldConfidence(hold.Value)),
                    Opacity = 0.96,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(holdRibbon, X(index) - slotWidth / 2);
                Canvas.SetTop(holdRibbon, top + priceHeight + 21);
                _canvas.Children.Add(holdRibbon);
            }

            double contangoOffset = _showHoldConfidence ? 37 : 21;
            double svxyOffset = _showHoldConfidence ? 53 : 37;

            VixCurveStatePoint curve = _renderedContangoStates[index];
            var contangoRibbon = new Rectangle
            {
                Width = Math.Max(1.0, slotWidth + 0.8),
                Height = 11,
                Fill = new SolidColorBrush(ColorForCurveState(curve.State)),
                Opacity = curve.State == VixCurveState.Unknown ? 0.40 : 0.92,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(contangoRibbon, X(index) - slotWidth / 2);
            Canvas.SetTop(contangoRibbon, top + priceHeight + contangoOffset);
            _canvas.Children.Add(contangoRibbon);

            var svxyRibbon = new Rectangle
            {
                Width = Math.Max(1.0, slotWidth + 0.8),
                Height = 11,
                Fill = new SolidColorBrush(ColorForSvxyRecommendation(curve.SvxyRecommendation)),
                Opacity = 0.95,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(svxyRibbon, X(index) - slotWidth / 2);
            Canvas.SetTop(svxyRibbon, top + priceHeight + svxyOffset);
            _canvas.Children.Add(svxyRibbon);
        }

        AddText("Phase", 8, top + priceHeight + 3, 9,
            new SolidColorBrush(Color.FromRgb(90, 92, 95)), FontWeights.Normal);
        if (_showHoldConfidence)
            AddText("Hold %", 5, top + priceHeight + 19, 9,
                new SolidColorBrush(Color.FromRgb(90, 92, 95)), FontWeights.SemiBold);
        AddText("Contango", 2, top + priceHeight + (_showHoldConfidence ? 35 : 19), 9,
            new SolidColorBrush(Color.FromRgb(90, 92, 95)), FontWeights.Normal);
        AddText("SVXY", 8, top + priceHeight + (_showHoldConfidence ? 51 : 35), 9,
            new SolidColorBrush(Color.FromRgb(90, 92, 95)), FontWeights.Normal);

        Brush gridBrush = new SolidColorBrush(Color.FromRgb(224, 216, 196));
        Brush axisBrush = new SolidColorBrush(Color.FromRgb(90, 92, 95));
        Brush darkBrush = new SolidColorBrush(Color.FromRgb(35, 35, 35));
        for (int tick = 0; tick < 5; tick++)
        {
            double ratio = tick / 4.0;
            double value = priceMin + (priceMax - priceMin) * ratio;
            double y = Y(value);
            _canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left + width,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 0.7
            });
            string label = value.ToString("N0", CultureInfo.InvariantCulture);
            AddText(label, 5, y - 8, 10, axisBrush, FontWeights.Normal);
            AddText(label, left + width + 5, y - 8, 10, axisBrush, FontWeights.Normal);
        }

        if (_showCandles)
        {
            double bodyWidth = Math.Clamp(slotWidth * 0.65, 1.0, 7.0);
            Brush risingBrush = new SolidColorBrush(Color.FromRgb(40, 142, 68));
            Brush fallingBrush = new SolidColorBrush(Color.FromRgb(177, 51, 47));
            Brush hollowFill = Brushes.White;
            for (int index = 0; index < prices.Count; index++)
            {
                PricePoint price = prices[index];
                double x = X(index);
                bool rising = price.Close >= price.Open;
                Brush candleBrush = rising ? risingBrush : fallingBrush;
                _canvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = Y(price.High),
                    Y2 = Y(price.Low),
                    Stroke = darkBrush,
                    StrokeThickness = 0.8
                });
                double openY = Y(price.Open);
                double closeY = Y(price.Close);
                var body = new Rectangle
                {
                    Width = bodyWidth,
                    Height = Math.Max(1.0, Math.Abs(closeY - openY)),
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
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(42, 48, 55)),
                StrokeThickness = 1.5
            };
            for (int index = 0; index < prices.Count; index++)
                line.Points.Add(new Point(X(index), Y(prices[index].Close)));
            _canvas.Children.Add(line);
        }

        AddSeriesLine(_renderedIndicators.Select(point => point.Ema20), X, Y, ema20Brush, 1.35);
        AddSeriesLine(_renderedIndicators.Select(point => point.Ema50), X, Y, ema50Brush, 1.35);
        AddSeriesLine(_renderedIndicators.Select(point => point.Ema100), X, Y, ema100Brush, 1.45);
        AddSeriesLine(_renderedIndicators.Select(point => point.Ema200), X, Y, ema200Brush, 1.7);

        var rsiBackground = new Rectangle
        {
            Width = width,
            Height = rsiHeight,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(205, 195, 171)),
            StrokeThickness = 0.8,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rsiBackground, left);
        Canvas.SetTop(rsiBackground, rsiTop);
        _canvas.Children.Add(rsiBackground);

        foreach (double level in new[] { -1.0, -0.5, 0.0, 0.5, 1.0 })
        {
            double y = RsiY(level);
            _canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left + width,
                Y1 = y,
                Y2 = y,
                Stroke = level == 0.0 ? new SolidColorBrush(Color.FromRgb(145, 138, 124)) : gridBrush,
                StrokeThickness = level == 0.0 ? 1.0 : 0.65,
                StrokeDashArray = Math.Abs(level) == 0.5 ? new DoubleCollection { 3, 3 } : null
            });
            string label = level.ToString("0.0", CultureInfo.InvariantCulture);
            AddText(label, 21, y - 7, 9, axisBrush, FontWeights.Normal);
            AddText(label, left + width + 5, y - 7, 9, axisBrush, FontWeights.Normal);
        }
        AddText("My RSI 14,8", left + 5, rsiTop + 3, 10, myRsiBrush, FontWeights.SemiBold);
        AddText("My RSI 10,8", left + 92, rsiTop + 3, 10, myRsi10Brush, FontWeights.SemiBold);
        AddSeriesLine(_renderedIndicators.Select(point => point.MyRsiRep), X, RsiY, myRsiBrush, 1.65);
        AddSeriesLine(_renderedIndicators.Select(point => point.MyRsiRep10), X, RsiY, myRsi10Brush, 1.45);

        _canvas.Children.Add(new Line
        {
            X1 = left,
            X2 = left,
            Y1 = top,
            Y2 = top + priceHeight,
            Stroke = darkBrush,
            StrokeThickness = 0.9
        });

        AddText(prices[0].Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            left, rsiTop + rsiHeight + 5, 10, axisBrush, FontWeights.Normal);
        double lastDataX = X(prices.Count - 1);
        AddText(prices[^1].Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Math.Max(left, lastDataX - 48), rsiTop + rsiHeight + 5, 10, axisBrush, FontWeights.Normal);
        // "Last" is the latest calculated dated state, not the right edge of the
        // movable viewport. Panning and zooming therefore cannot change it.
        FuzzyMarketStatePoint last = _states.Points.MaxBy(point => point.Date)!;
        HoldConfidencePoint? lastHold = _holdConfidence?.Points
            .FirstOrDefault(point => point.Date == last.Date);
        string lastText =
            $"Last {last.Date:yyyy-MM-dd}: {FuzzyStatePalette.Label(last.DominantZone)} {last.Confidence:P0}" +
            (_showHoldConfidence
                ? lastHold is null ? "; hold n/a" : $"; hold {lastHold.Value:F0}%"
                : string.Empty);
        AddRightAlignedText(lastText, right + 3, 27, 11, axisBrush, FontWeights.Normal);

        _plotLeft = left;
        _plotTop = top;
        _plotWidth = width;
        _plotHeight = rsiTop + rsiHeight - top;
        _horizontalSlots = horizontalSlots;
        DrawCursorLine();
    }

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_cursorPinned || _renderedStates.Count == 0 || _plotWidth <= 0)
            return;
        double x = e.GetPosition(_canvas).X;
        double relative = Math.Clamp((x - _plotLeft) / _plotWidth, 0.0, 1.0);
        int index = Math.Clamp((int)Math.Round(relative * _horizontalSlots), 0, _renderedStates.Count - 1);
        CursorDateChanged?.Invoke(_renderedStates[index].Date);
    }

    private void DrawCursorLine()
    {
        if (_cursorLine is not null)
        {
            _canvas.Children.Remove(_cursorLine);
            _cursorLine = null;
        }
        if (!_cursorDate.HasValue || _renderedStates.Count == 0 || _plotWidth <= 0)
            return;

        int index = FindNearestIndex(_renderedStates, _cursorDate.Value);
        double x = _plotLeft + _plotWidth * index / _horizontalSlots;
        _cursorLine = new Line
        {
            X1 = x,
            X2 = x,
            Y1 = _plotTop,
            Y2 = _plotTop + _plotHeight + (_showHoldConfidence ? 19 : 0),
            Stroke = ChartCursorStyle.Stroke(_cursorPinned, Brushes.Black),
            StrokeThickness = ChartCursorStyle.Thickness(_cursorPinned, 1.1),
            Opacity = ChartCursorStyle.Opacity(_cursorPinned, 0.85),
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_cursorLine, 1000);
        _canvas.Children.Add(_cursorLine);
    }

    private static int FindNearestIndex(IReadOnlyList<FuzzyMarketStatePoint> points, DateTime date)
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

    private void AddRightAlignedText(
        string text,
        double right,
        double y,
        double size,
        Brush brush,
        FontWeight weight)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight
        };
        Canvas.SetRight(block, right);
        Canvas.SetTop(block, y);
        _canvas.Children.Add(block);
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

    internal static Color ColorForCurveState(VixCurveState state) => state switch
    {
        VixCurveState.StrongContango => Color.FromRgb(24, 128, 64),
        VixCurveState.Contango => Color.FromRgb(126, 190, 118),
        VixCurveState.Mixed => Color.FromRgb(232, 151, 55),
        VixCurveState.Backwardation => Color.FromRgb(231, 139, 120),
        VixCurveState.StrongBackwardation => Color.FromRgb(196, 55, 55),
        _ => Color.FromRgb(190, 190, 190)
    };

    internal static Color ColorForSvxyRecommendation(SvxyRecommendation recommendation) => recommendation switch
    {
        SvxyRecommendation.StrongBuy => Color.FromRgb(38, 139, 196),
        SvxyRecommendation.Buy => Color.FromRgb(137, 203, 232),
        SvxyRecommendation.ExtendedHold => Color.FromRgb(211, 238, 248),
        _ => Colors.White
    };

    internal static Color ColorForHoldConfidence(double value)
    {
        double clamped = Math.Clamp(value, 0.0, 100.0);
        Color start;
        Color end;
        double t;
        if (clamped <= 50.0)
        {
            start = Color.FromRgb(196, 55, 55);
            end = Color.FromRgb(242, 201, 76);
            t = clamped / 50.0;
        }
        else
        {
            start = Color.FromRgb(242, 201, 76);
            end = Color.FromRgb(24, 128, 64);
            t = (clamped - 50.0) / 50.0;
        }

        byte Blend(byte from, byte to) => (byte)Math.Round(from + (to - from) * t);
        return Color.FromRgb(Blend(start.R, end.R), Blend(start.G, end.G), Blend(start.B, end.B));
    }

    private void AddHoldConfidenceLegend(double x, double y)
    {
        AddText("Hold", x - 34, y - 2, 9,
            new SolidColorBrush(Color.FromRgb(90, 92, 95)), FontWeights.SemiBold);
        const int segments = 20;
        const double width = 140.0;
        for (int index = 0; index < segments; index++)
        {
            double value = 100.0 * index / (segments - 1);
            var sample = new Rectangle
            {
                Width = width / segments + 0.5,
                Height = 9,
                Fill = new SolidColorBrush(ColorForHoldConfidence(value)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(sample, x + width * index / segments);
            Canvas.SetTop(sample, y);
            _canvas.Children.Add(sample);
        }
        AddText("0", x, y + 9, 8, Brushes.DimGray, FontWeights.Normal);
        AddText("50", x + width / 2 - 7, y + 9, 8, Brushes.DimGray, FontWeights.Normal);
        AddText("100%", x + width - 19, y + 9, 8, Brushes.DimGray, FontWeights.Normal);
    }
}
