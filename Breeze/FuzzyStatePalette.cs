using System.Windows.Media;

namespace BreezeVL;

internal static class FuzzyStatePalette
{
    public static Color ColorFor(MarketZone zone) => zone switch
    {
        MarketZone.Buy => Color.FromRgb(24, 128, 64),
        MarketZone.PrepareBuy => Color.FromRgb(126, 190, 118),
        MarketZone.Neutral => Colors.White,
        MarketZone.Caution => Colors.White,
        MarketZone.PrepareSell => Color.FromRgb(230, 132, 48),
        MarketZone.ReduceRisk => Color.FromRgb(196, 55, 55),
        _ => Colors.White
    };

    public static string Label(MarketZone zone) => zone switch
    {
        MarketZone.Buy => "Strong buy",
        MarketZone.PrepareBuy => "Buy",
        MarketZone.Neutral => "Neutral",
        MarketZone.Caution => "Neutral (caution)",
        MarketZone.PrepareSell => "Sell",
        MarketZone.ReduceRisk => "Strong sell",
        _ => "Neutral"
    };
}
