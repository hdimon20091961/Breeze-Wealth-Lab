using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WealthLab.Core;
using WealthLab.WPF;

namespace BreezeVL;

public partial class BreezeWindow : ChildWindow
{
    public const string WindowToken = "BreezeVL.MarketBreadth";
    private const int DefaultVisibleBarCount = 500;
    private const int MinimumVisibleBarCount = 50;
    private const int VisibleBarStep = 50;

    public BreezeWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Sp50DetailChart.SetAutoScaleBreadth(true);
        Sp100DetailChart.SetAutoScaleBreadth(true);
        Sp200DetailChart.SetAutoScaleBreadth(true);
        Nasdaq100DetailChart.SetAutoScaleBreadth(true);
        foreach (BreadthChart chart in AllCharts)
            chart.CursorDateChanged += UpdateCursorValues;
        BreadthTabs.SelectionChanged += BreadthTabsSelectionChanged;
        SetPlaceholders();
    }

    public override string Token => WindowToken;

    public override string WorkspaceString
    {
        get => $"{(SpDataSet.SelectedItem as DataSet)?.QualifiedName}|{(NasdaqDataSet.SelectedItem as DataSet)?.QualifiedName}";
        set => _workspaceSelection = value ?? string.Empty;
    }

    private string _workspaceSelection = string.Empty;
    private BreadthSeries? _viewportMaster;
    private BreadthSeries? _sp50Series;
    private BreadthSeries? _sp100Series;
    private BreadthSeries? _sp200Series;
    private BreadthSeries? _nasdaq100Series;
    private int _visibleBarCount = DefaultVisibleBarCount;

    private BreadthChart[] AllCharts =>
    [
        Sp50Chart, Sp100Chart, Sp200Chart, Nasdaq100Chart,
        Sp50DetailChart, Sp100DetailChart, Sp200DetailChart, Nasdaq100DetailChart
    ];

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        List<DataSet> dataSets = WLHost.Instance.DataSets
            .Where(item => !item.IsHidden)
            .OrderBy(item => item.QualifiedName)
            .ToList();
        SpDataSet.ItemsSource = dataSets;
        NasdaqDataSet.ItemsSource = dataSets;

        string[] saved = _workspaceSelection.Split('|');
        SpDataSet.SelectedItem = FindDataSet(dataSets, saved.ElementAtOrDefault(0), "S&P 500");
        NasdaqDataSet.SelectedItem = FindDataSet(dataSets, saved.ElementAtOrDefault(1), "NASDAQ 100", "Nasdaq 100", "Nasdaq-100");
        StatusText.Text = "Select Wealth-Lab dynamic DataSets, then calculate.";
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (SpDataSet.SelectedItem is not DataSet sp || NasdaqDataSet.SelectedItem is not DataSet nasdaq)
        {
            StatusText.Text = "Select both DataSets.";
            return;
        }

        RefreshButton.IsEnabled = false;
        SpDataSet.IsEnabled = false;
        NasdaqDataSet.IsEnabled = false;
        var progress = new Progress<string>(message => StatusText.Text = message);
        try
        {
            DateTime end = DateTime.Today;
            DateTime start = end.AddYears(-12);
            IReadOnlyList<BreadthSeries> spSeries = await Task.Run(() =>
                BreadthCalculator.Calculate(sp, [200, 100, 50], start, end, progress));
            IReadOnlyList<BreadthSeries> nasdaqSeries = await Task.Run(() =>
                BreadthCalculator.Calculate(nasdaq, [100], start, end, progress));
            PriceSeries? sp500Price = null;
            try
            {
                sp500Price = await Task.Run(() =>
                    BreadthCalculator.LoadSp500Price(sp, start, end));
            }
            catch (Exception priceException)
            {
                WLHost.Instance.AddLogItem(
                    "Breeze Wealth-Lab",
                    $"S&P 500 price overlay could not be loaded: {priceException.Message}",
                    WLColor.Orange,
                    priceException,
                    this);
            }

            _sp200Series = spSeries.Single(series => series.Period == 200);
            _sp100Series = spSeries.Single(series => series.Period == 100);
            _sp50Series = spSeries.Single(series => series.Period == 50);
            _nasdaq100Series = nasdaqSeries.Single();
            Sp200Chart.SetSeries(_sp200Series);
            Sp200DetailChart.SetSeries(_sp200Series);
            Sp100Chart.SetSeries(_sp100Series);
            Sp100DetailChart.SetSeries(_sp100Series);
            Sp50Chart.SetSeries(_sp50Series);
            Sp50DetailChart.SetSeries(_sp50Series);
            Nasdaq100Chart.SetSeries(_nasdaq100Series);
            Nasdaq100DetailChart.SetSeries(_nasdaq100Series);
            SetPriceSeries(sp500Price);
            ConfigureHistorySlider(_sp200Series);
            StatusText.Text = $"Ready: {start:yyyy-MM-dd} — {end:yyyy-MM-dd}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Calculation failed: {ex.Message}";
            WLHost.Instance.AddLogItem(
                "Breeze Wealth-Lab",
                $"Market breadth calculation failed: {ex}",
                WLColor.Red,
                ex,
                this);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            SpDataSet.IsEnabled = true;
            NasdaqDataSet.IsEnabled = true;
        }
    }

    private void ConfigureHistorySlider(BreadthSeries master)
    {
        _viewportMaster = master;
        _visibleBarCount = Math.Min(DefaultVisibleBarCount, Math.Max(1, master.Points.Count));
        int maximumStart = Math.Max(0, master.Points.Count - _visibleBarCount);
        HistorySlider.Maximum = maximumStart;
        HistorySlider.SmallChange = 10;
        HistorySlider.LargeChange = 100;
        HistorySlider.Value = maximumStart;
        ApplyHistoryViewport();
    }

    private void DecreaseVisibleBarsClick(object sender, RoutedEventArgs e) =>
        ChangeVisibleBarCount(-VisibleBarStep);

    private void IncreaseVisibleBarsClick(object sender, RoutedEventArgs e) =>
        ChangeVisibleBarCount(VisibleBarStep);

    private void ChartsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeVisibleBarCount(e.Delta > 0 ? -VisibleBarStep : VisibleBarStep);
        e.Handled = true;
    }

    private void BreadthTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, BreadthTabs))
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (BreadthTabs.SelectedIndex)
            {
                case 0:
                    Sp50Chart.Refresh();
                    Sp100Chart.Refresh();
                    Sp200Chart.Refresh();
                    Nasdaq100Chart.Refresh();
                    break;
                case 1:
                    Sp50DetailChart.Refresh();
                    break;
                case 2:
                    Sp100DetailChart.Refresh();
                    break;
                case 3:
                    Sp200DetailChart.Refresh();
                    break;
                case 4:
                    Nasdaq100DetailChart.Refresh();
                    break;
            }
        }));
    }

    private void ShowSp500PriceChanged(object sender, RoutedEventArgs e) =>
        SetPriceVisibility(ShowSp500Price.IsChecked == true);

    private void ShowSp500CandlesChanged(object sender, RoutedEventArgs e) =>
        SetPriceCandleMode(ShowSp500Candles.IsChecked == true);

    private void UseLogBreadthChanged(object sender, RoutedEventArgs e) =>
        SetLogBreadthMode(UseLogBreadth.IsChecked == true);

    private void SetPriceSeries(PriceSeries? series)
    {
        foreach (BreadthChart chart in AllCharts)
            chart.SetPriceSeries(series);
        SetPriceVisibility(ShowSp500Price.IsChecked == true);
        SetPriceCandleMode(ShowSp500Candles.IsChecked == true);
    }

    private void SetPriceVisibility(bool show)
    {
        if (Sp50Chart is null || Sp50DetailChart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetShowPriceSeries(show);
    }

    private void SetPriceCandleMode(bool showCandles)
    {
        if (Sp50Chart is null || Sp50DetailChart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetShowPriceCandles(showCandles);
    }

    private void SetLogBreadthMode(bool useLogarithmicScale)
    {
        if (Sp50Chart is null || Sp50DetailChart is null)
            return;

        foreach (BreadthChart chart in AllCharts)
            chart.SetLogBreadthScale(useLogarithmicScale);
    }

    private void UpdateCursorValues(DateTime date)
    {
        BreadthPoint? sp50 = FindPoint(_sp50Series, date);
        BreadthPoint? sp100 = FindPoint(_sp100Series, date);
        BreadthPoint? sp200 = FindPoint(_sp200Series, date);
        BreadthPoint? nasdaq100 = FindPoint(_nasdaq100Series, date);
        if (sp50 is null || sp100 is null || sp200 is null || nasdaq100 is null)
            return;

        CursorValuesText.Text =
            $"{date:yyyy-MM-dd}   S&P SMA50: {sp50.Value:F1}%   " +
            $"S&P SMA100: {sp100.Value:F1}%   S&P SMA200: {sp200.Value:F1}%   " +
            $"Nasdaq SMA100: {nasdaq100.Value:F1}%";
    }

    private static BreadthPoint? FindPoint(BreadthSeries? series, DateTime date)
    {
        if (series is null || series.Points.Count == 0)
            return null;

        BreadthPoint? exact = series.Points.FirstOrDefault(point => point.Date == date);
        if (exact is not null)
            return exact;

        return series.Points.MinBy(point => Math.Abs((point.Date - date).Ticks));
    }

    private void ChangeVisibleBarCount(int delta)
    {
        if (_viewportMaster is null || _viewportMaster.Points.Count == 0)
            return;

        int pointCount = _viewportMaster.Points.Count;
        int oldStart = Math.Clamp((int)Math.Round(HistorySlider.Value), 0, Math.Max(0, pointCount - 1));
        int oldEnd = Math.Min(pointCount - 1, oldStart + _visibleBarCount - 1);
        int minimum = Math.Min(MinimumVisibleBarCount, pointCount);
        int newCount = Math.Clamp(_visibleBarCount + delta, minimum, pointCount);
        if (newCount == _visibleBarCount)
            return;

        _visibleBarCount = newCount;
        int maximumStart = Math.Max(0, pointCount - _visibleBarCount);
        int newStart = Math.Clamp(oldEnd - _visibleBarCount + 1, 0, maximumStart);
        HistorySlider.Maximum = maximumStart;
        HistorySlider.Value = newStart;
        ApplyHistoryViewport();
    }

    private void HistorySliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ApplyHistoryViewport();

    private void ApplyHistoryViewport()
    {
        if (_viewportMaster is null || _viewportMaster.Points.Count == 0)
            return;

        int startIndex = Math.Clamp((int)Math.Round(HistorySlider.Value), 0, Math.Max(0, _viewportMaster.Points.Count - 1));
        int endIndex = Math.Min(_viewportMaster.Points.Count - 1, startIndex + _visibleBarCount - 1);
        DateTime start = _viewportMaster.Points[startIndex].Date;
        DateTime end = _viewportMaster.Points[endIndex].Date;

        Sp200Chart.SetViewport(start, end);
        Sp200DetailChart.SetViewport(start, end);
        Sp100Chart.SetViewport(start, end);
        Sp100DetailChart.SetViewport(start, end);
        Sp50Chart.SetViewport(start, end);
        Sp50DetailChart.SetViewport(start, end);
        Nasdaq100Chart.SetViewport(start, end);
        Nasdaq100DetailChart.SetViewport(start, end);
        VisibleBarsText.Text = $"{_visibleBarCount} bars";
        VisibleRangeText.Text = $"{start:yyyy-MM-dd} — {end:yyyy-MM-dd}";
    }

    private void SetPlaceholders()
    {
        Sp200Chart.SetMessage("S&P 500 — above SMA(200)", "Waiting for calculation");
        Sp200DetailChart.SetMessage("S&P 500 — above SMA(200)", "Waiting for calculation");
        Sp100Chart.SetMessage("S&P 500 — above SMA(100)", "Waiting for calculation");
        Sp100DetailChart.SetMessage("S&P 500 — above SMA(100)", "Waiting for calculation");
        Sp50Chart.SetMessage("S&P 500 — above SMA(50)", "Waiting for calculation");
        Sp50DetailChart.SetMessage("S&P 500 — above SMA(50)", "Waiting for calculation");
        Nasdaq100Chart.SetMessage("Nasdaq-100 — above SMA(100)", "Waiting for calculation");
        Nasdaq100DetailChart.SetMessage("Nasdaq-100 — above SMA(100)", "Waiting for calculation");
    }

    private static DataSet? FindDataSet(IEnumerable<DataSet> dataSets, string? exact, params string[] hints)
    {
        DataSet? exactMatch = dataSets.FirstOrDefault(item =>
            string.Equals(item.QualifiedName, exact, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
            return exactMatch;

        foreach (string hint in hints)
        {
            DataSet? match = dataSets.FirstOrDefault(item =>
                string.Equals(item.Name, hint, StringComparison.OrdinalIgnoreCase));
            match ??= dataSets.FirstOrDefault(item =>
                item.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }
}
