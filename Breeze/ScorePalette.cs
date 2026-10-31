namespace BreezeVL;

/// <summary>Shared, opaque, muted five-band palette; missing values are distinct.</summary>
internal static class ScorePalette
{
    internal static readonly string[] Colors = ["#C85151", "#E7AAAA", "#E9D681", "#AED0A3", "#529B69"];
    internal const string MissingColor = "#BEBEBE";
    internal static int Band(double value) => !double.IsFinite(value) ? -1 : value switch
    {
        < 30 => 0,
        < 45 => 1,
        <= 55 => 2,
        < 70 => 3,
        _ => 4
    };
    internal static string Hex(double value) => Band(value) is int band && band >= 0 ? Colors[band] : MissingColor;
    internal static string Label(double value) => Band(value) switch
    {
        0 => "Very low", 1 => "Low", 2 => "Neutral", 3 => "High", 4 => "Very high", _ => "Unknown"
    };
}
