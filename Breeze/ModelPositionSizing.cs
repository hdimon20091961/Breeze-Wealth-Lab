namespace BreezeVL;

public static class ModelPositionSizing
{
    public const double FixedLevel = 50.0;

    // The final Wealth-Lab research selected the fixed-level engine: below 50
    // the model is flat; at 50 or above it holds all ten contracts.
    public static int TargetContracts(double holdValue)
    {
        double clamped = Math.Clamp(holdValue, 0.0, 100.0);
        return clamped >= FixedLevel ? 10 : 0;
    }
}
