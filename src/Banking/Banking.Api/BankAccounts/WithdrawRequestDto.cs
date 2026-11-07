namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record WithdrawRequestDto
{
    public required Guid CharacterId { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>Optional free-form origin of the booking, such as the ATM's id; up to 128 characters. Shown on the ledger row.</summary>
    public string? Source { get; init; }

    public WithdrawCommand ToCommand(Guid bankAccountId, string idempotencyKey) =>
        new(new BankAccountId(bankAccountId), new CharacterId(CharacterId), Amount, Source, idempotencyKey);
}
