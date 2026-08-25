using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Domain.Events;
using Xunit;

namespace ELifeRPG.Shops.Domain.UnitTests;

public class ShopTests
{
    [Fact]
    public void Create_PersonalShop_SetsOwnerCharacterId()
    {
        var shopId = new ShopId(Guid.NewGuid());
        var characterId = new CharacterId(Guid.NewGuid());
        var payoutAccountId = new BankAccountId(Guid.NewGuid());
        var serverId = new GameServerId(Guid.NewGuid());

        var shop = Shop.Create(new ShopOpened(shopId, ShopOwnerType.Personal, characterId, null, "Joe's Guns", payoutAccountId, serverId));

        Assert.Equal(ShopOwnerType.Personal, shop.OwnerType);
        Assert.Equal(characterId, shop.OwnerCharacterId);
        Assert.Null(shop.OwnerCompanyId);
        Assert.Equal("Joe's Guns", shop.DisplayName);
        Assert.Equal(payoutAccountId, shop.PayoutBankAccountId);
        Assert.Equal(serverId, shop.ServerId);
    }

    [Fact]
    public void Create_CorporateShop_SetsOwnerCompanyId()
    {
        var shopId = new ShopId(Guid.NewGuid());
        var companyId = new CompanyId(Guid.NewGuid());
        var payoutAccountId = new BankAccountId(Guid.NewGuid());
        var serverId = new GameServerId(Guid.NewGuid());

        var shop = Shop.Create(new ShopOpened(shopId, ShopOwnerType.Corporate, null, companyId, "Acme Depot", payoutAccountId, serverId));

        Assert.Equal(ShopOwnerType.Corporate, shop.OwnerType);
        Assert.Null(shop.OwnerCharacterId);
        Assert.Equal(companyId, shop.OwnerCompanyId);
    }
}
