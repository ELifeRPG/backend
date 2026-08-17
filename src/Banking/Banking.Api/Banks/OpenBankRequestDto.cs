namespace ELifeRPG.Banking.Api.Banks;

public sealed record OpenBankRequestDto
{
    public required string Name { get; init; }

    public required decimal TransactionFeeBase { get; init; }

    public required decimal TransactionFeeMultiplier { get; init; }

    public OpenBankCommand ToCommand() => new(Name, TransactionFeeBase, TransactionFeeMultiplier);
}
