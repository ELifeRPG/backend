namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// Simplified stand-in for the legacy app's IBAN-style BankAccountNumber (a MOD-97 checksum derived
/// from a Country code) — Country was never ported into this codebase (unused even in the legacy
/// app, see MIGRATION.md §1.2/§6), so this just needs to be unique and readable, not internationally
/// valid. Shared by both OpenBankAccountCommand and OpenCorporateBankAccountCommand.
/// </summary>
internal static class BankAccountNumberGenerator
{
    public static string Generate() => $"EL{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
}
