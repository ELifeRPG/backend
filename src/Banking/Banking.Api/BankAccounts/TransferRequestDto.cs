namespace ELifeRPG.Banking.Api.BankAccounts;

public sealed record TransferRequestDto
{
    public required Guid CharacterId { get; init; }

    public required Guid TargetBankAccountId { get; init; }

    public required decimal Amount { get; init; }

    public TransferCommand ToCommand(Guid bankAccountId) => new(
        new BankAccountId(bankAccountId),
        new BankAccountId(TargetBankAccountId),
        new CharacterId(CharacterId),
        Amount);
}
