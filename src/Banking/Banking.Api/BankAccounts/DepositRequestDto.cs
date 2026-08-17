namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record DepositRequestDto
{
    public required decimal Amount { get; init; }

    public DepositCommand ToCommand(Guid bankAccountId) => new(new BankAccountId(bankAccountId), Amount);
}
