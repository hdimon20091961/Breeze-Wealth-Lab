// Integration smoke test for the VIX term-structure data pipeline.
using BreezeVL;

string source = args.ElementAtOrDefault(0) ?? @"D:\fear_greed_close\VIX";
string? output = args.ElementAtOrDefault(1);
string spyCalendarPath = args.ElementAtOrDefault(2) ?? @"D:\fear_greed_close\Breeze\SPY.csv";
VixTermStructureStore store = VixTermStructureStore.BuildIndex(source, CancellationToken.None);
HashSet<DateTime> tradingDates = File.ReadLines(spyCalendarPath)
    .Skip(1)
    .Select(line => line.Split(',', 2)[0])
    .Where(value => DateTime.TryParse(value, out _))
    .Select(DateTime.Parse)
    .Select(date => date.Date)
    .ToHashSet();
store.SetTradingCalendar(tradingDates);

string spotSource = args.ElementAtOrDefault(3) ?? @"D:\fear_greed_close\vix_term_structure.csv";
if (File.Exists(spotSource))
{
    store.SetSpotVix(File.ReadLines(spotSource).Skip(1).Select(line => line.Split(','))
        .Where(parts => parts.Length >= 2 && DateTime.TryParse(parts[0], out _) &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out _))
        .Select(parts => (DateTime.Parse(parts[0]).Date,
            double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))));
}

foreach (DateTime requested in new[]
         {
             new DateTime(2017, 11, 17),
             new DateTime(2020, 3, 23),
             new DateTime(2026, 10, 9)
         })
{
    VixTermSnapshot snapshot = store.GetExact(requested);
    if (snapshot.Points.Count < 2 || snapshot.Points.Any(point => point.Price <= 0))
        throw new InvalidDataException($"Invalid term structure for {requested:yyyy-MM-dd}");
    Console.WriteLine(
        $"{requested:yyyy-MM-dd} -> {snapshot.TradeDate:yyyy-MM-dd}: " +
        $"{snapshot.Points.Count} contracts, 30d={snapshot.Price30:0.0000}, " +
        $"60d={snapshot.Price60:0.0000}, contango={snapshot.Contango30To60:P2}");
}

if (store.GetExact(new DateTime(2017, 11, 17)).SpotVix is null)
    throw new InvalidDataException("Exact-date spot VIX lookup failed.");
foreach (DateTime invalid in new[] { new DateTime(2017, 11, 18), new DateTime(2026, 1, 19) })
{
    try
    {
        store.GetExact(invalid);
        throw new InvalidDataException($"Non-trading date {invalid:yyyy-MM-dd} was accepted.");
    }
    catch (InvalidOperationException)
    {
        // Expected: weekends and exchange holidays are not state dates.
    }
}

VixCurveStateSeries states = store.BuildStateSeries(CancellationToken.None);
if (states.Points.Count < 2500 || states.Points.Any(point => point.AdjacentPairCount < 1))
    throw new InvalidDataException("Contango state history is incomplete.");
if (states.Points.Any(point => !tradingDates.Contains(point.Date)) ||
    states.Points.Any(point => point.Date == new DateTime(2026, 1, 19)))
    throw new InvalidDataException("A non-SPY date entered the Contango state sequence.");
VixCurveStatePoint january20 = states.Points.Single(point => point.Date == new DateTime(2026, 1, 20));
if (january20.State != VixCurveState.StrongContango || january20.TransitionConfirmationApplied)
    throw new InvalidDataException("The 2026-01-19 holiday still changes the 2026-01-20 state.");
VixCurveStatePoint covid = states.Points.MinBy(point => Math.Abs((point.Date - new DateTime(2020, 3, 23)).Ticks))!;
if (covid.State != VixCurveState.StrongBackwardation || covid.CurveRegimeScore > -0.60)
    throw new InvalidDataException($"Expected broad backwardation near 2020-03-23, got {covid.State}.");
if (covid.AdjustmentExplanation.Contains("геометрия контанго восстановилась", StringComparison.Ordinal))
    throw new InvalidDataException("The recovered-contango explanation must not appear during backwardation.");
VixCurveStatePoint calm = states.Points.MinBy(point => Math.Abs((point.Date - new DateTime(2017, 11, 17)).Ticks))!;
if (calm.State != VixCurveState.StrongContango || calm.CurveRegimeScore < 0.60)
    throw new InvalidDataException($"Expected strong contango near 2017-11-17, got {calm.State}.");
int svxyCandidates = states.Points.Count(point => point.SvxyBuyCandidate);
if (svxyCandidates < 50)
    throw new InvalidDataException("Too few VIX-curve exits were marked as SVXY candidates.");
if (states.Points.Any(point =>
        point.CurveRegimeScore <= -0.18 && point.SvxyRecommendation != SvxyRecommendation.Neutral))
    throw new InvalidDataException("SVXY recommendation remained active after VIX curve returned to red.");
if (states.Points.Any(point => point.SvxyBarsSinceCandidate is < 0))
    throw new InvalidDataException("SVXY bars-since-candidate became negative.");
if (states.Points.Any(point =>
        point.SvxyRecommendation == SvxyRecommendation.Buy &&
        point.SvxyBarsSinceCandidate is not (>= 1 and <= 60)))
    throw new InvalidDataException("SVXY Buy/Hold state is outside the validated 1..60 bar window.");
if (states.Points.Any(point =>
        point.SvxyRecommendation == SvxyRecommendation.ExtendedHold &&
        point.SvxyBarsSinceCandidate is not > 60))
    throw new InvalidDataException("SVXY ExtendedHold state began before bar 61.");
if (states.Points.Any(point =>
        point.SvxyBuyCandidate != (point.SvxyRecommendation == SvxyRecommendation.StrongBuy)))
    throw new InvalidDataException("SVXY candidate day and StrongBuy ribbon state are not aligned.");
if (!states.Points.Any(point => point.SvxyRecommendation == SvxyRecommendation.ExtendedHold &&
        point.SvxyBarsSinceCandidate > 60))
    throw new InvalidDataException("No extended SVXY hold survived the corrected curve calculation.");
if (!states.Points.Any(point => point.LegacyState != point.State))
    throw new InvalidDataException("The New and Old curve histories never differ.");

VixTermPoint[] june24 =
[
    new(22, 14.3967), new(57, 15.2751), new(85, 16.0899),
    new(113, 18.4474), new(148, 17.4511), new(176, 17.4619)
];
VixTermPoint[] june25 =
[
    new(21, 14.0770), new(56, 14.9865), new(84, 15.8488),
    new(112, 18.2478), new(147, 17.4000), new(175, 17.3008)
];
VixCurveStatePoint corrected24 = VixCurveStateClassifier.Classify(new(2024, 6, 24), june24, 13.33);
VixCurveStatePoint corrected25 = VixCurveStateClassifier.Classify(new(2024, 6, 25), june25, 12.84);
if (corrected24.LegacyState != VixCurveState.StrongContango ||
    corrected25.LegacyState != VixCurveState.Contango ||
    corrected24.State != VixCurveState.StrongContango ||
    corrected25.State != VixCurveState.StrongContango)
    throw new InvalidDataException("The 24/25 June tolerance regression was not corrected.");
if (corrected25.IgnoredMinorInversions < 1)
    throw new InvalidDataException("The small far-tail inversion was not documented.");

VixCurveStatePoint spotStress = VixCurveStateClassifier.Classify(new(2024, 6, 24), june24, 19.0);
if (!spotStress.SpotStress || spotStress.State == VixCurveState.StrongContango ||
    !spotStress.AdjustmentExplanation.Contains("рыночный стресс", StringComparison.Ordinal))
    throw new InvalidDataException("Spot VIX above M1 did not cap and explain strong contango.");
Console.WriteLine($"Curve states: {states.Points.Count:N0}; 2017-11-17={calm.State}; 2020-03-23={covid.State}; SVXY candidates={svxyCandidates:N0}");

if (!string.IsNullOrWhiteSpace(output))
{
    Console.WriteLine(store.ExportWideCsv(output, CancellationToken.None));
    Console.WriteLine(store.ExportStateCsv(Path.ChangeExtension(output, ".states.csv"), CancellationToken.None));
    if (File.ReadLines(output).Skip(1).Select(line => line.Split(',', 2)[0])
        .Any(value => DateTime.TryParse(value, out DateTime date) && !tradingDates.Contains(date.Date)))
        throw new InvalidDataException("The wide VIX export contains non-SPY dates.");
}

Console.WriteLine($"OK: {store.TradingDateCount} SPY dates; excluded {store.ExcludedNonTradingDateCount} source files; " +
                  $"raw range {store.FirstDate:yyyy-MM-dd}..{store.LastDate:yyyy-MM-dd}");
