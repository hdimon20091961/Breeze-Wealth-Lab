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
    IReadOnlyList<TradingForecastPoint> Forecasts,
    int ModelCount = 0,
    string Warning = "");

internal sealed record TradingPackageStatus(
    string PackageName,
    bool Available,
    string Message,
    int ModelCount = 0,
    DateTime? LastDate = null);

internal enum TradingModelEngine
{
    TorchSharp,
    GpuJbin,
    JordanJbin,
    Encog
}

internal sealed record TradingPackageDefinition(
    string Key,
    string DisplayName,
    string RelativeDirectory,
    int CalculationMethod,
    TradingModelEngine Engine);

internal sealed record TradingEnsembleCalculationResult(
    IReadOnlyDictionary<string, TradingEnsembleSeries> Series,
    IReadOnlyList<TradingPackageStatus> Statuses)
{
    public bool AnyAvailable => Statuses.Any(status => status.Available);
    public string Summary => string.Join("; ", Statuses.Select(status =>
        status.Available
            ? $"{status.PackageName}: готов ({status.ModelCount} моделей, до {status.LastDate:yyyy-MM-dd})" +
              (string.IsNullOrWhiteSpace(status.Message) ? "" : $", {status.Message}")
            : $"{status.PackageName}: недоступен ({status.Message})"));
}

internal static class TradingPackageCollector
{
    internal static TradingEnsembleCalculationResult Calculate(
        IReadOnlyList<TradingPackageDefinition> packages,
        Func<TradingPackageDefinition, TradingEnsembleSeries> calculate,
        Action<string>? unavailable,
        CancellationToken cancellationToken)
    {
        var series = new Dictionary<string, TradingEnsembleSeries>(StringComparer.OrdinalIgnoreCase);
        var statuses = new List<TradingPackageStatus>(packages.Count);
        foreach (TradingPackageDefinition package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TradingEnsembleSeries item = calculate(package);
                series[package.Key] = item;
                statuses.Add(new(package.Key, true, item.Warning, item.ModelCount, item.Points.LastOrDefault()?.Date));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                statuses.Add(new(package.Key, false, ex.Message));
                unavailable?.Invoke($"{package.DisplayName} недоступен: {ex.Message}");
            }
        }
        return new(series, statuses);
    }
}
