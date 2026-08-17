namespace ELifeRPG.Banking.Api.Banks;

public sealed record BankDto
{
    public required Guid BankId { get; init; }

    public required string Name { get; init; }

    public required decimal TransactionFeeBase { get; init; }

    public required decimal TransactionFeeMultiplier { get; init; }

    public static BankDto Create(Bank source) => new()
    {
        BankId = source.Id.Value,
        Name = source.Name,
        TransactionFeeBase = source.TransactionFeeBase,
        TransactionFeeMultiplier = source.TransactionFeeMultiplier,
    };

    public static BankDto Create(OpenBankResult source, OpenBankRequestDto request) => new()
    {
        BankId = source.Id.Value,
        Name = request.Name,
        TransactionFeeBase = request.TransactionFeeBase,
        TransactionFeeMultiplier = request.TransactionFeeMultiplier,
    };
}
