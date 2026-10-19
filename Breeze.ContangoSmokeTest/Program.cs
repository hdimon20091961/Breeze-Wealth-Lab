using BreezeVL;

string source = args.ElementAtOrDefault(0) ?? @"D:\fear_greed_close\VIX";
string? output = args.ElementAtOrDefault(1);
VixTermStructureStore store = VixTermStructureStore.BuildIndex(source, CancellationToken.None);

foreach (DateTime requested in new[]
         {
             new DateTime(2008, 10, 16),
             new DateTime(2017, 11, 18),
             new DateTime(2026, 10, 9)
         })
{
    VixTermSnapshot snapshot = store.GetNearest(requested);
    if (snapshot.Points.Count < 2 || snapshot.Points.Any(point => point.Price <= 0))
        throw new InvalidDataException($"Invalid term structure for {requested:yyyy-MM-dd}");
    Console.WriteLine(
        $"{requested:yyyy-MM-dd} -> {snapshot.TradeDate:yyyy-MM-dd}: " +
        $"{snapshot.Points.Count} contracts, 30d={snapshot.Price30:0.0000}, " +
        $"60d={snapshot.Price60:0.0000}, contango={snapshot.Contango30To60:P2}");
}

store.SetSpotVix(new[]
{
    (new DateTime(2008, 10, 16), 69.25),
    (new DateTime(2017, 11, 17), 11.43),
    (new DateTime(2026, 10, 9), 15.72)
});
if (Math.Abs(store.GetNearest(new DateTime(2017, 11, 18)).SpotVix.GetValueOrDefault() - 11.43) > 0.000001)
    throw new InvalidDataException("Nearest spot VIX lookup failed.");

VixCurveStateSeries states = store.BuildStateSeries(CancellationToken.None);
if (states.Points.Count < 4000 || states.Points.Any(point => point.AdjacentPairCount < 1))
    throw new InvalidDataException("Contango state history is incomplete.");
VixCurveStatePoint covid = states.Points.MinBy(point => Math.Abs((point.Date - new DateTime(2020, 3, 23)).Ticks))!;
if (covid.State != VixCurveState.StrongBackwardation || covid.CurveRegimeScore > -0.60)
    throw new InvalidDataException($"Expected broad backwardation near 2020-03-23, got {covid.State}.");
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
VixCurveStatePoint extendedHold = states.Points.Single(point => point.Date == new DateTime(2024, 2, 2));
if (extendedHold.SvxyRecommendation != SvxyRecommendation.ExtendedHold ||
    extendedHold.SvxyBarsSinceCandidate != 231)
    throw new InvalidDataException("Expected an extended SVXY hold on 2024-02-02.");
Console.WriteLine($"Curve states: {states.Points.Count:N0}; 2017-11-17={calm.State}; 2020-03-23={covid.State}; SVXY candidates={svxyCandidates:N0}");

if (!string.IsNullOrWhiteSpace(output))
{
    Console.WriteLine(store.ExportWideCsv(output, CancellationToken.None));
    Console.WriteLine(store.ExportStateCsv(Path.ChangeExtension(output, ".states.csv"), CancellationToken.None));
}

Console.WriteLine($"OK: {store.Count} files, {store.FirstDate:yyyy-MM-dd}..{store.LastDate:yyyy-MM-dd}");
