using System.Globalization;
using System.Text;

const int ExportContractCount = 12;
const int MaximumCurveDays = 180;
const int MaximumShortExtrapolationDays = 7;

string sourceDirectory = args.ElementAtOrDefault(0) ?? @"D:\fear_greed_close\VIX";
string outputPath = args.ElementAtOrDefault(1) ?? @"D:\fear_greed_close\vix_term_structure_rules2.csv";

if (!Directory.Exists(sourceDirectory))
    throw new DirectoryNotFoundException($"VIX source directory was not found: {sourceDirectory}");

List<SourceFile> sourceFiles = Directory
    .EnumerateFiles(sourceDirectory, "*.csv", SearchOption.TopDirectoryOnly)
    .Select(path => new { Path = path, Name = Path.GetFileNameWithoutExtension(path) })
    .Where(item => DateTime.TryParseExact(item.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out _))
    .Select(item => new SourceFile(
        DateTime.ParseExact(item.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture).Date,
        item.Path))
    .OrderBy(item => item.Date)
    .ToList();

if (sourceFiles.Count == 0)
    throw new InvalidOperationException($"No yyyy-MM-dd.csv VIX source files were found in {sourceDirectory}");

var rows = new List<Rules2Row>(sourceFiles.Count);
foreach (SourceFile source in sourceFiles)
{
    List<TermPoint> points = ReadPoints(source.Path);
    if (points.Count < 2)
        continue;

    bool rollFlag = rows.Count > 0 && points[0].Days > rows[^1].Points[0].Days;
    rows.Add(BuildRow(source.Date, points, rollFlag));
}

if (rows.Count == 0)
    throw new InvalidOperationException("The source files contained no usable VIX term structures.");

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
    ?? throw new InvalidOperationException($"Output path has no directory: {outputPath}"));
string temporaryPath = outputPath + ".tmp";
using (var writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
{
    writer.WriteLine(BuildHeader());
    foreach (Rules2Row row in rows)
        WriteRow(writer, row);
}
File.Move(temporaryPath, outputPath, true);

int interpolated30 = rows.Count(row => row.Price30.Method is FixedPriceMethod.Exact or FixedPriceMethod.Interpolated);
int extrapolated30 = rows.Count(row => row.Price30.Method == FixedPriceMethod.ShortExtrapolated);
int unavailable30 = rows.Count(row => row.Price30.Method == FixedPriceMethod.Unavailable);
int unavailable60 = rows.Count(row => row.Price60.Method == FixedPriceMethod.Unavailable);
int rolls = rows.Count(row => row.RollFlag);

Console.WriteLine($"Created: {outputPath}");
Console.WriteLine($"Rows: {rows.Count:N0} ({rows[0].Date:yyyy-MM-dd} - {rows[^1].Date:yyyy-MM-dd})");
Console.WriteLine($"Price30: {interpolated30:N0} bracketed/exact, {extrapolated30:N0} short extrapolated, {unavailable30:N0} unavailable");
Console.WriteLine($"Price60 unavailable: {unavailable60:N0}");
Console.WriteLine($"Front-roll flags: {rolls:N0}");

static Rules2Row BuildRow(DateTime date, List<TermPoint> points, bool rollFlag)
{
    FixedPrice price30 = CalculateFixedPrice(points, 30);
    FixedPrice price60 = CalculateFixedPrice(points, 60);
    TermPoint first = points[0];
    TermPoint second = points[1];
    int frontSpan = second.Days - first.Days;
    double frontSpreadPct = first.Price > 0
        ? (second.Price / first.Price - 1.0) * 100.0
        : double.NaN;
    double frontSlope30DayPct = frontSpan > 0
        ? frontSpreadPct * 30.0 / frontSpan
        : double.NaN;
    double fixed30To60Pct = price30.Value.HasValue && price60.Value.HasValue && price30.Value.Value > 0
        ? (price60.Value.Value / price30.Value.Value - 1.0) * 100.0
        : double.NaN;

    List<TermPoint> curve = points.Where(point => point.Days <= MaximumCurveDays).ToList();
    int risingPairs = 0;
    int fallingPairs = 0;
    for (int index = 1; index < curve.Count; index++)
    {
        double difference = curve[index].Price - curve[index - 1].Price;
        if (difference > 0.000001)
            risingPairs++;
        else if (difference < -0.000001)
            fallingPairs++;
    }

    int pairCount = Math.Max(0, curve.Count - 1);
    double pairBalance180 = pairCount == 0
        ? double.NaN
        : (risingPairs - fallingPairs) / (double)pairCount;
    string qualityFlag = price30.Method == FixedPriceMethod.Unavailable ||
                         price60.Method == FixedPriceMethod.Unavailable
        ? "INCOMPLETE_FIXED_MATURITY"
        : price30.Method == FixedPriceMethod.ShortExtrapolated ||
          price60.Method == FixedPriceMethod.ShortExtrapolated
            ? "SHORT_EXTRAPOLATION"
            : "OK";

    return new Rules2Row(
        date,
        points,
        price30,
        price60,
        fixed30To60Pct,
        frontSpreadPct,
        frontSlope30DayPct,
        risingPairs,
        fallingPairs,
        pairCount,
        pairBalance180,
        rollFlag,
        qualityFlag);
}

static FixedPrice CalculateFixedPrice(IReadOnlyList<TermPoint> points, int targetDays)
{
    TermPoint? exact = points.FirstOrDefault(point => point.Days == targetDays);
    if (exact is not null)
        return new FixedPrice(exact.Price, FixedPriceMethod.Exact);

    for (int index = 1; index < points.Count; index++)
    {
        TermPoint left = points[index - 1];
        TermPoint right = points[index];
        if (left.Days < targetDays && right.Days > targetDays)
            return new FixedPrice(LinearPrice(left, right, targetDays), FixedPriceMethod.Interpolated);
    }

    if (points.Count >= 2 && targetDays < points[0].Days &&
        points[0].Days - targetDays <= MaximumShortExtrapolationDays)
        return new FixedPrice(LinearPrice(points[0], points[1], targetDays), FixedPriceMethod.ShortExtrapolated);

    if (points.Count >= 2 && targetDays > points[^1].Days &&
        targetDays - points[^1].Days <= MaximumShortExtrapolationDays)
        return new FixedPrice(LinearPrice(points[^2], points[^1], targetDays), FixedPriceMethod.ShortExtrapolated);

    return new FixedPrice(null, FixedPriceMethod.Unavailable);
}

static double LinearPrice(TermPoint left, TermPoint right, int targetDays)
{
    int span = right.Days - left.Days;
    if (span <= 0)
        throw new InvalidDataException("VIX term points must have distinct increasing maturities.");
    double fraction = (targetDays - left.Days) / (double)span;
    return left.Price + fraction * (right.Price - left.Price);
}

static List<TermPoint> ReadPoints(string path)
{
    var points = new List<TermPoint>();
    string[] lines = File.ReadAllLines(path);
    for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
    {
        string line = lines[lineIndex].Trim();
        if (string.IsNullOrWhiteSpace(line))
            continue;
        string[] parts = line.Split(',', ';', '\t');
        if (parts.Length < 2 ||
            !int.TryParse(Clean(parts[0]), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) ||
            !double.TryParse(Clean(parts[1]), NumberStyles.Float, CultureInfo.InvariantCulture, out double price) ||
            days < 0 || price <= 0 || double.IsNaN(price) || double.IsInfinity(price))
            continue;
        points.Add(new TermPoint(days, price));
    }

    return points
        .GroupBy(point => point.Days)
        .Select(group => group.First())
        .OrderBy(point => point.Days)
        .ToList();
}

static string BuildHeader()
{
    var header = new StringBuilder("Date,ContractCount");
    for (int contract = 1; contract <= ExportContractCount; contract++)
        header.Append($",M{contract}_Days,M{contract}_Price");
    header.Append(",Price30,Price30Method,Price60,Price60Method,Fixed30To60Pct");
    header.Append(",FrontSpreadPct,FrontSlope30DayPct");
    header.Append(",RisingPairs180,FallingPairs180,PairCount180,PairBalance180");
    header.Append(",RollFlag,QualityFlag");
    return header.ToString();
}

static void WriteRow(TextWriter writer, Rules2Row row)
{
    writer.Write(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    writer.Write(',');
    writer.Write(row.Points.Count.ToString(CultureInfo.InvariantCulture));
    for (int contract = 0; contract < ExportContractCount; contract++)
    {
        if (contract < row.Points.Count)
        {
            writer.Write(',');
            writer.Write(row.Points[contract].Days.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            WriteDouble(writer, row.Points[contract].Price);
        }
        else
        {
            writer.Write(",,");
        }
    }

    writer.Write(',');
    WriteNullableDouble(writer, row.Price30.Value);
    writer.Write(',');
    writer.Write(row.Price30.Method);
    writer.Write(',');
    WriteNullableDouble(writer, row.Price60.Value);
    writer.Write(',');
    writer.Write(row.Price60.Method);
    writer.Write(',');
    WriteDouble(writer, row.Fixed30To60Pct);
    writer.Write(',');
    WriteDouble(writer, row.FrontSpreadPct);
    writer.Write(',');
    WriteDouble(writer, row.FrontSlope30DayPct);
    writer.Write(',');
    writer.Write(row.RisingPairs180.ToString(CultureInfo.InvariantCulture));
    writer.Write(',');
    writer.Write(row.FallingPairs180.ToString(CultureInfo.InvariantCulture));
    writer.Write(',');
    writer.Write(row.PairCount180.ToString(CultureInfo.InvariantCulture));
    writer.Write(',');
    WriteDouble(writer, row.PairBalance180);
    writer.Write(',');
    writer.Write(row.RollFlag ? "1" : "0");
    writer.Write(',');
    writer.WriteLine(row.QualityFlag);
}

static void WriteNullableDouble(TextWriter writer, double? value)
{
    if (value.HasValue)
        WriteDouble(writer, value.Value);
}

static void WriteDouble(TextWriter writer, double value)
{
    if (!double.IsNaN(value) && !double.IsInfinity(value))
        writer.Write(value.ToString("0.########", CultureInfo.InvariantCulture));
}

static string Clean(string value) => value.Trim().Trim('"');

internal sealed record SourceFile(DateTime Date, string Path);
internal sealed record TermPoint(int Days, double Price);
internal sealed record FixedPrice(double? Value, FixedPriceMethod Method);
internal enum FixedPriceMethod
{
    Exact,
    Interpolated,
    ShortExtrapolated,
    Unavailable
}

internal sealed record Rules2Row(
    DateTime Date,
    IReadOnlyList<TermPoint> Points,
    FixedPrice Price30,
    FixedPrice Price60,
    double Fixed30To60Pct,
    double FrontSpreadPct,
    double FrontSlope30DayPct,
    int RisingPairs180,
    int FallingPairs180,
    int PairCount180,
    double PairBalance180,
    bool RollFlag,
    string QualityFlag);
