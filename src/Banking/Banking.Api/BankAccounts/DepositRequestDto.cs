namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record DepositRequestDto
{
    /// <summary>The character handing over the cash; recorded on the ledger row.</summary>
    public required Guid CharacterId { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>Optional free-form origin of the booking, such as the ATM's id; up to 128 characters. Shown on the ledger row.</summary>
    public string? Source { get; init; }

    public DepositCommand ToCommand(Guid bankAccountId) => new(new BankAccountId(bankAccountId), new CharacterId(CharacterId), Amount, Source);
}
