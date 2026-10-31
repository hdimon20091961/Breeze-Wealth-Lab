// Standalone Wealth-Lab 8 research strategy. Open this complete file in WL8 C# editor.
// No DLL installation. Standalone optimisation source for the WL8 C# editor.
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
#if !RULES2_ENGINE_ONLY
using WealthLab.Backtest;
using WealthLab.Core;
#endif

namespace BreezeRules2.Research;
public readonly record struct InputRow(DateTime Date, double Open, double High, double Low, double Close,
    double Rsi, double B50, double B100, double B200, double N100,
    double E20, double E50, double E100, double E200, double EligibleB100 = double.NaN,
    double FrontSpreadPct = double.NaN, double M1Days = double.NaN,
    double VixRollFlag = double.NaN, double Atrp = double.NaN,
    double Volume = double.NaN, double AdjustedCurveRegimeScore = double.NaN);

public readonly record struct FeatureRow(DateTime Date, double EarlyTurn, double Failure,
    double Resilience, double EmaContext, double PullbackResume = 0,
    double LowBreadthNewLow = 0, double UnconfirmedStress = 0,
    double BreadthVelocity = 0, double EmaFanTrend = 0,
    double RecoveryAsymmetry = 0, double VolumeConfirmation = 0,
    double BullishBreadthEmaAlignment = 0, double BearishBreadthEmaAlignment = 0,
    double StressBearishEmaAlignment = 0);

public readonly record struct PullbackResumeSettings(int HoldSessions, int SlopeLookback,
    double MinimumSlopeTr, double CrossMarginFraction, double Ema50ToleranceFraction,
    bool RequireBullOrder)
{
    public static PullbackResumeSettings Default => new(5, 5, 0, 0, 0, true);
    public void Validate()
    {
        if (HoldSessions < 2 || SlopeLookback < 1)
            throw new ArgumentException("H03 hold and slope lookbacks must be positive.");
        if (!double.IsFinite(MinimumSlopeTr) || MinimumSlopeTr < 0
            || !double.IsFinite(CrossMarginFraction) || CrossMarginFraction < 0 || CrossMarginFraction > 0.1
            || !double.IsFinite(Ema50ToleranceFraction) || Ema50ToleranceFraction < 0 || Ema50ToleranceFraction > 0.1)
            throw new ArgumentException("Invalid H03 threshold.");
    }
}

public readonly record struct LowBreadthNewLowSettings(int PriceLookback, double BreadthUpperLevel,
    double ParticipantToleranceSteps, double ExtraTolerancePoints, double MinimumPriceBreakFraction)
{
    public static LowBreadthNewLowSettings Default => new(20, 10, 1, 0, 0);
    public void Validate()
    {
        if (PriceLookback < 2) throw new ArgumentException("H06 price lookback must be at least 2.");
        if (!double.IsFinite(BreadthUpperLevel) || BreadthUpperLevel < 0 || BreadthUpperLevel > 100
            || !double.IsFinite(ParticipantToleranceSteps) || ParticipantToleranceSteps < 0 || ParticipantToleranceSteps > 10
            || !double.IsFinite(ExtraTolerancePoints) || ExtraTolerancePoints < 0 || ExtraTolerancePoints > 100
            || !double.IsFinite(MinimumPriceBreakFraction) || MinimumPriceBreakFraction < 0 || MinimumPriceBreakFraction > 0.1)
            throw new ArgumentException("Invalid H06 threshold.");
    }
}

public readonly record struct UnconfirmedStressSettings(int MinimumAgeSessions,
    double RsiUpperLevel, double Ema20MarginFraction, double MinimumFrontSpreadPct,
    int MinimumM1Days)
{
    public static UnconfirmedStressSettings Default => new(1, 0, 0, 0, 0);
    public void Validate()
    {
        if (MinimumAgeSessions < 1 || MinimumAgeSessions > 100)
            throw new ArgumentException("H05 minimum age must be between 1 and 100 sessions.");
        if (!double.IsFinite(RsiUpperLevel) || RsiUpperLevel < -1 || RsiUpperLevel > 1
            || !double.IsFinite(Ema20MarginFraction) || Ema20MarginFraction < 0 || Ema20MarginFraction > 0.1
            || !double.IsFinite(MinimumFrontSpreadPct) || MinimumFrontSpreadPct < -100 || MinimumFrontSpreadPct > 100
            || MinimumM1Days < 0 || MinimumM1Days > 365)
            throw new ArgumentException("Invalid H05 threshold.");
    }
}

public readonly record struct BreadthVelocitySettings(int LookbackSessions,
    double SpeedScalePointsPerSession, int MinimumAgreementCount,
    double MaximumVelocitySpread, double MinimumMeanAbsoluteSpeed)
{
    public static BreadthVelocitySettings Default => new(5, 1, 3, 10, 0);
    public void Validate()
    {
        if (LookbackSessions < 1 || LookbackSessions > 100)
            throw new ArgumentException("F01 lookback must be between 1 and 100 sessions.");
        if (!double.IsFinite(SpeedScalePointsPerSession) || SpeedScalePointsPerSession <= 0
            || !double.IsFinite(MaximumVelocitySpread) || MaximumVelocitySpread < 0
            || !double.IsFinite(MinimumMeanAbsoluteSpeed) || MinimumMeanAbsoluteSpeed < 0
            || MinimumAgreementCount < 1 || MinimumAgreementCount > 4)
            throw new ArgumentException("Invalid F01 threshold.");
    }
}

public readonly record struct EmaFanTrendSettings(int SlopeLookbackSessions,
    double StrengthScale, double SlopeMultiplier, int MinimumAgreementCount,
    double MinimumStrength)
{
    public static EmaFanTrendSettings Default => new(5, 1, 5, 4, 0);
    public void Validate()
    {
        if (SlopeLookbackSessions < 1 || SlopeLookbackSessions > 100)
            throw new ArgumentException("F02 slope lookback must be between 1 and 100 sessions.");
        if (!double.IsFinite(StrengthScale) || StrengthScale <= 0
            || !double.IsFinite(SlopeMultiplier) || SlopeMultiplier <= 0
            || MinimumAgreementCount < 1 || MinimumAgreementCount > 7
            || !double.IsFinite(MinimumStrength) || MinimumStrength < 0)
            throw new ArgumentException("Invalid F02 threshold.");
    }
}

public readonly record struct RecoveryAsymmetrySettings(double MinimumDrawdownFraction,
    double MinimumRecoveryFraction, int MinimumFallAge, int MaximumRecoveryAge,
    double AsymmetryScale)
{
    public const int PeakLookbackSessions = 63;
    public static RecoveryAsymmetrySettings Default => new(0.02, 0, 0, 20, 1);
    public void Validate()
    {
        if (!double.IsFinite(MinimumDrawdownFraction) || MinimumDrawdownFraction < 0 || MinimumDrawdownFraction > 1
            || !double.IsFinite(MinimumRecoveryFraction) || MinimumRecoveryFraction < 0 || MinimumRecoveryFraction > 1
            || MinimumFallAge < 0 || MinimumFallAge >= PeakLookbackSessions
            || MaximumRecoveryAge < 1 || MaximumRecoveryAge >= PeakLookbackSessions
            || !double.IsFinite(AsymmetryScale) || AsymmetryScale <= 0)
            throw new ArgumentException("Invalid F03 threshold.");
    }
}

public readonly record struct VolumeConfirmationSettings(int MedianLookbackSessions,
    double VolumeRatioCap, double MinimumPriceMoveAtr, double FeatureScale,
    double MinimumVolumeRatio)
{
    public static VolumeConfirmationSettings Default => new(20, 2, 0, 0.5, 1);
    public void Validate()
    {
        if (MedianLookbackSessions < 2 || MedianLookbackSessions > 252)
            throw new ArgumentException("F04 volume lookback must be between 2 and 252 sessions.");
        if (!double.IsFinite(VolumeRatioCap) || VolumeRatioCap <= 1
            || !double.IsFinite(MinimumPriceMoveAtr) || MinimumPriceMoveAtr < 0
            || !double.IsFinite(FeatureScale) || FeatureScale <= 0
            || !double.IsFinite(MinimumVolumeRatio) || MinimumVolumeRatio <= 0
            || MinimumVolumeRatio > VolumeRatioCap)
            throw new ArgumentException("Invalid F04 threshold.");
    }
}

public readonly record struct FeatureSettings(double SaturationThreshold, int RisingBreadths,
    int WatchSessions, int FallingBreadths, int ResilienceShortLookback,
    int ResilienceLongLookback, double B50Level, double B200Level,
    double MinimumB50Decline, double MinimumB200Increase,
    double MinimumBullishBodyFraction)
{
    public static FeatureSettings Default => new(-1, 3, 5, 3, 5, 20, 50, 50, 0, 0, 0);
    public void Validate()
    {
        if (!double.IsFinite(SaturationThreshold) || SaturationThreshold < -1 || SaturationThreshold > 1)
            throw new ArgumentException("RSI saturation threshold must be between -1 and 1.");
        if (RisingBreadths < 1 || RisingBreadths > 4 || FallingBreadths < 1 || FallingBreadths > 4)
            throw new ArgumentException("Breadth confirmation count must be between 1 and 4.");
        if (WatchSessions < 1 || ResilienceShortLookback < 1 || ResilienceLongLookback <= ResilienceShortLookback)
            throw new ArgumentException("Lookbacks must be positive and H04 long must exceed H04 short.");
        if (!double.IsFinite(B50Level) || B50Level < 0 || B50Level > 100
            || !double.IsFinite(B200Level) || B200Level < 0 || B200Level > 100)
            throw new ArgumentException("Breadth levels must be between 0 and 100.");
        if (!double.IsFinite(MinimumB50Decline) || MinimumB50Decline < 0 || MinimumB50Decline > 100)
            throw new ArgumentException("Minimum B50 decline must be between 0 and 100.");
        if (!double.IsFinite(MinimumB200Increase) || MinimumB200Increase < 0 || MinimumB200Increase > 100)
            throw new ArgumentException("Minimum B200 increase must be between 0 and 100.");
        if (!double.IsFinite(MinimumBullishBodyFraction) || MinimumBullishBodyFraction < 0 || MinimumBullishBodyFraction > 1)
            throw new ArgumentException("Minimum bullish body fraction must be between 0 and 1.");
    }
}

public readonly record struct ScoreWeights(double Failure, double Resilience, double EmaContext,
    double PullbackResume = 0, double LowBreadthNewLow = 0, double UnconfirmedStress = 0,
    double BreadthVelocity = 0, double EmaFanTrend = 0, double RecoveryAsymmetry = 0,
    double VolumeConfirmation = 0, double BullishBreadthEmaAlignment = 0,
    double BearishBreadthEmaAlignment = 0, double StressBearishEmaAlignment = 0)
{
    private double Denominator => 1 + Math.Abs(Failure) + Math.Abs(Resilience)
        + Math.Abs(EmaContext) + Math.Abs(PullbackResume) + Math.Abs(LowBreadthNewLow)
        + Math.Abs(UnconfirmedStress) + Math.Abs(BreadthVelocity) + Math.Abs(EmaFanTrend)
        + Math.Abs(RecoveryAsymmetry) + Math.Abs(VolumeConfirmation)
        + Math.Abs(BullishBreadthEmaAlignment) + Math.Abs(BearishBreadthEmaAlignment)
        + Math.Abs(StressBearishEmaAlignment);
    public double MinimumScore => 50 + 50 * (Math.Min(0, Failure) + Math.Min(0, Resilience)
        - Math.Abs(EmaContext) + Math.Min(0, PullbackResume) + Math.Min(0, LowBreadthNewLow)
        + Math.Min(0, UnconfirmedStress) - Math.Abs(BreadthVelocity)
        - Math.Abs(EmaFanTrend) + Math.Min(0, RecoveryAsymmetry)
        - Math.Abs(VolumeConfirmation) + Math.Min(0, BullishBreadthEmaAlignment)
        + Math.Min(0, BearishBreadthEmaAlignment) + Math.Min(0, StressBearishEmaAlignment)) / Denominator;
    public double MaximumScore => 50 + 50 * (1 + Math.Max(0, Failure) + Math.Max(0, Resilience)
        + Math.Abs(EmaContext) + Math.Max(0, PullbackResume) + Math.Max(0, LowBreadthNewLow)
        + Math.Max(0, UnconfirmedStress) + Math.Abs(BreadthVelocity)
        + Math.Abs(EmaFanTrend) + Math.Max(0, RecoveryAsymmetry)
        + Math.Abs(VolumeConfirmation) + Math.Max(0, BullishBreadthEmaAlignment)
        + Math.Max(0, BearishBreadthEmaAlignment) + Math.Max(0, StressBearishEmaAlignment)) / Denominator;
    public double Score(FeatureRow row)
    {
        // H01 is the fixed reference weight +1. Unknown is never a neutral vote.
        double sum = row.EarlyTurn;
        if (!double.IsFinite(sum)) return double.NaN;
        void Add(double weight, double value)
        {
            if (!double.IsFinite(weight)) throw new ArgumentException("Nonfinite weight");
            if (weight == 0) return;
            sum += weight * value;
        }
        Add(Failure, row.Failure); Add(Resilience, row.Resilience); Add(EmaContext, row.EmaContext);
        Add(PullbackResume, row.PullbackResume);
        Add(LowBreadthNewLow, row.LowBreadthNewLow);
        Add(UnconfirmedStress, row.UnconfirmedStress);
        Add(BreadthVelocity, row.BreadthVelocity);
        Add(EmaFanTrend, row.EmaFanTrend);
        Add(RecoveryAsymmetry, row.RecoveryAsymmetry);
        Add(VolumeConfirmation, row.VolumeConfirmation);
        Add(BullishBreadthEmaAlignment, row.BullishBreadthEmaAlignment);
        Add(BearishBreadthEmaAlignment, row.BearishBreadthEmaAlignment);
        Add(StressBearishEmaAlignment, row.StressBearishEmaAlignment);
        if (!double.IsFinite(sum)) return double.NaN;
        return Math.Clamp(50 + 50 * sum / Denominator, 0, 100);
    }
}

public enum Rules2Action { Hold, Buy, Sell }

public static class Rules2Trading
{
    public static void Validate(ScoreWeights weights, double entry, double exit)
    {
        if (!double.IsFinite(entry) || !double.IsFinite(exit))
            throw new ArgumentException("Entry and Exit must be finite.");
        if (exit < weights.MinimumScore - 1e-9)
            throw new ArgumentException($"Exit {exit:F2} is unreachable: minimum possible score {weights.MinimumScore:F2}.");
        if (entry > weights.MaximumScore + 1e-9)
            throw new ArgumentException($"Entry {entry:F2} is unreachable: maximum possible score {weights.MaximumScore:F2}.");
    }
    // State resolves equal or overlapping thresholds: flat tests Entry, holding tests Exit.
    // An overlap can deliberately increase switching; the backtest exposes this in trade count and costs.
    // HOLD preserves the real position; a falling spike is not an automatic sell.
    public static Rules2Action Decide(double score, bool holding, double entry, double exit)
    {
        if (!double.IsFinite(score)) return Rules2Action.Hold;
        if (!holding && score >= entry) return Rules2Action.Buy;
        if (holding && score <= exit) return Rules2Action.Sell;
        return Rules2Action.Hold;
    }
}

public static class Rules2Engine
{
    public const double SaturationEpsilon = 1e-6;
    private static bool Fin(params double[] values) => values.All(double.IsFinite);
    private static double[] Breadths(InputRow row) => [row.B50, row.B100, row.B200, row.N100];

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows) =>
        Calculate(rows, FeatureSettings.Default, PullbackResumeSettings.Default,
            LowBreadthNewLowSettings.Default, UnconfirmedStressSettings.Default,
            BreadthVelocitySettings.Default, EmaFanTrendSettings.Default);

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows, FeatureSettings settings) =>
        Calculate(rows, settings, PullbackResumeSettings.Default,
            LowBreadthNewLowSettings.Default, UnconfirmedStressSettings.Default,
            BreadthVelocitySettings.Default, EmaFanTrendSettings.Default);

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows,
        FeatureSettings settings, PullbackResumeSettings h03) =>
        Calculate(rows, settings, h03, LowBreadthNewLowSettings.Default,
            UnconfirmedStressSettings.Default, BreadthVelocitySettings.Default,
            EmaFanTrendSettings.Default, RecoveryAsymmetrySettings.Default);

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows,
        FeatureSettings settings, PullbackResumeSettings h03, LowBreadthNewLowSettings h06)
        => Calculate(rows, settings, h03, h06, UnconfirmedStressSettings.Default,
            BreadthVelocitySettings.Default, EmaFanTrendSettings.Default);

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows,
        FeatureSettings settings, PullbackResumeSettings h03, LowBreadthNewLowSettings h06,
        UnconfirmedStressSettings h05)
        => Calculate(rows, settings, h03, h06, h05, BreadthVelocitySettings.Default,
            EmaFanTrendSettings.Default);

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows,
        FeatureSettings settings, PullbackResumeSettings h03, LowBreadthNewLowSettings h06,
        UnconfirmedStressSettings h05, BreadthVelocitySettings f01)
        => Calculate(rows, settings, h03, h06, h05, f01, EmaFanTrendSettings.Default);

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows,
        FeatureSettings settings, PullbackResumeSettings h03, LowBreadthNewLowSettings h06,
        UnconfirmedStressSettings h05, BreadthVelocitySettings f01, EmaFanTrendSettings f02)
        => Calculate(rows, settings, h03, h06, h05, f01, f02,
            RecoveryAsymmetrySettings.Default);

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows,
        FeatureSettings settings, PullbackResumeSettings h03, LowBreadthNewLowSettings h06,
        UnconfirmedStressSettings h05, BreadthVelocitySettings f01, EmaFanTrendSettings f02,
        RecoveryAsymmetrySettings f03)
        => Calculate(rows, settings, h03, h06, h05, f01, f02, f03,
            VolumeConfirmationSettings.Default);

    public static ReadOnlyCollection<FeatureRow> Calculate(IReadOnlyList<InputRow> rows,
        FeatureSettings settings, PullbackResumeSettings h03, LowBreadthNewLowSettings h06,
        UnconfirmedStressSettings h05, BreadthVelocitySettings f01, EmaFanTrendSettings f02,
        RecoveryAsymmetrySettings f03, VolumeConfirmationSettings f04)
    {
        settings.Validate();
        h03.Validate();
        h06.Validate();
        h05.Validate();
        f01.Validate();
        f02.Validate();
        f03.Validate();
        f04.Validate();
        var output = new FeatureRow[rows.Count];
        bool? previousQ = null;
        bool? previousH06 = null;
        int h05Age = 0;
        var watches = new List<(int Index, double Low)>();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (i > 0 && r.Date <= rows[i - 1].Date) throw new ArgumentException("Dates must strictly increase");
            var b = Breadths(r);
            var prev = i > 0 ? Breadths(rows[i - 1]) : [double.NaN, double.NaN, double.NaN, double.NaN];
            bool valid = Fin(r.Open, r.High, r.Low, r.Close, r.Rsi) && b.All(double.IsFinite) && prev.All(double.IsFinite)
                && r.Low <= Math.Min(r.Open, r.Close) && r.High >= Math.Max(r.Open, r.Close);
            double early = double.NaN, failure = double.NaN;
            if (valid)
            {
                bool saturated = r.Rsi <= settings.SaturationThreshold + SaturationEpsilon;
                double dayRange = r.High - r.Low;
                bool bodyPass = r.Close > r.Open && dayRange >= 0
                    && (dayRange == 0 ? settings.MinimumBullishBodyFraction == 0
                        : (r.Close - r.Open) / dayRange + 1e-12 >= settings.MinimumBullishBodyFraction);
                bool q = saturated && bodyPass
                    && Enumerable.Range(0, 4).Count(k => b[k] > prev[k]) >= settings.RisingBreadths;
                if (previousQ.HasValue) early = q && !previousQ.Value ? 1 : 0;
                else if (!q) early = 0; // True after an unknown never creates a fabricated rising edge.
                failure = 0;
                for (int j = watches.Count - 1; j >= 0; j--)
                {
                    var w = watches[j];
                    if (!saturated || i - w.Index > settings.WatchSessions) { watches.RemoveAt(j); continue; }
                    if (r.Close < w.Low
                        && Enumerable.Range(0, 4).Count(k => b[k] <= prev[k]) >= settings.FallingBreadths)
                    { failure = 1; watches.RemoveAt(j); }
                }
                if (early == 1) watches.Add((i, r.Low));
                previousQ = q;
            }
            else { previousQ = null; watches.Clear(); }

            double resilience = double.NaN;
            int longLookback = settings.ResilienceLongLookback;
            if (i >= longLookback && Enumerable.Range(i - longLookback, longLookback + 1)
                .All(k => Fin(rows[k].B50, rows[k].B200)))
                resilience = r.B50 < settings.B50Level
                    && r.B50 < rows[i - settings.ResilienceShortLookback].B50 - settings.MinimumB50Decline
                    && r.B200 > settings.B200Level
                    && r.B200 >= rows[i - longLookback].B200 + settings.MinimumB200Increase ? 1 : 0;
            double context = !Fin(r.E20, r.E50, r.E100, r.E200) ? double.NaN
                : r.E20 > r.E50 && r.E50 > r.E100 && r.E100 > r.E200 ? 1
                : r.E20 < r.E50 && r.E50 < r.E100 && r.E100 < r.E200 ? -1 : 0;

            double pullbackResume = double.NaN;
            int firstHold = i - h03.HoldSessions + 1;
            int slopeStart = i - h03.SlopeLookback;
            if (i >= 2 && firstHold >= 0 && slopeStart >= 0)
            {
                bool requiredKnown = Enumerable.Range(firstHold, h03.HoldSessions)
                    .All(k => Fin(rows[k].Close, rows[k].E50))
                    && Fin(r.Close, r.E20, r.E50, r.E100, r.E200,
                        rows[i - 1].Close, rows[i - 1].E20, rows[slopeStart].E50)
                    && Fin(rows[i - 1].High, rows[i - 1].Low, rows[i - 1].Close, rows[i - 2].Close);
                if (requiredKnown)
                {
                    var previous = rows[i - 1];
                    double previousTr = Math.Max(previous.High - previous.Low,
                        Math.Max(Math.Abs(previous.High - rows[i - 2].Close),
                            Math.Abs(previous.Low - rows[i - 2].Close)));
                    if (previousTr > 0)
                    {
                        bool crossed = r.Close > r.E20 * (1 + h03.CrossMarginFraction)
                            && previous.Close <= previous.E20 * (1 + h03.CrossMarginFraction);
                        bool held50 = Enumerable.Range(firstHold, h03.HoldSessions)
                            .All(k => rows[k].Close >= rows[k].E50 * (1 - h03.Ema50ToleranceFraction));
                        bool bullOrder = r.E20 > r.E50 && r.E50 > r.E100 && r.E100 > r.E200;
                        double slopeTr = (r.E50 - rows[slopeStart].E50) / previousTr;
                        pullbackResume = crossed && held50
                            && (!h03.RequireBullOrder || bullOrder)
                            && slopeTr >= h03.MinimumSlopeTr ? 1 : 0;
                    }
                }
            }
            double lowBreadthNewLow = double.NaN;
            if (i >= h06.PriceLookback)
            {
                int start = i - h06.PriceLookback;
                bool known = Fin(r.Close, r.B100, r.EligibleB100) && r.EligibleB100 > 0;
                int comparison = -1;
                double comparisonClose = double.PositiveInfinity;
                for (int k = start; known && k < i; k++)
                {
                    if (!Fin(rows[k].Close)) { known = false; break; }
                    if (rows[k].Close <= comparisonClose) { comparisonClose = rows[k].Close; comparison = k; }
                }
                if (known && comparison >= 0)
                {
                    var priorLow = rows[comparison];
                    known = Fin(priorLow.B100, priorLow.EligibleB100) && priorLow.EligibleB100 > 0;
                    if (known)
                    {
                        double participantStep = 100.0 / Math.Min(r.EligibleB100, priorLow.EligibleB100);
                        bool state = r.Close < comparisonClose * (1 - h06.MinimumPriceBreakFraction)
                            && r.B100 <= h06.BreadthUpperLevel
                            && r.B100 >= priorLow.B100
                                - h06.ParticipantToleranceSteps * participantStep
                                - h06.ExtraTolerancePoints - 1e-6;
                        if (previousH06.HasValue) lowBreadthNewLow = state && !previousH06.Value ? 1 : 0;
                        else if (!state) lowBreadthNewLow = 0;
                        previousH06 = state;
                    }
                }
                if (!known) previousH06 = null;
            }
            else previousH06 = null;

            double unconfirmedStress;
            bool h05Known = Fin(r.Close, r.E20, r.Rsi, r.FrontSpreadPct, r.M1Days, r.VixRollFlag)
                && r.E20 > 0 && r.M1Days >= 0 && (r.VixRollFlag == 0 || r.VixRollFlag == 1);
            // On an expiry roll the raw M1/M2 identity changes, but the corrected
            // fixed-maturity curve score remains comparable across sessions.  A
            // roll is therefore unknown only when that corrected score is absent.
            bool correctedRollKnown = r.VixRollFlag == 1
                && double.IsFinite(r.AdjustedCurveRegimeScore);
            if (!h05Known || (r.VixRollFlag == 1 && !correctedRollKnown))
            {
                h05Age = 0;
                unconfirmedStress = double.NaN;
            }
            else
            {
                bool correctedContango = double.IsFinite(r.AdjustedCurveRegimeScore)
                    ? r.AdjustedCurveRegimeScore >= 0.18
                    : r.FrontSpreadPct > h05.MinimumFrontSpreadPct;
                bool state = r.Close < r.E20 * (1 - h05.Ema20MarginFraction)
                    && r.Rsi < h05.RsiUpperLevel
                    && r.FrontSpreadPct > h05.MinimumFrontSpreadPct
                    && correctedContango
                    && r.M1Days >= h05.MinimumM1Days;
                h05Age = state ? h05Age + 1 : 0;
                unconfirmedStress = state && h05Age >= h05.MinimumAgeSessions ? 1 : 0;
            }

            double breadthVelocity = double.NaN;
            if (i >= f01.LookbackSessions)
            {
                double[] currentBreadths = Breadths(r);
                double[] priorBreadths = Breadths(rows[i - f01.LookbackSessions]);
                if (currentBreadths.All(double.IsFinite) && priorBreadths.All(double.IsFinite))
                {
                    double[] velocities = Enumerable.Range(0, 4)
                        .Select(k => (currentBreadths[k] - priorBreadths[k]) / f01.LookbackSessions)
                        .ToArray();
                    int positive = velocities.Count(value => value > 1e-12);
                    int negative = velocities.Count(value => value < -1e-12);
                    int agreement = Math.Max(positive, negative);
                    double coherence = (positive - negative) / 4.0;
                    double meanAbsoluteSpeed = velocities.Average(Math.Abs);
                    double spread = velocities.Max() - velocities.Min();
                    breadthVelocity = agreement >= f01.MinimumAgreementCount
                        && meanAbsoluteSpeed + 1e-12 >= f01.MinimumMeanAbsoluteSpeed
                        && spread <= f01.MaximumVelocitySpread + 1e-12
                        ? coherence * Math.Tanh(meanAbsoluteSpeed / f01.SpeedScalePointsPerSession)
                        : 0;
                }
            }

            double emaFanTrend = double.NaN;
            if (i >= f02.SlopeLookbackSessions)
            {
                var priorEma = rows[i - f02.SlopeLookbackSessions];
                double atr = r.Close * r.Atrp / 100.0;
                if (Fin(r.Close, r.Atrp, r.E20, r.E50, r.E100, r.E200,
                        priorEma.E20, priorEma.E50, priorEma.E100, priorEma.E200)
                    && atr > 0)
                {
                    double[] gaps =
                    [
                        (r.E20 - r.E50) / atr,
                        (r.E50 - r.E100) / atr,
                        (r.E100 - r.E200) / atr
                    ];
                    double[] slopes =
                    [
                        (r.E20 - priorEma.E20) / atr / f02.SlopeLookbackSessions,
                        (r.E50 - priorEma.E50) / atr / f02.SlopeLookbackSessions,
                        (r.E100 - priorEma.E100) / atr / f02.SlopeLookbackSessions,
                        (r.E200 - priorEma.E200) / atr / f02.SlopeLookbackSessions
                    ];
                    double[] components = gaps.Concat(slopes).ToArray();
                    int positive = components.Count(value => value > 1e-12);
                    int negative = components.Count(value => value < -1e-12);
                    int agreement = Math.Max(positive, negative);
                    double coherence = (positive - negative) / 7.0;
                    double gapStrength = gaps.Average(Math.Abs);
                    double slopeStrength = slopes.Average(Math.Abs);
                    double strength = (gapStrength + f02.SlopeMultiplier * slopeStrength) / 2.0;
                    emaFanTrend = agreement >= f02.MinimumAgreementCount
                        && strength + 1e-12 >= f02.MinimumStrength
                        ? coherence * Math.Tanh(strength / f02.StrengthScale)
                        : 0;
                }
            }

            double recoveryAsymmetry = double.NaN;
            int recoveryStart = i - RecoveryAsymmetrySettings.PeakLookbackSessions + 1;
            if (recoveryStart >= 0)
            {
                bool known = Enumerable.Range(recoveryStart, RecoveryAsymmetrySettings.PeakLookbackSessions)
                    .All(k => Fin(rows[k].Close) && rows[k].Close > 0);
                if (known)
                {
                    int peak = recoveryStart;
                    for (int k = recoveryStart + 1; k <= i; k++)
                        if (rows[k].Close >= rows[peak].Close) peak = k;
                    int trough = peak;
                    for (int k = peak + 1; k <= i; k++)
                        if (rows[k].Close <= rows[trough].Close) trough = k;
                    double peakClose = rows[peak].Close;
                    double troughClose = rows[trough].Close;
                    if (peakClose <= troughClose + 1e-12)
                    {
                        recoveryAsymmetry = 0; // NotApplicable: no observed fall from the current anchor.
                    }
                    else
                    {
                        double drawdown = (peakClose - troughClose) / peakClose;
                        double recoveryFraction = Math.Clamp((r.Close - troughClose) / (peakClose - troughClose), 0, 1);
                        int fallAge = trough - peak;
                        int recoveryAge = i - trough;
                        double asymmetry = recoveryFraction * fallAge / Math.Max(1.0, recoveryAge);
                        recoveryAsymmetry = recoveryAge >= 1
                            && recoveryAge <= f03.MaximumRecoveryAge
                            && fallAge >= f03.MinimumFallAge
                            && drawdown + 1e-12 >= f03.MinimumDrawdownFraction
                            && recoveryFraction + 1e-12 >= f03.MinimumRecoveryFraction
                            ? Math.Tanh(asymmetry / f03.AsymmetryScale)
                            : 0;
                    }
                }
            }

            double volumeConfirmation = double.NaN;
            if (i >= f04.MedianLookbackSessions)
            {
                bool known = Fin(r.Close, r.Atrp, r.Volume, rows[i - 1].Close)
                    && r.Close > 0 && r.Atrp > 0 && r.Volume > 0;
                var priorVolumes = new double[f04.MedianLookbackSessions];
                for (int k = 0; known && k < priorVolumes.Length; k++)
                {
                    double value = rows[i - f04.MedianLookbackSessions + k].Volume;
                    if (!double.IsFinite(value) || value <= 0) known = false;
                    else priorVolumes[k] = value;
                }
                if (known)
                {
                    Array.Sort(priorVolumes);
                    int middle = priorVolumes.Length / 2;
                    double median = priorVolumes.Length % 2 == 0
                        ? (priorVolumes[middle - 1] + priorVolumes[middle]) / 2.0
                        : priorVolumes[middle];
                    if (median > 0)
                    {
                        double volumeRatio = r.Volume / median;
                        double atr = r.Close * r.Atrp / 100.0;
                        double priceMoveAtr = (r.Close - rows[i - 1].Close) / atr;
                        double cappedRatio = Math.Min(volumeRatio, f04.VolumeRatioCap);
                        double raw = (cappedRatio - 1.0) * priceMoveAtr;
                        volumeConfirmation = volumeRatio + 1e-12 >= f04.MinimumVolumeRatio
                            && Math.Abs(priceMoveAtr) + 1e-12 >= f04.MinimumPriceMoveAtr
                            ? Math.Tanh(raw / f04.FeatureScale)
                            : 0;
                    }
                }
            }

            double bullishBreadthEmaAlignment = Fin(breadthVelocity, emaFanTrend)
                ? Math.Max(0, breadthVelocity) * Math.Max(0, emaFanTrend) : double.NaN;
            double bearishBreadthEmaAlignment = Fin(breadthVelocity, emaFanTrend)
                ? Math.Max(0, -breadthVelocity) * Math.Max(0, -emaFanTrend) : double.NaN;
            double stressBearishEmaAlignment = Fin(unconfirmedStress, emaFanTrend)
                ? unconfirmedStress * Math.Max(0, -emaFanTrend) : double.NaN;
            output[i] = new(r.Date, early, failure, resilience, context, pullbackResume,
                lowBreadthNewLow, unconfirmedStress, breadthVelocity, emaFanTrend,
                recoveryAsymmetry, volumeConfirmation, bullishBreadthEmaAlignment,
                bearishBreadthEmaAlignment, stressBearishEmaAlignment);
        }
        return Array.AsReadOnly(output);
    }
}

public sealed class ResearchData
{
    public const string DefaultDirectory = @"D:\CODEX_PRG\Breadth\research\rules2_wl8_v1\data";
    public const string DefaultRules2VixPath = @"D:\fear_greed_close\vix_term_structure_rules2.csv";
    // One immutable snapshot per WL process, all optimisation instances share only readonly values.
    private static readonly Lazy<ResearchData> Shared = new(() => Load(DefaultDirectory), true);
    public static ResearchData Default => Shared.Value;
    public ReadOnlyCollection<InputRow> Inputs { get; }
    public ReadOnlyCollection<FeatureRow> Features { get; }
    public IReadOnlyDictionary<DateTime, int> ByDate { get; }
    private ResearchData(List<InputRow> inputs)
    {
        Inputs = inputs.AsReadOnly(); Features = Rules2Engine.Calculate(Inputs);
        ByDate = new ReadOnlyDictionary<DateTime, int>(inputs.Select((r, i) => (r.Date, i)).ToDictionary(x => x.Date, x => x.i));
    }
    public static ResearchData Load(string directory) => Load(directory, DefaultRules2VixPath);

    public static ResearchData Load(string directory, string rules2VixPath)
    {
        // Read the six CSV files directly. No JSON or additional assembly references.
        var price = Read(directory, "SPY.csv");
        var tech = Read(directory, "spy_ema_myrsi_agc.csv");
        var breadth = new[] { "sp500_sma50.csv", "sp500_sma100.csv", "sp500_sma200.csv", "nasdaq100_sma100.csv" }
            .Select(f => Read(directory, f)).ToArray();
        var vix = ReadPath(rules2VixPath);
        var inputs = new List<InputRow>();
        foreach (var (date, p) in price.OrderBy(x => x.Key))
        {
            tech.TryGetValue(date, out var t);
            var b = breadth.Select(table => table.GetValueOrDefault(date)).ToArray();
            vix.TryGetValue(date, out var vx);
            inputs.Add(new(date, Get(p, "Open"), Get(p, "High"), Get(p, "Low"), Get(p, "Close"),
                Get(t, "MyRSI_rep_AGC_14_8"), Get(b[0], "Close"), Get(b[1], "Close"), Get(b[2], "Close"), Get(b[3], "Close"),
                Get(t, "EMA20"), Get(t, "EMA50"), Get(t, "EMA100"), Get(t, "EMA200"), Get(b[1], "EligibleCount"),
                Get(vx, "FrontSpreadPct"), Get(vx, "M1_Days"), Get(vx, "RollFlag"), Get(t, "ATRP_1"),
                Get(p, "Volume"), Get(vx, "AdjustedCurveRegimeScore")));
        }
        if (inputs.Count == 0) throw new InvalidDataException("Empty SPY snapshot");
        foreach (var row in inputs)
        {
            bool Bad(double value, double low, double high) => double.IsFinite(value) && (value < low || value > high);
            if (Bad(row.Rsi, -1.000001, 1.000001) || new[] {row.B50,row.B100,row.B200,row.N100}.Any(v=>Bad(v,0,100)))
                throw new InvalidDataException($"Invalid RSI/breadth range on {row.Date:yyyy-MM-dd}");
            if (new[] {row.Open,row.High,row.Low,row.Close,row.E20,row.E50,row.E100,row.E200}.Any(v=>double.IsFinite(v)&&v<=0))
                throw new InvalidDataException($"Nonpositive price/EMA on {row.Date:yyyy-MM-dd}");
            if (double.IsFinite(row.EligibleB100) && row.EligibleB100 <= 0)
                throw new InvalidDataException($"Nonpositive B100 eligible count on {row.Date:yyyy-MM-dd}");
            if (double.IsFinite(row.M1Days) && row.M1Days < 0)
                throw new InvalidDataException($"Negative Rules 2 VIX M1 DTE on {row.Date:yyyy-MM-dd}");
            if (double.IsFinite(row.VixRollFlag) && row.VixRollFlag != 0 && row.VixRollFlag != 1)
                throw new InvalidDataException($"Invalid Rules 2 VIX roll flag on {row.Date:yyyy-MM-dd}");
            if (double.IsFinite(row.Atrp) && row.Atrp <= 0)
                throw new InvalidDataException($"Nonpositive ATRP on {row.Date:yyyy-MM-dd}");
            if (double.IsFinite(row.Volume) && row.Volume <= 0)
                throw new InvalidDataException($"Nonpositive SPY volume on {row.Date:yyyy-MM-dd}");
            if (new[] {row.Open,row.High,row.Low,row.Close}.All(double.IsFinite)
                && (row.Low>Math.Min(row.Open,row.Close) || row.High<Math.Max(row.Open,row.Close)))
                throw new InvalidDataException($"Invalid SPY OHLC on {row.Date:yyyy-MM-dd}");
        }
        return new(inputs);
    }
    private static double Get(Dictionary<string, double>? row, string name) => row?.GetValueOrDefault(name, double.NaN) ?? double.NaN;
    private static Dictionary<DateTime, Dictionary<string, double>> Read(string directory, string file)
        => ReadPath(Path.Combine(directory, file));

    private static Dictionary<DateTime, Dictionary<string, double>> ReadPath(string path)
    {
        using var reader = new StreamReader(path);
        var header = (reader.ReadLine() ?? throw new InvalidDataException(path)).TrimStart('\ufeff').Split(',');
        if (header[0] != "Date") throw new InvalidDataException("Missing Date header: " + path);
        var table = new Dictionary<DateTime, Dictionary<string, double>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = line.Split(',');
            if (cells.Length != header.Length) throw new InvalidDataException("Column count: " + path);
            var date = DateTime.ParseExact(cells[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 1; i < header.Length; i++) values[header[i]] = double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : double.NaN;
            if (!table.TryAdd(date, values)) throw new InvalidDataException("Duplicate date: " + path);
        }
        return table;
    }
}

#if !RULES2_ENGINE_ONLY
public sealed class Rules2ResearchStrategy : UserStrategyBase
{
    public override string StrategyName => "Breeze Rules 2 — threshold overlap 169";
    public override string Description => "Final threshold pass with equality and overlap allowed. All formulas and weights are frozen; green Entry and red Exit each vary from 45% to 60%. 13x13 = 169 runs, including 13 one-threshold cases.";
    private TimeSeries _score = null!;
    private TimeSeries _target = null!, _actual = null!;
    private double _entry, _exit;
    private bool _loggedExecutionStart;
    private static readonly FeatureSettings FinalSettings = new(
        -0.9, 3, 6, 3, 11, 35, 67.5, 57.5, 7.5, 2.5, 0.10);
    private static readonly LowBreadthNewLowSettings FinalH06 = new(20, 10, 1, 0, 0);
    private const double FinalH06Weight = 0;
    private static readonly UnconfirmedStressSettings FinalH05 = new(4, -0.2, 0, 0.5, 0);
    private static readonly BreadthVelocitySettings FinalF01 = new(7, 1.5, 3, 5, 0.5);
    private static readonly EmaFanTrendSettings FinalF02 = new(4, 1.5, 5, 7, 0.25);
    private static readonly RecoveryAsymmetrySettings FinalF03 = new(0.02, 0, 0, 20, 1);
    private const double FinalF03Weight = 0;
    private static readonly VolumeConfirmationSettings FinalF04 = new(10, 2, 0.25, 0.5, 1.5);
    public Rules2ResearchStrategy()
    {
        StartIndex = 0;
        AddParameter("Entry green range %", ParameterType.Double, 55, 45, 60, 1.25);
        AddParameter("Exit red range %", ParameterType.Double, 50, 45, 60, 1.25);
    }
    public override void Initialize(BarHistory bars)
    {
        _loggedExecutionStart = false;
        if (!string.Equals(bars.Symbol, "SPY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Block 1 supports SPY Daily only.");
        var settings = FinalSettings;
        settings.Validate();
        var h03 = PullbackResumeSettings.Default;
        h03.Validate();
        FinalH06.Validate();
        FinalH05.Validate();
        FinalF01.Validate();
        FinalF02.Validate();
        FinalF03.Validate();
        FinalF04.Validate();
        var weights = new ScoreWeights(
            -4.5,
            -2.5,
            -1.5,
            1,
            FinalH06Weight,
            -0.25,
            0.5,
            1.75,
            FinalF03Weight,
            -1);
        double entryRangeFraction = Parameters[0].AsDouble / 100.0;
        double exitRangeFraction = Parameters[1].AsDouble / 100.0;
        double scoreMin = weights.MinimumScore;
        double scoreMax = weights.MaximumScore;
        double scoreSpan = scoreMax - scoreMin;
        _entry = scoreMin + scoreSpan * entryRangeFraction;
        _exit = scoreMin + scoreSpan * exitRangeFraction;
        Rules2Trading.Validate(weights, _entry, _exit);
        var data = ResearchData.Default;
        var features = Rules2Engine.Calculate(data.Inputs, settings, h03, FinalH06, FinalH05,
            FinalF01, FinalF02, FinalF03, FinalF04);
        _score = new TimeSeries(bars.DateTimes, double.NaN);
        _target = new TimeSeries(bars.DateTimes, double.NaN);
        _actual = new TimeSeries(bars.DateTimes, double.NaN);
        int matched = 0, available = 0;
        DateTime? previous = null;
        for (int i = 0; i < bars.Count; i++)
        {
            var date = bars.DateTimes[i].Date;
            if (previous.HasValue && date <= previous.Value) throw new InvalidOperationException("Use SPY Daily, one bar per date.");
            previous = date;
            if (!data.ByDate.TryGetValue(date, out int row)) continue;
            var p = data.Inputs[row];
            foreach (var pair in new[] { (bars.Open[i], p.Open), (bars.High[i], p.High), (bars.Low[i], p.Low), (bars.Close[i], p.Close) })
                if (!double.IsFinite(pair.Item1) || Math.Abs(pair.Item1 - pair.Item2) > 1e-6 * Math.Max(1, Math.Abs(pair.Item2)))
                    throw new InvalidOperationException($"SPY OHLC differs from snapshot on {date:yyyy-MM-dd}. Use matching provider/adjustment settings.");
            matched++; _score[i] = weights.Score(features[row]);
            if (double.IsFinite(_score[i])) available++;
        }
        if (matched == 0) throw new InvalidOperationException("No dates match the frozen Rules 2 snapshot.");
        if (available == 0) throw new InvalidOperationException("No complete active feature rows; check snapshot and history.");
        PlotTimeSeries(_score, "Rules 2 score", "Rules 2", WLColor.Blue, PlotStyle.Line);
        DrawHorzLine(_entry, WLColor.Green, 1, LineStyle.Dashed, "Rules 2", false);
        DrawHorzLine(_exit, WLColor.Red, 1, LineStyle.Dashed, "Rules 2", false);
        PlotTimeSeries(_target, "Target after next Open (0/1)", "Rules 2 position", WLColor.Blue, PlotStyle.Line);
        PlotTimeSeries(_actual, "Actual position (0/1)", "Rules 2 position", WLColor.Orange, PlotStyle.Line);
        SetPaneMinMax("Rules 2 position", 0, 1.1);
        string thresholdRelation = Math.Abs(_entry - _exit) < 1e-9 ? "single threshold"
            : _exit < _entry ? "positive hysteresis" : "overlap";
        WriteToDebugLog($"Rules 2 threshold overlap: snapshot {data.Inputs[0].Date:yyyy-MM-dd}..{data.Inputs[^1].Date:yyyy-MM-dd}; {matched} matched, {available} scored. All formulas and weights frozen; interaction weights=0. Reachable range {scoreMin:F2}..{scoreMax:F2}; green Entry {entryRangeFraction:P2} => {_entry:F2}; red Exit {exitRangeFraction:P2} => {_exit:F2}; mode={thresholdRelation}. Orders execute next Open. Full interval, no folds.");
    }
    public override void Execute(BarHistory bars, int idx)
    {
        if (!_loggedExecutionStart)
        {
            WriteToDebugLog($"Rules 2 actual execution range begins {bars.DateTimes[idx]:yyyy-MM-dd}; last loaded bar {bars.DateTimes[bars.Count-1]:yyyy-MM-dd}. Check WL8 backtest date settings against the full CSV interval.");
            _loggedExecutionStart = true;
        }
        var positions = OpenPositions.Where(p => !p.NSF).ToArray();
        if (OpenQuantity < 0 || OpenQuantity > 1 || positions.Any(p => Math.Abs(p.Quantity - 1) > 1e-9))
            throw new InvalidOperationException("Diagnostic requires a fresh run with positions of exactly 1 share.");
        bool holding = positions.Length > 0;
        _actual[idx] = OpenQuantity;
        _target[idx] = holding ? 1 : 0;
        if (idx == bars.Count - 1) return; // No invented next-open fill after the data ends.
        double score = _score[idx];
        var action = Rules2Trading.Decide(score, holding, _entry, _exit);
        if (action == Rules2Action.Buy)
        {
            var order = PlaceTrade(bars, TransactionType.Buy, OrderType.Market, 0, "Rules2 BUY1");
            order.Quantity = 1;
            _target[idx] = 1;
            WriteToDebugLog($"{bars.DateTimes[idx]:yyyy-MM-dd} BUY1 next Open; score={score:F2}; position={OpenQuantity}");
        }
        else if (action == Rules2Action.Sell)
        {
            foreach (var position in positions)
                ClosePosition(position, OrderType.Market, 0, "Rules2 SELL1");
            _target[idx] = 0;
            WriteToDebugLog($"{bars.DateTimes[idx]:yyyy-MM-dd} SELL1 next Open; score={score:F2}; close real position");
        }
    }
}
#endif
