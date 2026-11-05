namespace ELifeRPG.Banking.Api.Banks;

public sealed record BankDto
{
    public required Guid BankId { get; init; }

    public required string Name { get; init; }

    /// <summary>The four letters embedded in every account number this bank issues.</summary>
    public required string Code { get; init; }

    public required decimal TransactionFeeBase { get; init; }

    public required decimal TransactionFeeMultiplier { get; init; }

    public static BankDto Create(Bank source) => new()
    {
        BankId = source.Id.Value,
        Name = source.Name,
        Code = source.Code,
        TransactionFeeBase = source.TransactionFeeBase,
        TransactionFeeMultiplier = source.TransactionFeeMultiplier,
    };
}
