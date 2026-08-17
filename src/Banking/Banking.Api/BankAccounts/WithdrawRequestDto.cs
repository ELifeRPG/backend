namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record WithdrawRequestDto
{
    public required Guid CharacterId { get; init; }

    public required decimal Amount { get; init; }

    public WithdrawCommand ToCommand(Guid bankAccountId) => new(new BankAccountId(bankAccountId), new CharacterId(CharacterId), Amount);
}
