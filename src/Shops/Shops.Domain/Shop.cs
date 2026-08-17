using System.Text.Json.Serialization;
using ELifeRPG.Shops.Domain.Events;

namespace ELifeRPG.Shops.Domain;

public class Shop
{
    [JsonInclude]
    public ShopId Id { get; private set; }

    [JsonInclude]
    public ShopOwnerType OwnerType { get; private set; }

    [JsonInclude]
    public CharacterId? OwnerCharacterId { get; private set; }

    [JsonInclude]
    public CompanyId? OwnerCompanyId { get; private set; }

    [JsonInclude]
    public string DisplayName { get; private set; } = string.Empty;

    [JsonInclude]
    public BankAccountId PayoutBankAccountId { get; private set; }

    public static Shop Create(ShopOpened domainEvent)
    {
        var shop = new Shop();
        shop.Apply(domainEvent);
        return shop;
    }

    public void Apply(ShopOpened domainEvent)
    {
        Id = domainEvent.Id;
        OwnerType = domainEvent.OwnerType;
        OwnerCharacterId = domainEvent.OwnerCharacterId;
        OwnerCompanyId = domainEvent.OwnerCompanyId;
        DisplayName = domainEvent.DisplayName;
        PayoutBankAccountId = domainEvent.PayoutBankAccountId;
    }
}
