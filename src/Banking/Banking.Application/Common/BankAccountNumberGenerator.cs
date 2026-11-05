namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// Issues a number under the bank's code with ten random account digits. Uniqueness is arbitrated by
/// the unique index on BankAccount.Number rather than a pre-check two concurrent opens could both
/// pass, so callers retry on BankAccountNumberTakenException. Shared by OpenBankAccountCommand and
/// OpenCorporateBankAccountCommand.
/// </summary>
internal static class BankAccountNumberGenerator
{
    // Generous enough that exhausting it means something is genuinely wrong, not merely unlucky:
    // ten digits give ten billion numbers per bank.
    public const int MaxAttempts = 5;

    public static string Generate(string bankCode)
        => BankAccountNumber.Create(bankCode, Random.Shared.NextInt64(0, 10_000_000_000).ToString("D10"));
}
