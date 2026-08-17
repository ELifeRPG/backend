namespace ELifeRPG.Shops.Api.Shops;

public sealed record OpenShopRequestDto
{
    public required string OwnerType { get; init; }

    public Guid? OwnerCharacterId { get; init; }

    public Guid? OwnerCompanyId { get; init; }

    public required string DisplayName { get; init; }

    public required Guid PayoutBankAccountId { get; init; }

    public OpenShopCommand ToCommand(ShopOwnerType ownerType) => new(
        ownerType,
        OwnerCharacterId.HasValue ? new CharacterId(OwnerCharacterId.Value) : null,
        OwnerCompanyId.HasValue ? new CompanyId(OwnerCompanyId.Value) : null,
        DisplayName,
        new BankAccountId(PayoutBankAccountId));
}
