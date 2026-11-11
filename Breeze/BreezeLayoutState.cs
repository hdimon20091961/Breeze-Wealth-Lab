using System.IO;
using System.Text.Json;

namespace BreezeVL;

internal sealed class BreezeLayoutState
{
    public int Version { get; set; } = 1;
    public string SelectedScoreModel { get; set; } = "Rules1";
    public int ScoreProfile { get; set; } = 1;
    public bool[] TradingCollapsed { get; set; } = new bool[4];
    public double FuzzyAnalysisRatio { get; set; } = 0.25;
    public double TradingAnalysisRatio { get; set; } = 0.25;
    public double TradingPriceBoundary { get; set; } = 0.52;
    public double TradingEnsembleBoundary { get; set; } = 0.63;
    public double TradingDecileBoundary { get; set; } = 0.91;
    public double OverviewCombinedRatio { get; set; } = 2.0 / 3.0;
    public double OverviewFullLeftRatio { get; set; } = 0.50;
    public double OverviewFullTopRatio { get; set; } = 0.50;
    public double ContangoLeftRatio { get; set; } = 0.50;
    public double ContangoTopRatio { get; set; } = 2.0 / 3.0;
    public double TitanicTopRatio { get; set; } = 0.68;
    public double ZweigTopRatio { get; set; } = 0.68;
    public double SentimentTopRatio { get; set; } = 2.0 / 3.0;
    public double MarketSetupAnalysisRatio { get; set; } = 0.42;
    public double MarketSetupTopRatio { get; set; } = 2.0 / 3.0;
    public double MarketSetupIndicatorsRatio { get; set; } = 0.62;
    public double MarketSetupIndicatorsShare { get; set; } = 0.30;
    public double MarketSetupRulesShare { get; set; } = 0.325;
}

internal static class BreezeLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WealthLab8",
        "BreezeLayout.json");

    public static bool TryLoad(out BreezeLayoutState state)
    {
        state = new BreezeLayoutState();
        try
        {
            if (!File.Exists(FilePath))
                return false;
            BreezeLayoutState? loaded = JsonSerializer.Deserialize<BreezeLayoutState>(
                File.ReadAllText(FilePath), JsonOptions);
            if (loaded is null || loaded.Version != 1)
                return false;
            state = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySave(BreezeLayoutState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(directory))
                return false;
            Directory.CreateDirectory(directory);
            string temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, FilePath, true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
