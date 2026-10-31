namespace BreezeVL;

internal sealed record HoldConfidencePoint(
    DateTime Date,
    double Value,
    double TrendComponent,
    double BreadthComponent,
    double PhaseComponent,
    double VolatilityComponent,
    double ContangoComponent,
    double ZweigComponent,
    double TitanicPenalty,
    double ShockPenalty,
    string Rationale,
    string Profile = "Legacy",
    string MainSupport = "",
    string MainRisk = "",
    string ActiveEvents = "",
    string TopContributions = "",
    string Model = "Rules1",
    int TargetPosition = -1,
    double RawValue = double.NaN,
    double EntryThreshold = 50,
    double ExitThreshold = 50);

internal sealed record HoldConfidenceSeries(
    IReadOnlyList<HoldConfidencePoint> Points,
    bool IsStale = false,
    string Status = "")
{
    public DateTime? LastDate => Points.Count == 0 ? null : Points[^1].Date;
}

internal static class ModelSeriesState
{
    internal static HoldConfidenceSeries? Available(HoldConfidenceSeries series, string warning = "") =>
        series with { IsStale = !string.IsNullOrWhiteSpace(warning), Status = warning };

    internal static HoldConfidenceSeries? Unavailable(HoldConfidenceSeries? previous, string message) =>
        previous is { Points.Count: > 0 }
            ? previous with { IsStale = true, Status = message }
            : null;
}

internal static class RefreshCyclePolicy
{
    internal static bool CanStartExport(Task? current) => current is null || current.IsCompleted;
}
