namespace ELifeRPG.Shared.Kernel;

/// <summary>
/// The rounding rule for every amount that reaches the books: money is held in whole cents
/// (two decimal places). Inputs that carry a fraction of a cent are rejected, never rounded; values
/// the backend computes itself, such as transaction fees, are rounded to whole cents half away from
/// zero (0.005 → 0.01, 0.0049 → 0.00) at the moment they are booked.
/// </summary>
public static class Money
{
    public static decimal RoundToCents(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Compares the value rather than its scale, so <c>1.500m</c> counts as whole cents.</summary>
    public static bool IsWholeCents(decimal value) => decimal.Round(value, 2) == value;

    /// <summary>Strictly positive and in whole cents — the only amount a booking or a price may carry.</summary>
    public static bool IsValidAmount(decimal value) => value > 0 && IsWholeCents(value);
}
