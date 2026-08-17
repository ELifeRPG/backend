namespace ELifeRPG.Shops.Api.Shops;

public sealed record ShopDto
{
    public required Guid ShopId { get; init; }

    public required string OwnerType { get; init; }

    public Guid? OwnerCharacterId { get; init; }

    public Guid? OwnerCompanyId { get; init; }

    public required string DisplayName { get; init; }

    public required Guid PayoutBankAccountId { get; init; }

    public static ShopDto Create(Shop source) => new()
    {
        ShopId = source.Id.Value,
        OwnerType = source.OwnerType.ToString(),
        OwnerCharacterId = source.OwnerCharacterId?.Value,
        OwnerCompanyId = source.OwnerCompanyId?.Value,
        DisplayName = source.DisplayName,
        PayoutBankAccountId = source.PayoutBankAccountId.Value,
    };
}
