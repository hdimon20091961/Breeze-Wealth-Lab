using System.Windows;
using System.Windows.Controls;

namespace BreezeVL;

internal sealed class MarketSetupSelectionState
{
    private readonly HashSet<string> _manual = new(StringComparer.Ordinal);
    private HashSet<string> _automatic = new(StringComparer.Ordinal);

    public bool IsSelected(string key) => _manual.Contains(key) || _automatic.Contains(key);

    public void SetManual(string key, bool selected)
    {
        _automatic.Remove(key);
        if (selected) _manual.Add(key); else _manual.Remove(key);
    }

    public void SelectRule(string id, string definition)
    {
        _manual.Clear();
        _automatic = new(StringComparer.Ordinal);
        string formula = definition.ToUpperInvariant();
        if (new[] { "EMA", "E20", "E50", "E100", "E200" }.Any(token => formula.Contains(token, StringComparison.Ordinal)))
            _automatic.Add("MarketSetupEma");
        bool rsiFamily = id.Length == 3 && id[0] == 'R' && char.IsDigit(id[1]) && char.IsDigit(id[2]);
        if (rsiFamily || formula.Contains("RSI", StringComparison.Ordinal))
            _automatic.UnionWith(["MarketSetupMyRsi", "MarketSetupMyRsiFast"]);
        if (id.StartsWith("T03", StringComparison.Ordinal))
            _automatic.UnionWith(["MarketSetupSp50", "MarketSetupSp100", "MarketSetupSp200", "MarketSetupNasdaq100", "MarketSetupAtrp"]);
        if (new[] { "B50", "B100", "B200", "N100", "ШИРОТ" }.Any(token => formula.Contains(token, StringComparison.Ordinal)))
            _automatic.UnionWith(["MarketSetupSp50", "MarketSetupSp100", "MarketSetupSp200", "MarketSetupNasdaq100"]);
        if (formula.Contains("ATRP", StringComparison.Ordinal) || formula.Contains("ATR", StringComparison.Ordinal))
            _automatic.Add("MarketSetupAtrp");
    }
}

// Geometry is shared by the production window and WPF transition regression checks.
internal sealed class MarketSetupPaneLayout(
    Grid outer, Grid lower, FrameworkElement outerSplitter,
    FrameworkElement indicator, FrameworkElement innerSplitter, FrameworkElement atrp)
{
    public void Apply(bool showIndicator, bool showAtrp, double priceRatio, double indicatorRatio)
    {
        bool any = showIndicator || showAtrp;
        lower.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        outerSplitter.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        outer.RowDefinitions[1].Height = new GridLength(any ? 11 : 0);
        outer.RowDefinitions[2].MinHeight = showIndicator && showAtrp ? 180 : showIndicator ? 90 : showAtrp ? 70 : 0;
        priceRatio = double.IsFinite(priceRatio) ? Math.Clamp(priceRatio, .20, .85) : 2.0 / 3.0;
        outer.RowDefinitions[0].Height = new GridLength(any ? priceRatio : 1, GridUnitType.Star);
        outer.RowDefinitions[2].Height = any ? new GridLength(1 - priceRatio, GridUnitType.Star) : new GridLength(0);

        indicator.Visibility = showIndicator ? Visibility.Visible : Visibility.Collapsed;
        atrp.Visibility = showAtrp ? Visibility.Visible : Visibility.Collapsed;
        innerSplitter.Visibility = showIndicator && showAtrp ? Visibility.Visible : Visibility.Collapsed;
        lower.RowDefinitions[1].Height = new GridLength(showIndicator && showAtrp ? 11 : 0);
        lower.RowDefinitions[0].MinHeight = showIndicator ? 90 : 0;
        lower.RowDefinitions[2].MinHeight = showAtrp ? 70 : 0;
        indicatorRatio = double.IsFinite(indicatorRatio) ? Math.Clamp(indicatorRatio, .20, .80) : .62;
        lower.RowDefinitions[0].Height = showIndicator
            ? new GridLength(showAtrp ? indicatorRatio : 1, GridUnitType.Star) : new GridLength(0);
        lower.RowDefinitions[2].Height = showAtrp
            ? new GridLength(showIndicator ? 1 - indicatorRatio : 1, GridUnitType.Star) : new GridLength(0);
    }
}
