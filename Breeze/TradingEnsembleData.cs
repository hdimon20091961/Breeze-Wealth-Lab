namespace BreezeVL;

internal sealed record TradingEnsemblePoint(
    DateTime Date,
    double EnsemblePercent,
    IReadOnlyList<double> Deciles);

internal sealed record TradingForecastPoint(
    DateTime Date,
    IReadOnlyList<double> ScenarioPercentages,
    bool IsNextBar);

internal sealed record TradingEnsembleSeries(
    string PackageName,
    IReadOnlyList<TradingEnsemblePoint> Points,
    IReadOnlyList<TradingForecastPoint> Forecasts);
