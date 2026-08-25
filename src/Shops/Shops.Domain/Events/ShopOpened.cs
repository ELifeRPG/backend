namespace ELifeRPG.Shops.Domain.Events;

public sealed record ShopOpened(
    ShopId Id,
    ShopOwnerType OwnerType,
    CharacterId? OwnerCharacterId,
    CompanyId? OwnerCompanyId,
    string DisplayName,
    BankAccountId PayoutBankAccountId,
    GameServerId ServerId);
