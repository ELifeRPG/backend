namespace ELifeRPG.Phone.Application.Common;

/// <summary>
/// Mirrors BankAccountNumberGenerator — the same "generate and let the unique index arbitrate"
/// approach, rather than a pre-check that two concurrent issues could both pass.
/// </summary>
internal static class PhoneNumberGenerator
{
    public static PhoneNumber Generate() =>
        PhoneNumber.Parse(Random.Shared.NextInt64(10_000_000, 99_999_999).ToString());
}
