using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BreezeVL;

internal sealed class ContangoChart : Border
{
    private const int MaximumDisplayedDays = 180;
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(37, 48, 61));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(95, 97, 100));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(35, 164, 235));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(220, 224, 226));
    private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromArgb(82, 255, 244, 105));
    private readonly Canvas _canvas = new();
    private VixTermSnapshot? _snapshot;
    private string _message = "Open the Contango tab to load VIX futures data";

    public ContangoChart()
    {
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(205, 195, 171));
        BorderThickness = new Thickness(1);
        Margin = new Thickness(4);
        Padding = new Thickness(2);
        Child = _canvas;
        SizeChanged += (_, _) => RenderChart();
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(RenderChart)); };
    }

    public void SetMessage(string message)
    {
        _snapshot = null;
        _message = message;
        if (IsVisible)
            RenderChart();
    }

    public void SetSnapshot(VixTermSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (IsVisible)
            RenderChart();
    }

    public void Refresh() => RenderChart();

    private void RenderChart()
    {
        _canvas.Children.Clear();
        if (ActualWidth < 160 || ActualHeight < 140)
            return;

        if (_snapshot is null)
        {
            AddText("VIX futures term structure", 14, 10, 13, TextBrush, FontWeights.SemiBold);
            AddText(_message, 14, 40, 13, MutedBrush, FontWeights.Normal);
            return;
        }

        IReadOnlyList<VixTermPoint> points = _snapshot.Points
            .Where(point => point.DaysToExpiration <= MaximumDisplayedDays)
            .ToList();
        if (points.Count == 0)
        {
            AddText("VIX futures term structure", 14, 10, 13, TextBrush, FontWeights.SemiBold);
            AddText("No contracts with 180 days or less to expiration", 14, 40, 13, MutedBrush, FontWeights.Normal);
            return;
        }
        string dateSuffix = _snapshot.RequestedDate == _snapshot.TradeDate
            ? _snapshot.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : $"{_snapshot.TradeDate:yyyy-MM-dd} (nearest to {_snapshot.RequestedDate:yyyy-MM-dd})";
        AddText($"VIX futures term structure   {dateSuffix}", 14, 7, 13, TextBrush, FontWeights.SemiBold);

        const double left = 52;
        const double right = 18;
        const double top = 38;
        const double bottom = 58;
        double width = Math.Max(1, ActualWidth - left - right);
        double height = Math.Max(1, ActualHeight - top - bottom);
        int maxDays = Math.Min(
            MaximumDisplayedDays,
            Math.Max(60, (int)Math.Ceiling(points[^1].DaysToExpiration / 30.0) * 30));
        double rawMin = points.Min(point => point.Price);
        double rawMax = points.Max(point => point.Price);
        if (_snapshot.SpotVix is > 0)
        {
            rawMin = Math.Min(rawMin, _snapshot.SpotVix.Value);
            rawMax = Math.Max(rawMax, _snapshot.SpotVix.Value);
        }
        double padding = Math.Max(0.5, (rawMax - rawMin) * 0.12);
        double minPrice = Math.Max(0, rawMin - padding);
        double maxPrice = rawMax + padding;
        if (maxPrice <= minPrice)
            maxPrice = minPrice + 1;

        double X(double days) => left + width * days / maxDays;
        double Y(double price) => top + height * (maxPrice - price) / (maxPrice - minPrice);

        for (int startDay = 0, band = 0; startDay < maxDays; startDay += 30, band++)
        {
            if (band % 2 != 0)
                continue;
            var zone = new Rectangle
            {
                Fill = YellowBrush,
                Width = X(Math.Min(maxDays, startDay + 30)) - X(startDay),
                Height = height
            };
            Canvas.SetLeft(zone, X(startDay));
            Canvas.SetTop(zone, top);
            _canvas.Children.Add(zone);
        }

        for (int step = 0; step <= 5; step++)
        {
            double price = minPrice + (maxPrice - minPrice) * step / 5.0;
            double y = Y(price);
            AddLine(left, y, left + width, y, GridBrush, 1);
            AddText(price.ToString("0.0", CultureInfo.InvariantCulture), 4, y - 8, 10, MutedBrush, FontWeights.Normal);
        }
        for (int day = 0; day <= maxDays; day += 30)
        {
            double x = X(day);
            AddLine(x, top, x, top + height, GridBrush, 1);
            AddText(day.ToString(CultureInfo.InvariantCulture), x - 8, top + height + 5, 10, MutedBrush, FontWeights.Normal);
        }

        if (_snapshot.SpotVix is > 0)
        {
            double spotY = Y(_snapshot.SpotVix.Value);
            var spotLine = new Line
            {
                X1 = left,
                X2 = left + width,
                Y1 = spotY,
                Y2 = spotY,
                Stroke = new SolidColorBrush(Color.FromRgb(24, 145, 50)),
                StrokeThickness = 1.8,
                StrokeDashArray = new DoubleCollection { 7, 4 },
                IsHitTestVisible = false
            };
            _canvas.Children.Add(spotLine);
            AddText($"VIX {_snapshot.SpotVix.Value:0.00}", left + width - 72, spotY - 21, 11,
                new SolidColorBrush(Color.FromRgb(18, 116, 40)), FontWeights.SemiBold);
        }

        var curve = new Polyline { Stroke = BlueBrush, StrokeThickness = 2.4 };
        foreach (VixTermPoint point in points)
            curve.Points.Add(new Point(X(point.DaysToExpiration), Y(point.Price)));
        _canvas.Children.Add(curve);

        foreach (VixTermPoint point in points)
        {
            double x = X(point.DaysToExpiration);
            double y = Y(point.Price);
            var marker = new Ellipse { Width = 8, Height = 8, Fill = BlueBrush };
            Canvas.SetLeft(marker, x - 4);
            Canvas.SetTop(marker, y - 4);
            _canvas.Children.Add(marker);
            AddText(point.Price.ToString("0.000", CultureInfo.InvariantCulture), x - 19, y - 22, 10, TextBrush, FontWeights.Normal);
        }

        DrawMetrics(points, left, top + height + 27, width);
    }

    private void DrawMetrics(IReadOnlyList<VixTermPoint> points, double left, double top, double width)
    {
        int adjacentCount = Math.Min(7, Math.Max(0, points.Count - 1));
        AddText("Contango:", left, top, 10, TextBrush, FontWeights.SemiBold);
        double metricsLeft = left + 62;
        double cellWidth = Math.Max(1, (width - 62) / Math.Max(1, adjacentCount));
        for (int index = 0; index < adjacentCount; index++)
        {
            double difference = points[index + 1].Price - points[index].Price;
            double percent = points[index].Price == 0 ? 0 : difference / points[index].Price;
            double x = metricsLeft + index * cellWidth;
            AddText($"{index + 1}→{index + 2}  {percent:+0.00%;-0.00%;0.00%}", x, top, 10,
                percent >= 0 ? new SolidColorBrush(Color.FromRgb(36, 126, 70)) : new SolidColorBrush(Color.FromRgb(190, 55, 55)),
                FontWeights.SemiBold);
        }
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        _canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness });
    }

    private void AddText(string text, double x, double y, double size, Brush brush, FontWeight weight)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        _canvas.Children.Add(block);
    }
}
