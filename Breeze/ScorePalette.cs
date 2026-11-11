namespace BreezeVL;

/// <summary>Shared, opaque, muted five-band palette; missing values are distinct.</summary>
internal static class ScorePalette
{
    internal static readonly string[] Colors = ["#C85151", "#E7AAAA", "#E9D681", "#AED0A3", "#529B69"];
    internal static readonly double[] Rules1Boundaries = [0, 30, 45, 55, 70, 100];
    // Rules2 occupies a narrower raw range, but its trading meaning is anchored
    // at 50 just like Rules1.  The neutral band therefore straddles 50 and the
    // two outer Rules2 boundaries are symmetric around that fixed centre.
    internal static readonly double[] Rules2Boundaries = [0, 41, 45, 55, 59, 100];
    internal const string MissingColor = "#BEBEBE";
    internal static int Band(double value) => Band(value, Rules1Boundaries);
    internal static int Rules2Band(double value) => Band(value, Rules2Boundaries);
    private static int Band(double value, IReadOnlyList<double> boundaries) => !double.IsFinite(value) ? -1 : value switch
    {
        _ when value < boundaries[1] => 0,
        _ when value < boundaries[2] => 1,
        _ when value <= boundaries[3] => 2,
        _ when value < boundaries[4] => 3,
        _ => 4
    };
    internal static string Hex(double value) => Band(value) is int band && band >= 0 ? Colors[band] : MissingColor;
    internal static string Label(double value) => Band(value) switch
    {
        0 => "Very low", 1 => "Low", 2 => "Neutral", 3 => "High", 4 => "Very high", _ => "Unknown"
    };
}
