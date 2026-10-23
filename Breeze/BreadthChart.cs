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
    private PriceSeries? _leftPriceOverlaySeries;
    private bool _showLeftPriceOverlay;
    private bool _showPriceSeries;
    private bool _priceOnly;
    private bool _showPriceCandles;
    private bool _showBreadthCandles;
    private bool _useLogBreadthScale;
    private bool _autoScaleBreadth;
    private bool _showMarketStates;
    private FuzzyMarketStateSeries? _marketStates;
    private VixCurveStateSeries? _vixCurveStates;
    private bool _showVixCurveRibbon;
    private bool _showSvxyRecommendationRibbon;
    private TechnicalIndicatorSeries? _technicalIndicators;
    private bool _showEmaOverlays;
    private bool _showDateLabels = true;
    private IReadOnlyList<(BreadthSeries Series, Color Color)> _overlayBreadthSeries = [];
    private string? _titleOverride;
    private Color _primaryBreadthColor = Color.FromRgb(25, 112, 174);
    private DateTime? _viewportStart;
    private DateTime? _viewportEnd;
    private IReadOnlyList<BreadthPoint> _renderedPoints = [];
    private DateTime? _cursorDate;
    private Line? _cursorLine;
    private bool _cursorPinned;
    private double _plotLeft;
    private double _plotWidth;
    private double _plotTop;
    private double _plotHeight;
    private double _horizontalSlots = 1;

    public event Action<DateTime>? CursorDateChanged;

    public BreadthChart()
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

    public void SetSeries(BreadthSeries series)
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

    public void SetPriceSeries(PriceSeries? series)
    {
        _priceSeries = series;
        if (IsVisible)
            RenderSeries();
    }

    public void SetLeftPriceOverlay(PriceSeries? series, bool show)
    {
        _leftPriceOverlaySeries = series;
        _showLeftPriceOverlay = show;
        if (IsVisible)
            RenderSeries();
    }

    public void SetShowPriceSeries(bool show)
    {
        _showPriceSeries = _priceOnly || show;
        if (IsVisible)
            RenderSeries();
    }

    public void SetPriceOnly(bool priceOnly)
    {
        _priceOnly = priceOnly;
        if (priceOnly)
        {
            _showPriceSeries = true;
            Background = new SolidColorBrush(Color.FromRgb(255, 252, 244));
        }
        if (IsVisible)
            RenderSeries();
    }

    public void SetOverlayBreadthSeries(params (BreadthSeries Series, Color Color)[] overlays)
    {
        _overlayBreadthSeries = overlays;
        if (IsVisible)
            RenderSeries();
    }

    public void SetTitle(string? title)
    {
        _titleOverride = title;
        if (IsVisible)
            RenderSeries();
    }

    public void SetPrimaryBreadthColor(Color color)
    {
        _primaryBreadthColor = color;
        if (IsVisible)
            RenderSeries();
    }

    public void SetShowPriceCandles(bool show)
    {
        _showPriceCandles = show;
        if (IsVisible)
            RenderSeries();
    }

    public void SetShowBreadthCandles(bool show)
    {
        _showBreadthCandles = show;
        if (IsVisible)
            RenderSeries();
    }

    public void SetLogBreadthScale(bool useLogarithmicScale)
    {
        _useLogBreadthScale = useLogarithmicScale;
        if (IsVisible)
            RenderSeries();
    }

    public void SetAutoScaleBreadth(bool autoScale)
    {
        _autoScaleBreadth = autoScale;
        if (IsVisible)
            RenderSeries();
    }

    public void SetMarketStates(FuzzyMarketStateSeries? states)
    {
        _marketStates = states;
        if (IsVisible)
            RenderSeries();
    }

    public void SetTechnicalIndicators(TechnicalIndicatorSeries? indicators)
    {
        _technicalIndicators = indicators;
        if (IsVisible)
            RenderSeries();
    }

    public void SetVixCurveStates(
        VixCurveStateSeries? states,
        bool showVixRibbon = true,
        bool showSvxyRibbon = false)
    {
        _vixCurveStates = states;
        _showVixCurveRibbon = showVixRibbon;
        _showSvxyRecommendationRibbon = showSvxyRibbon;
        if (IsVisible)
            RenderSeries();
    }

    public void SetShowEmaOverlays(bool show)
    {
        _showEmaOverlays = show;
        if (IsVisible)
            RenderSeries();
    }

    public void SetShowDateLabels(bool show)
    {
        _showDateLabels = show;
        if (IsVisible)
            RenderSeries();
    }

    public void SetShowMarketStates(bool show)
    {
        _showMarketStates = show;
        if (IsVisible)
            RenderSeries();
    }

    public void Refresh() => RenderSeries();

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

    public void SetMessage(string title, string message)
    {
        _series = null;
        _renderedPoints = [];
        _cursorLine = null;
        _canvas.Children.Clear();
        AddText(title, 12, 8, 15, new SolidColorBrush(Color.FromRgb(37, 48, 61)), FontWeights.SemiBold);
        AddText(message, 12, 36, 13, new SolidColorBrush(Color.FromRgb(95, 97, 100)), FontWeights.Normal);
    }

    private void RenderSeries()
    {
        if (_series is null || ActualWidth < 80 || ActualHeight < 80)
            return;

        _renderedPoints = [];
        _cursorLine = null;
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

        IReadOnlyList<PricePoint> visibleLeftOverlayPoints = _priceOnly && _showLeftPriceOverlay &&
                                                              _leftPriceOverlaySeries is not null
            ? _leftPriceOverlaySeries.Points
                .Where(point => point.Date >= visiblePoints[0].Date && point.Date <= visiblePoints[^1].Date)
                .ToList()
            : [];
        if (visibleLeftOverlayPoints.Count > 0)
        {
            var breadthDates = visiblePoints.Select(point => point.Date).ToHashSet();
            visibleLeftOverlayPoints = visibleLeftOverlayPoints
                .Where(point => breadthDates.Contains(point.Date))
                .ToList();
        }
        bool showLeftOverlay = visibleLeftOverlayPoints.Count > 1;

        double left = _priceOnly ? (showLeftOverlay ? 47 : 12) : showPrice ? 58 : 12;
        const double top = 34;
        const double right = 46;
        int ribbonRows = (_showVixCurveRibbon ? 1 : 0) + (_showSvxyRecommendationRibbon ? 1 : 0);
        double bottom = (_showDateLabels ? 24 : 10) + (ribbonRows > 0 ? 2 + 14 * ribbonRows : 0);
        double width = Math.Max(1, ActualWidth - left - right);
        double height = Math.Max(1, ActualHeight - top - bottom);
        double rightAxisX = left + width;
        Brush darkBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        Brush axisBrush = new SolidColorBrush(Color.FromRgb(103, 105, 107));
        (double breadthMinimum, double breadthMaximum) = GetBreadthScaleBounds(visiblePoints);

        AddText(_titleOverride ?? (_priceOnly ? "S&P 500 (SPY)" : _series.Name), 10, 6, 14,
            new SolidColorBrush(Color.FromRgb(37, 48, 61)), FontWeights.SemiBold);
        if (!_priceOnly)
        {
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
        }

        int count = visiblePoints.Count;
        double horizontalSlots = Math.Max(1, count - 1 + RightPaddingSlots);
        _renderedPoints = visiblePoints;
        _plotLeft = left;
        _plotWidth = width;
        _plotTop = top;
        _plotHeight = height + 14 * ribbonRows;
        _horizontalSlots = horizontalSlots;
        double BreadthY(double value) => top + height * (1.0 - BreadthScaleRatio(
            value, breadthMinimum, breadthMaximum));
        double BreadthX(int index) => left + width * index / horizontalSlots;
        if (!_priceOnly)
        {
            DrawMarketStateBackground(visiblePoints, BreadthX, top, height, width / horizontalSlots);
            if (_showBreadthCandles && _series.HasOhlc)
            {
                Brush candleBrush = Brushes.Black;
                Brush hollowFill = new SolidColorBrush(Color.FromRgb(255, 252, 244));
                double bodyWidth = ChartDrawingMetrics.CandleBodyWidth(width / horizontalSlots);
                for (int index = 0; index < count; index++)
                {
                    BreadthPoint point = visiblePoints[index];
                    double x = BreadthX(index);
                    _canvas.Children.Add(new Line
                    {
                        X1 = x,
                        X2 = x,
                        Y1 = BreadthY(point.High),
                        Y2 = BreadthY(point.Low),
                        Stroke = candleBrush,
                        StrokeThickness = 0.9
                    });
                    double openY = BreadthY(point.Open);
                    double closeY = BreadthY(point.Close);
                    bool rising = point.Close >= point.Open;
                    var body = new Rectangle
                    {
                        Width = bodyWidth,
                        Height = Math.Max(1, Math.Abs(closeY - openY)),
                        Fill = rising ? hollowFill : candleBrush,
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
                var breadthLine = new Polyline
                {
                    Stroke = new SolidColorBrush(_primaryBreadthColor),
                    StrokeThickness = 1.7
                };
                for (int index = 0; index < count; index++)
                    breadthLine.Points.Add(new Point(BreadthX(index), BreadthY(visiblePoints[index].Close)));
                _canvas.Children.Add(breadthLine);
            }

            if (_overlayBreadthSeries.Count > 0)
            {
                var breadthIndexByDate = visiblePoints
                    .Select((point, index) => (point.Date, index))
                    .ToDictionary(item => item.Date, item => item.index);
                DrawBreadthOverlayLines(
                    visiblePoints,
                    breadthIndexByDate,
                    left,
                    top,
                    width,
                    height,
                    horizontalSlots,
                    breadthMinimum,
                    breadthMaximum);
            }
        }

        if (showPrice)
        {
            double priceMin = visiblePricePoints.Min(point => point.Low);
            double priceMax = visiblePricePoints.Max(point => point.High);
            if (_showEmaOverlays && _technicalIndicators is not null)
            {
                DateTime priceStart = visiblePricePoints[0].Date;
                DateTime priceEnd = visiblePricePoints[^1].Date;
                double[] emaValues = _technicalIndicators.Points
                    .Where(point => point.Date >= priceStart && point.Date <= priceEnd)
                    .SelectMany(point => new[] { point.Ema20, point.Ema50, point.Ema100, point.Ema200 })
                    .Where(double.IsFinite)
                    .ToArray();
                if (emaValues.Length > 0)
                {
                    priceMin = Math.Min(priceMin, emaValues.Min());
                    priceMax = Math.Max(priceMax, emaValues.Max());
                }
            }
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

            if (_priceOnly)
            {
                Brush priceGridBrush = new SolidColorBrush(Color.FromRgb(224, 216, 196));
                for (int section = 1; section <= 2; section++)
                {
                    double y = top + height * section / 3.0;
                    _canvas.Children.Add(new Line
                    {
                        X1 = left,
                        X2 = rightAxisX,
                        Y1 = y,
                        Y2 = y,
                        Stroke = priceGridBrush,
                        StrokeThickness = 0.9,
                        IsHitTestVisible = false
                    });
                }
            }

            if (_showPriceCandles)
            {
                double bodyWidth = ChartDrawingMetrics.CandleBodyWidth(width / horizontalSlots);
                Brush risingBrush = new SolidColorBrush(Color.FromRgb(40, 164, 64));
                Brush fallingBrush = new SolidColorBrush(Color.FromRgb(183, 52, 45));
                Brush hollowFill = new SolidColorBrush(Color.FromRgb(255, 252, 244));
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

            if (_showEmaOverlays && _technicalIndicators is not null)
                DrawEmaOverlays(visiblePoints, breadthIndexByDate, PriceX, PriceY);

            if (_priceOnly && _overlayBreadthSeries.Count > 0)
                DrawBreadthOverlays(visiblePoints, breadthIndexByDate, left, top, width, height, horizontalSlots);

            if (_priceOnly && showLeftOverlay)
            {
                double vixMin = _showPriceCandles
                    ? visibleLeftOverlayPoints.Min(point => point.Low)
                    : visibleLeftOverlayPoints.Min(point => point.Close);
                double vixMax = _showPriceCandles
                    ? visibleLeftOverlayPoints.Max(point => point.High)
                    : visibleLeftOverlayPoints.Max(point => point.Close);
                double vixRange = vixMax - vixMin;
                if (vixRange < 0.000001)
                    vixRange = Math.Max(1.0, Math.Abs(vixMax) * 0.05);
                double vixPadding = vixRange * 0.05;
                vixMin -= vixPadding;
                vixMax += vixPadding;
                double VixY(double value) => top + height * (vixMax - value) / (vixMax - vixMin);

                Brush vixBrush = new SolidColorBrush(Color.FromRgb(92, 76, 153));
                if (_showPriceCandles)
                {
                    double bodyWidth = ChartDrawingMetrics.CandleBodyWidth(width / horizontalSlots);
                    Brush hollowFill = new SolidColorBrush(Color.FromArgb(205, 255, 252, 244));
                    foreach (PricePoint point in visibleLeftOverlayPoints)
                    {
                        double x = PriceX(point.Date);
                        _canvas.Children.Add(new Line
                        {
                            X1 = x,
                            X2 = x,
                            Y1 = VixY(point.High),
                            Y2 = VixY(point.Low),
                            Stroke = vixBrush,
                            StrokeThickness = 0.9,
                            Opacity = 0.90,
                            IsHitTestVisible = false
                        });
                        double openY = VixY(point.Open);
                        double closeY = VixY(point.Close);
                        bool rising = point.Close >= point.Open;
                        var body = new Rectangle
                        {
                            Width = bodyWidth,
                            Height = Math.Max(1.0, Math.Abs(closeY - openY)),
                            Fill = rising ? hollowFill : vixBrush,
                            Stroke = vixBrush,
                            StrokeThickness = 1.0,
                            Opacity = 0.90,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(body, x - bodyWidth / 2.0);
                        Canvas.SetTop(body, Math.Min(openY, closeY));
                        _canvas.Children.Add(body);
                    }
                }
                else
                {
                    var vixLine = new Polyline
                    {
                        Stroke = vixBrush,
                        StrokeThickness = 1.55,
                        Opacity = 0.94,
                        IsHitTestVisible = false
                    };
                    foreach (PricePoint point in visibleLeftOverlayPoints)
                        vixLine.Points.Add(new Point(PriceX(point.Date), VixY(point.Close)));
                    _canvas.Children.Add(vixLine);
                }

                _canvas.Children.Add(new Line
                {
                    X1 = left,
                    X2 = left,
                    Y1 = top,
                    Y2 = top + height,
                    Stroke = vixBrush,
                    StrokeThickness = 0.9,
                    IsHitTestVisible = false
                });
                AddText(vixMax.ToString("N1", CultureInfo.InvariantCulture), 4, top - 7, 10, vixBrush, FontWeights.Normal);
                AddText(((vixMin + vixMax) / 2.0).ToString("N1", CultureInfo.InvariantCulture),
                    4, top + height / 2.0 - 7, 10, vixBrush, FontWeights.Normal);
                AddText(vixMin.ToString("N1", CultureInfo.InvariantCulture),
                    4, top + height - 12, 10, vixBrush, FontWeights.Normal);
                AddText("VIX", left + 5, top + 2, 10, vixBrush, FontWeights.SemiBold);
            }

            double priceAxisX = _priceOnly ? rightAxisX : left;
            _canvas.Children.Add(new Line
            {
                X1 = priceAxisX,
                X2 = priceAxisX,
                Y1 = top,
                Y2 = top + height,
                Stroke = darkBrush,
                StrokeThickness = 0.9
            });
            if (_priceOnly)
            {
                double labelX = rightAxisX + 5;
                AddText(priceMax.ToString("N0", CultureInfo.InvariantCulture), labelX, top - 7, 10, darkBrush, FontWeights.Normal);
                AddText((priceMin + (priceMax - priceMin) * 2.0 / 3.0).ToString("N0", CultureInfo.InvariantCulture),
                    labelX, top + height / 3.0 - 7, 10, darkBrush, FontWeights.Normal);
                AddText((priceMin + (priceMax - priceMin) / 3.0).ToString("N0", CultureInfo.InvariantCulture),
                    labelX, top + height * 2.0 / 3.0 - 7, 10, darkBrush, FontWeights.Normal);
                AddText(priceMin.ToString("N0", CultureInfo.InvariantCulture), labelX, top + height - 12, 10, darkBrush, FontWeights.Normal);
            }
            else
            {
                AddText(priceMax.ToString("N0", CultureInfo.InvariantCulture), 5, top - 7, 10, darkBrush, FontWeights.Normal);
                AddText(((priceMin + priceMax) / 2).ToString("N0", CultureInfo.InvariantCulture), 5, top + height / 2 - 7, 10, darkBrush, FontWeights.Normal);
                AddText(priceMin.ToString("N0", CultureInfo.InvariantCulture), 5, top + height - 12, 10, darkBrush, FontWeights.Normal);
            }
        }

        if (ribbonRows > 0)
            DrawConfiguredRibbons(visiblePoints, BreadthX, top + height + 3, width / horizontalSlots);

        BreadthPoint last = visiblePoints[^1];
        if (_showDateLabels)
        {
            BreadthPoint first = visiblePoints[0];
            double dateTop = top + height + (ribbonRows > 0 ? 4 + 14 * ribbonRows : 3);
            AddText(first.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture), left, dateTop, 10, axisBrush, FontWeights.Normal);
            double lastDataX = left + width * (count - 1) / horizontalSlots;
            AddText(last.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture), Math.Max(left, lastDataX - 46), dateTop, 10, axisBrush, FontWeights.Normal);
        }
        if (_priceOnly && visiblePricePoints.Count > 0)
            AddText($"Last: {visiblePricePoints[^1].Close:N2}", Math.Max(left + 120, ActualWidth - 155), 7, 11,
                new SolidColorBrush(Color.FromRgb(75, 78, 82)), FontWeights.Normal);
        else
            AddText($"Last: {last.Value:F1}%  (n={last.EligibleCount})", Math.Max(left + 120, ActualWidth - 215), 7, 11,
                new SolidColorBrush(Color.FromRgb(75, 78, 82)), FontWeights.Normal);
        DrawCursorLine();
    }

    private void DrawConfiguredRibbons(
        IReadOnlyList<BreadthPoint> points,
        Func<int, double> xForIndex,
        double top,
        double slotWidth)
    {
        var statesByDate = _vixCurveStates?.Points.ToDictionary(point => point.Date) ?? [];
        int ribbonRow = 0;
        if (_showVixCurveRibbon)
        {
            for (int index = 0; index < points.Count; index++)
            {
                VixCurveState state = statesByDate.TryGetValue(points[index].Date, out VixCurveStatePoint? point)
                    ? point.State
                    : VixCurveState.Unknown;
                var ribbon = new Rectangle
                {
                    Width = Math.Max(1.0, slotWidth + 0.8),
                    Height = 11,
                    Fill = new SolidColorBrush(FuzzyStateChart.ColorForCurveState(state)),
                    Opacity = state == VixCurveState.Unknown ? 0.40 : 0.94,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ribbon, xForIndex(index) - slotWidth / 2.0);
                Canvas.SetTop(ribbon, top + 14 * ribbonRow);
                _canvas.Children.Add(ribbon);
            }
            ribbonRow++;
        }

        if (_showSvxyRecommendationRibbon)
        {
            for (int index = 0; index < points.Count; index++)
            {
                SvxyRecommendation recommendation = statesByDate.TryGetValue(
                    points[index].Date, out VixCurveStatePoint? svxyPoint)
                    ? svxyPoint.SvxyRecommendation
                    : SvxyRecommendation.Neutral;
                var svxyRibbon = new Rectangle
                {
                    Width = Math.Max(1.0, slotWidth + 0.8),
                    Height = 11,
                    Fill = new SolidColorBrush(FuzzyStateChart.ColorForSvxyRecommendation(recommendation)),
                    Opacity = 0.95,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(svxyRibbon, xForIndex(index) - slotWidth / 2.0);
                Canvas.SetTop(svxyRibbon, top + 14 * ribbonRow);
                _canvas.Children.Add(svxyRibbon);
            }
        }
    }

    private void DrawBreadthOverlays(
        IReadOnlyList<BreadthPoint> masterPoints,
        IReadOnlyDictionary<DateTime, int> masterIndexByDate,
        double left,
        double top,
        double width,
        double height,
        double horizontalSlots)
    {
        DateTime start = masterPoints[0].Date;
        DateTime end = masterPoints[^1].Date;
        var visibleOverlays = _overlayBreadthSeries
            .Select(overlay => (
                overlay.Color,
                Points: (IReadOnlyList<BreadthPoint>)overlay.Series.Points
                    .Where(point => point.Date >= start && point.Date <= end && masterIndexByDate.ContainsKey(point.Date))
                    .ToList()))
            .Where(overlay => overlay.Points.Count > 0)
            .ToList();
        if (visibleOverlays.Count == 0)
            return;

        double minimum = _showBreadthCandles
            ? visibleOverlays.Min(overlay => overlay.Points.Min(point => point.Low))
            : visibleOverlays.Min(overlay => overlay.Points.Min(point => point.Value));
        double maximum = _showBreadthCandles
            ? visibleOverlays.Max(overlay => overlay.Points.Max(point => point.High))
            : visibleOverlays.Max(overlay => overlay.Points.Max(point => point.Value));
        double range = maximum - minimum;
        if (range < 0.000001)
            range = Math.Max(1.0, Math.Abs(maximum) * 0.05);
        double padding = range * 0.05;
        minimum -= padding;
        maximum += padding;

        double X(DateTime date) => left + width * masterIndexByDate[date] / horizontalSlots;
        double Y(double value) => top + height * (maximum - value) / (maximum - minimum);
        foreach ((Color color, IReadOnlyList<BreadthPoint> points) in visibleOverlays)
        {
            if (_showBreadthCandles)
            {
                Brush candleBrush = new SolidColorBrush(color);
                Brush hollowFill = new SolidColorBrush(Color.FromRgb(255, 252, 244));
                double bodyWidth = ChartDrawingMetrics.CandleBodyWidth(width / horizontalSlots);
                foreach (BreadthPoint point in points)
                {
                    double x = X(point.Date);
                    _canvas.Children.Add(new Line
                    {
                        X1 = x,
                        X2 = x,
                        Y1 = Y(point.High),
                        Y2 = Y(point.Low),
                        Stroke = candleBrush,
                        StrokeThickness = 0.9,
                        Opacity = 0.9,
                        IsHitTestVisible = false
                    });
                    double openY = Y(point.Open);
                    double closeY = Y(point.Close);
                    bool rising = point.Close >= point.Open;
                    var body = new Rectangle
                    {
                        Width = bodyWidth,
                        Height = Math.Max(1.0, Math.Abs(closeY - openY)),
                        Fill = rising ? hollowFill : candleBrush,
                        Stroke = candleBrush,
                        StrokeThickness = 1.0,
                        Opacity = 0.9,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(body, x - bodyWidth / 2.0);
                    Canvas.SetTop(body, Math.Min(openY, closeY));
                    _canvas.Children.Add(body);
                }
                continue;
            }

            var line = new Polyline
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1.7,
                Opacity = 0.92,
                IsHitTestVisible = false
            };
            foreach (BreadthPoint point in points)
                line.Points.Add(new Point(X(point.Date), Y(point.Value)));
            _canvas.Children.Add(line);
        }
    }

    private void DrawEmaOverlays(
        IReadOnlyList<BreadthPoint> masterPoints,
        IReadOnlyDictionary<DateTime, int> masterIndexByDate,
        Func<DateTime, double> x,
        Func<double, double> y)
    {
        if (_technicalIndicators is null)
            return;

        DateTime start = masterPoints[0].Date;
        DateTime end = masterPoints[^1].Date;
        IReadOnlyList<TechnicalIndicatorPoint> points = _technicalIndicators.Points
            .Where(point => point.Date >= start && point.Date <= end && masterIndexByDate.ContainsKey(point.Date))
            .ToList();
        if (points.Count == 0)
            return;

        DrawEmaLine(points, point => point.Ema20, x, y, Color.FromRgb(30, 112, 191), 1.35);
        DrawEmaLine(points, point => point.Ema50, x, y, Color.FromRgb(221, 126, 28), 1.35);
        DrawEmaLine(points, point => point.Ema100, x, y, Color.FromRgb(130, 76, 170), 1.45);
        DrawEmaLine(points, point => point.Ema200, x, y, Color.FromRgb(31, 128, 81), 1.7);
    }

    private void DrawEmaLine(
        IEnumerable<TechnicalIndicatorPoint> points,
        Func<TechnicalIndicatorPoint, double> selector,
        Func<DateTime, double> x,
        Func<double, double> y,
        Color color,
        double thickness)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        foreach (TechnicalIndicatorPoint point in points)
        {
            double value = selector(point);
            if (double.IsFinite(value))
                line.Points.Add(new Point(x(point.Date), y(value)));
        }
        _canvas.Children.Add(line);
    }

    private void DrawBreadthOverlayLines(
        IReadOnlyList<BreadthPoint> masterPoints,
        IReadOnlyDictionary<DateTime, int> masterIndexByDate,
        double left,
        double top,
        double width,
        double height,
        double horizontalSlots,
        double minimum,
        double maximum)
    {
        DateTime start = masterPoints[0].Date;
        DateTime end = masterPoints[^1].Date;
        double X(DateTime date) => left + width * masterIndexByDate[date] / horizontalSlots;
        double Y(double value) => top + height * (1.0 - BreadthScaleRatio(value, minimum, maximum));
        foreach ((BreadthSeries series, Color color) in _overlayBreadthSeries)
        {
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1.7,
                Opacity = 0.92,
                IsHitTestVisible = false
            };
            foreach (BreadthPoint point in series.Points)
            {
                if (point.Date >= start && point.Date <= end && masterIndexByDate.ContainsKey(point.Date))
                    line.Points.Add(new Point(X(point.Date), Y(point.Value)));
            }
            _canvas.Children.Add(line);
        }
    }

    private void DrawMarketStateBackground(
        IReadOnlyList<BreadthPoint> points,
        Func<int, double> xForIndex,
        double top,
        double height,
        double slotWidth)
    {
        if (!_showMarketStates || _marketStates is null)
            return;

        var statesByDate = _marketStates.Points.ToDictionary(point => point.Date);
        for (int index = 0; index < points.Count; index++)
        {
            if (!statesByDate.TryGetValue(points[index].Date, out FuzzyMarketStatePoint? state) ||
                state.DominantZone == MarketZone.Neutral)
                continue;

            var band = new Rectangle
            {
                Width = Math.Max(1.0, slotWidth + 0.8),
                Height = height,
                Fill = new SolidColorBrush(FuzzyStatePalette.ColorFor(state.DominantZone)),
                Opacity = 0.04 + 0.17 * state.Confidence,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(band, xForIndex(index) - slotWidth / 2);
            Canvas.SetTop(band, top);
            Panel.SetZIndex(band, -100);
            _canvas.Children.Add(band);
        }
    }

    private void ChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_cursorPinned || _renderedPoints.Count == 0 || _plotWidth <= 0)
            return;

        double x = e.GetPosition(_canvas).X;
        double relative = Math.Clamp((x - _plotLeft) / _plotWidth, 0, 1);
        int index = (int)Math.Round(relative * _horizontalSlots);
        index = Math.Clamp(index, 0, _renderedPoints.Count - 1);
        CursorDateChanged?.Invoke(_renderedPoints[index].Date);
    }

    private void DrawCursorLine()
    {
        if (_cursorLine is not null)
        {
            _canvas.Children.Remove(_cursorLine);
            _cursorLine = null;
        }

        if (!_cursorDate.HasValue || _renderedPoints.Count == 0 || _plotWidth <= 0 || _plotHeight <= 0)
            return;

        int index = FindNearestIndex(_renderedPoints, _cursorDate.Value, point => point.Date);
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

    private static int FindNearestIndex<T>(IReadOnlyList<T> points, DateTime date, Func<T, DateTime> selector)
    {
        int low = 0;
        int high = points.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            DateTime candidate = selector(points[middle]);
            if (candidate == date) return middle;
            if (candidate < date) low = middle + 1; else high = middle - 1;
        }
        if (low <= 0) return 0;
        if (low >= points.Count) return points.Count - 1;
        return Math.Abs((selector(points[low - 1]) - date).Ticks) <= Math.Abs((selector(points[low]) - date).Ticks)
            ? low - 1 : low;
    }

    private (double Minimum, double Maximum) GetBreadthScaleBounds(IReadOnlyList<BreadthPoint> points)
    {
        if (!_autoScaleBreadth)
            return _useLogBreadthScale ? (10.0, 100.0) : (0.0, 100.0);

        double dataMinimum = _showBreadthCandles && _series?.HasOhlc == true
            ? points.Min(point => point.Low)
            : points.Min(point => point.Value);
        double dataMaximum = _showBreadthCandles && _series?.HasOhlc == true
            ? points.Max(point => point.High)
            : points.Max(point => point.Value);
        if (_overlayBreadthSeries.Count > 0)
        {
            DateTime start = points[0].Date;
            DateTime end = points[^1].Date;
            var masterDates = points.Select(point => point.Date).ToHashSet();
            var overlayPoints = _overlayBreadthSeries
                .SelectMany(overlay => overlay.Series.Points)
                .Where(point => point.Date >= start && point.Date <= end && masterDates.Contains(point.Date))
                .ToList();
            if (overlayPoints.Count > 0)
            {
                dataMinimum = Math.Min(dataMinimum, overlayPoints.Min(point => point.Value));
                dataMaximum = Math.Max(dataMaximum, overlayPoints.Max(point => point.Value));
            }
        }
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

            double logPadding = (logMaximum - logMinimum) * 0.05;
            double minimum = Math.Max(positiveFloor, Math.Pow(10.0, logMinimum - logPadding));
            double maximum = Math.Min(100.0, Math.Pow(10.0, logMaximum + logPadding));
            return (minimum, Math.Max(minimum * 1.01, maximum));
        }

        double range = dataMaximum - dataMinimum;
        if (range < 0.1)
            range = Math.Max(1.0, Math.Abs(dataMaximum) * 0.05);
        double linearPadding = range * 0.05;
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
