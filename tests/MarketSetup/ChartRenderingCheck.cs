using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Markup;
using System.Xml.Linq;
using Path = System.IO.Path;

namespace BreezeVL;

// The rendering test needs the dated data contract, without any provider/host.
internal sealed record BreadthPoint(DateTime Date, double Open, double High, double Low, double Close, int EligibleCount)
{
    public double Value => Close;
}
internal sealed record BreadthSeries(string Name, int Period, IReadOnlyList<BreadthPoint> Points, bool HasOhlc);
internal sealed record TechnicalIndicatorPoint(DateTime Date, double Ema20, double Ema50,
    double Ema100, double Ema200, double MyRsiRep, double MyRsiRep10, double Atrp1);
internal sealed record TechnicalIndicatorSeries(int MyRsiPeriod, int MyRsiSmoothPeriod,
    int AgcHalfLife, IReadOnlyList<TechnicalIndicatorPoint> Points);

internal static class ChartRenderingCheck
{
    public static void Run(SentimentSeries series, string outputDirectory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(series, outputDirectory); } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Dual-axis chart rendering failed", failure);
    }

    private static void Verify(SentimentSeries series, string outputDirectory)
    {
        void Check(bool valid, string description)
        {
            if (!valid) throw new InvalidOperationException(description);
        }
        BreadthSeries Breadth(string name, int period, double baseline, bool withGaps = false) => new(name, period,
            series.Points.Select((point, index) => new BreadthPoint(point.Date, 0, 0, 0,
                baseline + 12 * Math.Sin(index * 0.07), 500))
                .Where((_, index) => !withGaps || index % 17 != 0).ToArray(), false);

        var chart = new SentimentChart();
        chart.SetSeries(series);
        chart.SetBreadthSeries(Breadth("SP50", 50, 35), Breadth("SP100", 100, 50, true),
            Breadth("SP200", 200, 60), Breadth("N100", 100, 70));
        chart.SetViewport(series.Points[200].Date, series.Points[300].Date);
        chart.SetSelection([SentimentIndicator.MyRsi, SentimentIndicator.MyRsiFast], []);
        chart.Measure(new Size(1000, 320));
        chart.Arrange(new Rect(0, 0, 1000, 320));
        chart.UpdateLayout();
        chart.Refresh();
        var canvas = (Canvas)chart.Child;
        Polyline[] Lines(Color color) => canvas.Children.OfType<Polyline>()
            .Where(line => line.Stroke is SolidColorBrush brush && brush.Color == color).ToArray();
        Color myRsiColor = SentimentChart.ColorFor(SentimentIndicator.MyRsi);
        Check(canvas.Children.OfType<Polyline>().Count() == 2, "Breadths must initially be off");
        Point[] originalIndicators = Lines(myRsiColor).Single().Points.ToArray();
        Check(originalIndicators.Length == 101, "Indicator viewport must use the shared dates");

        chart.SetSelection([SentimentIndicator.MyRsi, SentimentIndicator.MyRsiFast], [SentimentBreadth.Sp50]);
        chart.Refresh();
        Check(originalIndicators.SequenceEqual(Lines(myRsiColor).Single().Points),
            "Adding percentage breadths must not change indicator X or Y coordinates");
        Check(canvas.Children.OfType<TextBlock>().Where(label => Canvas.GetLeft(label) > 500)
            .All(label => label.Text.EndsWith('%')), "Right-axis labels must use percentages");
        Check(canvas.Children.OfType<TextBlock>().Where(label => Canvas.GetLeft(label) < 50)
            .All(label => !label.Text.EndsWith('%')), "Left-axis labels must retain indicator units");
        Point[] originalBreadth = Lines(Color.FromRgb(25, 118, 210)).Single().Points.ToArray();

        chart.SetSelection([], [SentimentBreadth.Sp50]);
        chart.Refresh();
        Check(originalBreadth.SequenceEqual(Lines(Color.FromRgb(25, 118, 210)).Single().Points),
            "Hiding indicators must not change the independent breadth scale");
        Check(!canvas.Children.OfType<TextBlock>().Any(label => Canvas.GetLeft(label) < 50),
            "Breadth-only mode must hide the unused indicator axis");

        chart.SetSelection([SentimentIndicator.MyRsi, SentimentIndicator.MyRsiFast],
            [SentimentBreadth.Sp50, SentimentBreadth.Sp100, SentimentBreadth.Sp200, SentimentBreadth.Nasdaq100]);
        chart.Refresh();
        Polyline[] gapped = Lines(Color.FromRgb(46, 125, 50));
        Check(gapped.Length > 1, "Missing breadth observations must split the curve instead of shifting its calendar");
        double slotWidth = (canvas.ActualWidth - 100) / 102.0;
        // First missing observation in this window is at source index 204.
        Check(gapped[0].Points.Count == 4 && Math.Abs(gapped[1].Points[0].X - (50 + 5 * slotWidth)) < 1e-8,
            "Breadth dates must use the indicator/master horizontal slots");
        chart.SetCursorDate(series.Points[250].Date);
        chart.SetCursorPinned(true);
        chart.Refresh();
        Line pinned = canvas.Children.OfType<Line>().Single(line => line.StrokeThickness == 2);
        Check(Math.Abs(pinned.X1 - (50 + 50 * slotWidth)) < 1e-8, "Pinned cursor must retain the master date");

        string output = Path.Combine(outputDirectory, "dual_axis.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        chart.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1000, 320, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(chart);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(output);
        encoder.Save(file);
        VerifyCompactLayout(outputDirectory);
        VerifyAtrp(series, outputDirectory);
        VerifyPaneTransitions(outputDirectory);
        VerifyRealPaneResize(series, outputDirectory);
        VerifyNumericControls();
        Console.WriteLine("Chart rendering passed: independent Y axes, shared dated X slots, missing dates, pinned cursor, default-off breadths.");
    }

    private static void VerifyNumericControls()
    {
        XNamespace wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml="http://schemas.microsoft.com/winfx/2006/xaml";
        var source=XDocument.Load("Breeze/BreezeWindow.xaml");
        foreach(string handler in new[]{"DecreaseVisibleBarsClick","IncreaseVisibleBarsClick","DecreaseTradingForecastClick","IncreaseTradingForecastClick"})
        {
            XElement element=source.Descendants().Single(e=>(string?)e.Attribute("Click")==handler);
            if(element.Name!=wpf+"RepeatButton" || (int?)element.Attribute("Delay")!=350 || (int?)element.Attribute("Interval")!=100)
                throw new InvalidOperationException("Hold-to-repeat missing: "+handler);
        }
        XElement input=source.Descendants(wpf+"TextBox").Single(e=>(string?)e.Attribute(xaml+"Name")=="TradingForecastValue");
        if((string?)input.Attribute("IsReadOnly")=="True" || input.Attribute("PreviewKeyDown") is null || input.Attribute("LostKeyboardFocus") is null)
            throw new InvalidOperationException("Forecast must allow exact keyboard input and commit");
        Console.WriteLine("Numeric controls passed: four WPF RepeatButtons; editable Forecast with Enter/focus commit.");
    }

    private static void VerifyRealPaneResize(SentimentSeries series, string outputDirectory)
    {
        var indicator=new SentimentChart();
        indicator.SetSeries(series);
        var breadth=new BreadthSeries("SP50",50,series.Points.Select((p,i)=>new BreadthPoint(p.Date,0,0,0,50+25*Math.Sin(i*.07),500)).ToArray(),false);
        indicator.SetBreadthSeries(breadth,breadth,breadth,breadth);
        indicator.SetSelection([SentimentIndicator.MyRsi],[SentimentBreadth.Sp50]);
        var atrp=new AtrpChart();
        atrp.SetShowDateLabels(false);
        atrp.SetSeries(new TechnicalIndicatorSeries(14,8,77,series.Points.Select((p,i)=>new TechnicalIndicatorPoint(p.Date,0,0,0,0,0,0,.1+2*Math.Abs(Math.Sin(i*.05)))).ToArray()));
        var root=new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(indicator);root.Children.Add(atrp);Grid.SetRow(atrp,1);
        indicator.SetViewport(series.Points[200].Date,series.Points[300].Date);
        atrp.SetViewport(series.Points[200].Date,series.Points[300].Date);
        foreach(int height in new[]{320,90,180,82,70,420,90})
        {
            root.RowDefinitions[0].Height=new GridLength(height);
            root.RowDefinitions[1].Height=new GridLength(Math.Max(70,height/2));
            // Only the parent is arranged: the real chart Canvas must resize and redraw itself.
            root.Measure(new Size(1000,700));root.Arrange(new Rect(0,0,1000,700));root.UpdateLayout();
            foreach(Border chart in new Border[]{indicator,atrp})
            {
                var canvas=(Canvas)chart.Child;
                var lines=canvas.Children.OfType<Polyline>().ToArray();
                if(lines.Length==0 || !canvas.ClipToBounds || Math.Abs(canvas.ActualHeight-(chart.ActualHeight-6))>.001)
                    throw new InvalidOperationException("Real chart failed compact layout: "+height);
                double top=chart==indicator ? 10 : 27, bottom=chart==indicator ? 9 : 8;
                if(lines.SelectMany(line=>line.Points).Any(p=>p.Y<top-.001 || p.Y>canvas.ActualHeight-bottom+.001))
                    throw new InvalidOperationException("Stale geometry escapes compact chart: "+height);
                double axisBottom=canvas.Children.OfType<Line>().Where(l=>Math.Abs(l.Y1-l.Y2)<.001).Max(l=>l.Y1);
                if(Math.Abs(axisBottom-(canvas.ActualHeight-bottom))>.001)
                    throw new InvalidOperationException("Scale did not follow actual canvas height: "+height);
            }
        }
        var bitmap=new RenderTargetBitmap(1000,700,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(outputDirectory,"real_compact_panes.png"));encoder.Save(file);
        Console.WriteLine("Real resize regression passed: indicator and ATRP controls at 70–420 px, no manual Canvas arrangement or splitter movement.");
    }

    private static void VerifyCompactLayout(string outputDirectory)
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument source = XDocument.Load("Breeze/BreezeWindow.xaml");
        XElement tab = source.Descendants(wpf + "TabItem").Single(item => (string?)item.Attribute(xaml + "Name") == "MarketSetupTab");
        XElement[] lowerSelectors = tab.Descendants(wpf + "CheckBox")
            .Where(item => new[] { "MarketSetupNasdaq100", "MarketSetupAtrp" }.Contains((string?)item.Attribute(xaml + "Name"))).ToArray();
        if (lowerSelectors.Length != 2 || (string?)lowerSelectors[1].Attribute(xaml + "Name") != "MarketSetupAtrp" ||
            (string?)lowerSelectors[1].Attribute("IsChecked") != "False")
            throw new InvalidOperationException("ATRP must be default-off immediately after Nasdaq in the breadth block");
        var fragment = new XElement(tab.Element(wpf + "Grid")!);
        // Isolate the real left-column layout; no Wealth-Lab host or providers are started.
        fragment.Elements(wpf + "Grid").Single(item => (string?)item.Attribute("Grid.Column") == "2")
            .ReplaceWith(new XElement(wpf + "Border", new XAttribute("Grid.Column", "2"),
                new XAttribute("Background", "#FFFCF4")));
        XElement resources = source.Root!.Elements().Single(item => item.Name.LocalName == "ChildWindow.Resources");
        fragment.AddFirst(new XElement(wpf + "Grid.Resources", resources.Elements().Select(item => new XElement(item))));
        string[] events = ["SizeChanged", "Checked", "Unchecked", "SelectedItemChanged", "DragCompleted"];
        foreach (XAttribute attribute in fragment.DescendantsAndSelf().Attributes().Where(item => events.Contains(item.Name.LocalName)).ToArray())
            attribute.Remove();
        // Display the compact template; production switches it on when a column narrows.
        fragment.SetAttributeValue(XNamespace.Xmlns + "x", xaml.NamespaceName);
        foreach (XElement checkbox in fragment.Descendants(wpf + "StackPanel")
                     .Single(item => (string?)item.Attribute(xaml + "Name") == "MarketSetupIndicatorSelectors")
                     .Descendants(wpf + "CheckBox"))
            checkbox.SetAttributeValue("ContentTemplate", "{StaticResource MarketSetupCompactToggleTemplate}");
        var grid = (Grid)XamlReader.Parse(fragment.ToString());
        if (((CheckBox)grid.FindName("MarketSetupActiveRules")).IsChecked != false)
            throw new InvalidOperationException("The Active rules filter must initially be off");
        grid.ColumnDefinitions[0].Width = new GridLength(200);
        Grid analysis = (Grid)grid.FindName("MarketSetupAnalysisGrid");
        analysis.ColumnDefinitions[0].Width = new GridLength(52);
        analysis.ColumnDefinitions[2].Width = new GridLength(110);
        analysis.ColumnDefinitions[4].Width = new GridLength(0);
        var tree = (TreeView)grid.FindName("MarketSetupRuleTree");
        ((TextBlock)grid.FindName("MarketSetupCatalogSummary")).Text = "Всего: 141; DLL: 86; активных: 32";
        ((TextBlock)grid.FindName("MarketSetupIndicatorsTitle")).Text = "И";
        tree.FontSize = 11;
        var family = new TreeViewItem { Header = "T", IsExpanded = true };
        family.Items.Add(new TreeViewItem { Header = "08 T08" });
        tree.Items.Add(family);
        grid.Measure(new Size(1200, 720));
        grid.Arrange(new Rect(0, 0, 1200, 720));
        grid.UpdateLayout();
        if (grid.ColumnDefinitions[0].ActualWidth > 200.01 ||
            analysis.ColumnDefinitions[0].ActualWidth > 52.01 ||
            analysis.ColumnDefinitions[2].ActualWidth > 110.01 ||
            analysis.ColumnDefinitions[4].ActualWidth > 0.01)
            throw new InvalidOperationException("Production XAML still prevents checkbox/code-only columns or collapsed description");
        var bitmap = new RenderTargetBitmap(1200, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(outputDirectory, "compact_columns.png"));
        encoder.Save(file);
        Console.WriteLine("Layout rendering passed: production XAML supports 52px checkboxes, 110px rule codes, 0px description, 200px total analysis.");
    }

    private static void VerifyAtrp(SentimentSeries series, string outputDirectory)
    {
        var dates = series.Points.Skip(200).Take(101).Select(point => point.Date).ToArray();
        var points = dates.Select((date, index) => new TechnicalIndicatorPoint(date,0,0,0,0,0,0,0.4+index*.008))
            .Where((point,index) => index != 4).ToArray();
        var chart = new AtrpChart();
        chart.SetHorizontalPlotMargins(50,50); chart.SetShowDateLabels(false);
        chart.SetReferenceLevel(2); chart.SetMasterDates(dates);
        chart.SetSeries(new TechnicalIndicatorSeries(14,8,77,points));
        chart.SetViewport(dates[0],dates[^1]);
        chart.Measure(new Size(1000,180)); chart.Arrange(new Rect(0,0,1000,180)); chart.UpdateLayout(); chart.Refresh();
        var canvas=(Canvas)chart.Child;
        double slot=(canvas.ActualWidth-100)/102;
        Polyline[] lines=canvas.Children.OfType<Polyline>().ToArray();
        if(lines.Length!=2 || lines[0].Points.Count!=4 || Math.Abs(lines[1].Points[0].X-(50+5*slot))>1e-8)
            throw new InvalidOperationException("ATRP gaps must keep exact master date slots");
        chart.SetCursorDate(dates[50]); chart.SetCursorPinned(true); chart.Refresh();
        Line cursor=canvas.Children.OfType<Line>().Single(line => line.StrokeThickness==2);
        if(Math.Abs(cursor.X1-(50+50*slot))>1e-8 ||
            !canvas.Children.OfType<TextBlock>().Any(text => text.Text.Contains("2%")))
            throw new InvalidOperationException("ATRP cursor or threshold reference failed");
        chart.UpdateLayout();
        var image=new RenderTargetBitmap(1000,180,96,96,PixelFormats.Pbgra32); image.Render(chart);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        using var file=File.Create(Path.Combine(outputDirectory,"atrp_pane.png"));encoder.Save(file);
        chart.SetReferenceLevel(null);
        chart.SetViewport(dates[0],dates[10]);
        chart.Refresh();
        if (!canvas.Children.OfType<TextBlock>().Any(text=>text.Text=="0.50") ||
            canvas.Children.OfType<TextBlock>().Any(text=>text.Text.StartsWith("Порог:")))
            throw new InvalidOperationException("ATRP must rescale to the new viewport and remove the previous rule threshold");
        Console.WriteLine("ATRP rendering passed: last selector after Nasdaq, default-off, independent percentage axis, threshold, dated gaps and pinned cursor.");
    }

    private static void VerifyPaneTransitions(string outputDirectory)
    {
        XNamespace wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml="http://schemas.microsoft.com/winfx/2006/xaml";
        var source=XDocument.Load("Breeze/BreezeWindow.xaml");
        var fragment=new XElement(source.Descendants(wpf+"Grid").Single(e=>(string?)e.Attribute(xaml+"Name")=="MarketSetupChartGrid"));
        foreach(var chart in fragment.Descendants().Where(e=>new[]{"BreadthChart","SentimentChart","AtrpChart"}.Contains(e.Name.LocalName)).ToArray())
            chart.ReplaceWith(new XElement(wpf+"Border",chart.Attributes(),new XAttribute("Background","#FFFCF4"),new XAttribute("Margin","4")));
        foreach(var attribute in fragment.DescendantsAndSelf().Attributes().Where(a=>a.Name.LocalName=="DragCompleted").ToArray())attribute.Remove();
        fragment.SetAttributeValue(XNamespace.Xmlns+"x",xaml.NamespaceName);
        var outer=(Grid)XamlReader.Parse(fragment.ToString());
        T Find<T>(string name) where T:FrameworkElement => (T)outer.FindName(name);
        var lower=Find<Grid>("MarketSetupLowerGrid");
        var layout=new MarketSetupPaneLayout(outer,lower,Find<FrameworkElement>("MarketSetupHorizontalSplitter"),
            Find<FrameworkElement>("MarketSetupIndicatorChart"),Find<FrameworkElement>("MarketSetupAtrpSplitter"),Find<FrameworkElement>("MarketSetupAtrpChart"));
        var selection=new MarketSetupSelectionState();
        string[] lowerKeys=["MarketSetupCnnFearGreed","MarketSetupSp50","MarketSetupSp100","MarketSetupSp200","MarketSetupNasdaq100","MarketSetupMyRsi","MarketSetupMyRsiFast","MarketSetupAroonUp"];
        void Apply(bool indicator,bool atrp,string scenario)
        {
            if(lowerKeys.Any(selection.IsSelected)!=indicator || selection.IsSelected("MarketSetupAtrp")!=atrp)
                throw new InvalidOperationException("Wrong manual/automatic selection: "+scenario);
            layout.Apply(indicator,atrp,.61,.57);
            outer.Measure(new Size(1000,700));outer.Arrange(new Rect(0,0,1000,700));outer.UpdateLayout();
            if(!indicator && !atrp)
            {
                if(outer.RowDefinitions[2].ActualHeight>.01 || outer.RowDefinitions[1].ActualHeight>.01 ||
                   Math.Abs(outer.RowDefinitions[0].ActualHeight-outer.ActualHeight)>.01)
                    throw new InvalidOperationException("Empty panes still reserve space: "+scenario);
            }
            else if(Math.Abs(outer.RowDefinitions[0].Height.Value-.61)>.00001 ||
                    (!indicator && lower.RowDefinitions[0].ActualHeight>.01) ||
                    (!atrp && lower.RowDefinitions[2].ActualHeight>.01) ||
                    (!(indicator && atrp) && lower.RowDefinitions[1].ActualHeight>.01))
                throw new InvalidOperationException("Wrong pane geometry or saved proportions: "+scenario);
        }
        void Rule(string id) => selection.SelectRule(id,BreezeRuleCatalog.Current.ById(id).ImplementedFormula);
        Rule("T01");Apply(false,false,"EMA only");
        selection.SetManual("MarketSetupCnnFearGreed",true);Apply(true,false,"CNN enabled");
        selection.SetManual("MarketSetupCnnFearGreed",false);Apply(false,false,"CNN disabled");
        Rule("T03");Apply(true,true,"T03 dependencies");
        Rule("T04");Apply(false,false,"T03 to T04");
        Rule("T03_1");Apply(true,true,"T03/1 dependencies");
        Rule("T01");Apply(false,false,"T03/1 to T01");
        Rule("R03");Apply(true,false,"RSI dependencies");
        Rule("T01");Apply(false,false,"automatic RSI removed on T01");
        selection.SetManual("MarketSetupSp50",true);
        Rule("T03");Apply(true,true,"manual breadth plus T03");
        Rule("T01");Apply(false,false,"manual breadth removed on rule selection");
        selection.SetManual("MarketSetupSp50",false);
        Rule("T03");
        selection.SetManual("MarketSetupSp50",false);selection.SetManual("MarketSetupSp50",true);
        Rule("T01");Apply(false,false,"manual override removed on rule selection");
        selection.SetManual("MarketSetupSp50",false);
        selection.SetManual("MarketSetupAtrp",true);Apply(false,true,"ATRP only");
        selection.SetManual("MarketSetupAtrp",false);
        selection.SetManual("MarketSetupCnnFearGreed",true);
        Rule("T03");Apply(true,true,"manual CNN with T03");
        selection.SetManual("MarketSetupAtrp",false);Rule("T03");Apply(true,true,"same-rule selection restores exact dependencies");
        Rule("T01");Apply(false,false,"manual CNN removed on T01");
        selection.SetManual("MarketSetupCnnFearGreed",false);Apply(false,false,"full collapse after manual removal");
        var bitmap=new RenderTargetBitmap(1000,700,96,96,PixelFormats.Pbgra32);bitmap.Render(outer);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(outputDirectory,"price_only_layout.png"));encoder.Save(output);
        Console.WriteLine("Pane transitions passed: production XAML, EMA-only collapse, CNN show/hide, T03->T04/T01 cleanup, manual overrides, ATRP-only, restored proportions.");
    }
}
