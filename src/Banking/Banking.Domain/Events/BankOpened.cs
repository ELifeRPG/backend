namespace ELifeRPG.Banking.Domain.Events;

/// <summary>
/// Code is the bank's four-letter code embedded in every account number it issues. It is set here
/// and nowhere else, so it never changes — a different code means a different bank.
/// </summary>
public sealed record BankOpened(BankId Id, string Name, string Code, decimal TransactionFeeBase, decimal TransactionFeeMultiplier);
