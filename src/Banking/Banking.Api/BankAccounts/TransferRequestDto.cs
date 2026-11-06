namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record TransferRequestDto
{
    public required Guid CharacterId { get; init; }

    public required Guid TargetBankAccountId { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>Optional free-form origin of the booking, such as the ATM's id; up to 128 characters. Shown on the ledger row.</summary>
    public string? Source { get; init; }

    public TransferCommand ToCommand(Guid bankAccountId) => new(
        new BankAccountId(bankAccountId),
        new BankAccountId(TargetBankAccountId),
        new CharacterId(CharacterId),
        Amount,
        Source);
}
