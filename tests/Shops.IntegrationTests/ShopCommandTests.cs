using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Banking.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Application.Shops;
using ELifeRPG.Shops.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Shops.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class ShopCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task OpenShop_PersonalForKnownCharacter_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var payoutAccountId = await OpenPersonalBankAccountAsync(mediator, characterId);

        var result = await mediator.Send(new OpenShopCommand(ShopOwnerType.Personal, characterId, null, "Joe's Guns", payoutAccountId));

        Assert.True(result is OpenShopResult.Opened, $"Expected Opened, got {result}");
    }

    [Fact]
    public async Task OpenShop_PersonalForUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var payoutAccountId = await OpenPersonalBankAccountAsync(mediator, founderId);

        var result = await mediator.Send(
            new OpenShopCommand(ShopOwnerType.Personal, new CharacterId(Guid.NewGuid()), null, "Ghost Shop", payoutAccountId));

        Assert.True(result is OpenShopResult.CharacterNotFound, $"Expected CharacterNotFound, got {result}");
    }

    [Fact]
    public async Task OpenShop_CorporateForUnknownCompany_ReturnsCompanyNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var payoutAccountId = await OpenPersonalBankAccountAsync(mediator, founderId);

        var result = await mediator.Send(
            new OpenShopCommand(ShopOwnerType.Corporate, null, new CompanyId(Guid.NewGuid()), "Ghost Depot", payoutAccountId));

        Assert.True(result is OpenShopResult.CompanyNotFound, $"Expected CompanyNotFound, got {result}");
    }

    [Fact]
    public async Task AddListing_ByPersonalOwner_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var shopId = await OpenPersonalShopAsync(mediator, characterId);
        var itemId = await CreateItemAsync(mediator);

        var result = await mediator.Send(new AddListingCommand(shopId, itemId, 5m, 10, characterId));

        Assert.True(result is AddListingResult.Added, $"Expected Added, got {result}");
    }

    [Fact]
    public async Task AddListing_ByNonOwner_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var ownerId = await CreateCharacterAsync(mediator);
        var otherId = await CreateCharacterAsync(mediator);
        var shopId = await OpenPersonalShopAsync(mediator, ownerId);
        var itemId = await CreateItemAsync(mediator);

        var result = await mediator.Send(new AddListingCommand(shopId, itemId, 5m, 10, otherId));

        Assert.True(result is AddListingResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task AddListing_WithUnknownItem_ReturnsItemNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var shopId = await OpenPersonalShopAsync(mediator, characterId);

        var result = await mediator.Send(new AddListingCommand(shopId, new ItemId(Guid.NewGuid()), 5m, 10, characterId));

        Assert.True(result is AddListingResult.ItemNotFound, $"Expected ItemNotFound, got {result}");
    }

    [Fact]
    public async Task AddListing_OnCorporateShop_ByFounder_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, founderId) = await CreateCompanyWithFounderAsync(mediator);
        var shopId = await OpenCorporateShopAsync(mediator, companyId, founderId);
        var itemId = await CreateItemAsync(mediator);

        var result = await mediator.Send(new AddListingCommand(shopId, itemId, 5m, 10, founderId));

        Assert.True(result is AddListingResult.Added, $"Expected Added, got {result}");
    }

    [Fact]
    public async Task AddListing_OnCorporateShop_ByRookieMember_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (companyId, founderId) = await CreateCompanyWithFounderAsync(mediator);
        var shopId = await OpenCorporateShopAsync(mediator, companyId, founderId);
        var itemId = await CreateItemAsync(mediator);
        var rookieId = await CreateCharacterAsync(mediator);
        var addMemberResult = await mediator.Send(new AddMemberCommand(companyId, rookieId));
        Assert.True(addMemberResult is AddMemberResult.Added, $"Expected Added, got {addMemberResult}");

        var result = await mediator.Send(new AddListingCommand(shopId, itemId, 5m, 10, rookieId));

        Assert.True(result is AddListingResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task UpdateListing_ByOwner_UpdatesPriceAndStock()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var shopId = await OpenPersonalShopAsync(mediator, characterId);
        var itemId = await CreateItemAsync(mediator);
        var addResult = await mediator.Send(new AddListingCommand(shopId, itemId, 5m, 10, characterId));
        Assert.True(addResult is AddListingResult.Added, $"Expected Added, got {addResult}");
        if (addResult is not AddListingResult.Added added)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new UpdateListingCommand(shopId, added.ListingId, 7.5m, 20, characterId));

        Assert.True(result is UpdateListingResult.Updated, $"Expected Updated, got {result}");
        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            var listing = Assert.Single(found.Listings);
            Assert.Equal(7.5m, listing.Price);
            Assert.Equal(20, listing.Stock);
        }
    }

    [Fact]
    public async Task RemoveListing_ByOwner_ExcludesListingFromShopQuery()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var shopId = await OpenPersonalShopAsync(mediator, characterId);
        var itemId = await CreateItemAsync(mediator);
        var addResult = await mediator.Send(new AddListingCommand(shopId, itemId, 5m, 10, characterId));
        Assert.True(addResult is AddListingResult.Added, $"Expected Added, got {addResult}");
        if (addResult is not AddListingResult.Added added)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new RemoveListingCommand(shopId, added.ListingId, characterId));

        Assert.True(result is RemoveListingResult.Removed, $"Expected Removed, got {result}");
        var shopQuery = await mediator.Send(new ShopQuery(shopId));
        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is ShopQueryResult.Found found)
        {
            Assert.Empty(found.Listings);
        }
    }

    [Fact]
    public async Task AddListing_WithZeroPrice_ReturnsInvalidPrice()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var shopId = await OpenPersonalShopAsync(mediator, characterId);
        var itemId = await CreateItemAsync(mediator);

        var result = await mediator.Send(new AddListingCommand(shopId, itemId, 0m, 10, characterId));

        Assert.True(result is AddListingResult.InvalidPrice, $"Expected InvalidPrice, got {result}");
    }

    [Fact]
    public async Task RemoveListing_CalledTwice_IsIdempotent()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var shopId = await OpenPersonalShopAsync(mediator, characterId);
        var listingId = await AddListingAsync(mediator, shopId, characterId);

        var first = await mediator.Send(new RemoveListingCommand(shopId, listingId, characterId));
        var second = await mediator.Send(new RemoveListingCommand(shopId, listingId, characterId));

        Assert.True(first is RemoveListingResult.Removed, $"Expected Removed, got {first}");
        Assert.True(second is RemoveListingResult.Removed, $"Expected Removed on the repeat call, got {second}");
    }

    [Fact]
    public async Task UpdateListing_OnRemovedListing_ReturnsListingNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var shopId = await OpenPersonalShopAsync(mediator, characterId);
        var listingId = await AddListingAsync(mediator, shopId, characterId);
        var removeResult = await mediator.Send(new RemoveListingCommand(shopId, listingId, characterId));
        Assert.True(removeResult is RemoveListingResult.Removed, $"Expected Removed, got {removeResult}");

        var result = await mediator.Send(new UpdateListingCommand(shopId, listingId, 9m, 3, characterId));

        Assert.True(result is UpdateListingResult.ListingNotFound, $"Expected ListingNotFound, got {result}");
    }

    [Fact]
    public async Task ShopQuery_ForUnknownShop_ReturnsNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ShopQuery(new ShopId(Guid.NewGuid())));

        Assert.True(result is ShopQueryResult.NotFound, $"Expected NotFound, got {result}");
    }

    [Fact]
    public async Task Shop_OpenedOnOneServer_IsVisibleFromAnotherServer()
    {
        // Hive model: shops are hive-wide, so a shop opened via one gameserver must be reachable
        // from another. Asserts the opposite of the pre-hive behaviour — see
        // docs/superpowers/specs/2026-08-22-hive-tenancy-design.md.
        await using var providerB = TestServices.BuildProvider("gameserver-two");

        await using var scopeA = _provider.CreateAsyncScope();
        var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediatorA);
        var shopId = await OpenPersonalShopAsync(mediatorA, characterId);

        await using var scopeB = providerB.CreateAsyncScope();
        var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();

        var lookupFromCreatingServer = await mediatorA.Send(new ShopQuery(shopId));
        var lookupFromOtherServer = await mediatorB.Send(new ShopQuery(shopId));

        Assert.True(lookupFromCreatingServer is ShopQueryResult.Found, $"Expected Found from the creating server, got {lookupFromCreatingServer}");
        Assert.True(lookupFromOtherServer is ShopQueryResult.Found, $"Expected Found from a different server, got {lookupFromOtherServer}");
    }

    [Fact]
    public async Task OpenShop_StampsTheServerItWasOpenedOn()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var currentServer = scope.ServiceProvider.GetRequiredService<ICurrentGameServer>();
        var expectedServerId = await currentServer.GetIdAsync(CancellationToken.None);
        var characterId = await CreateCharacterAsync(mediator);
        var payoutAccountId = await OpenPersonalBankAccountAsync(mediator, characterId);

        var result = await mediator.Send(new OpenShopCommand(ShopOwnerType.Personal, characterId, null, "Corner Store", payoutAccountId));

        Assert.True(result is OpenShopResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenShopResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var shopQuery = await mediator.Send(new ShopQuery(opened.ShopId));

        Assert.True(shopQuery is ShopQueryResult.Found, $"Expected Found, got {shopQuery}");
        if (shopQuery is not ShopQueryResult.Found found)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.Equal(expectedServerId, found.Shop.ServerId);
    }

    // Accounts come from portal signup now, not from joining the gameserver:
    // CreateSessionCommand no longer creates one. See TestAccounts.
    private async Task<AccountId> CreateActiveAccountAsync()
    {
        using var scope = _provider.CreateScope();
        return (await TestAccounts.CreateAsync(scope.ServiceProvider)).Id;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var accountId = await CreateActiveAccountAsync();
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Shops Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }

    private async Task<(CompanyId CompanyId, CharacterId FounderId)> CreateCompanyWithFounderAsync(IMediator mediator)
    {
        var founderId = await CreateCharacterAsync(mediator);
        var result = await mediator.Send(new CreateCompanyCommand("Shops Test Corp", founderId));

        Assert.True(result is CreateCompanyResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCompanyResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return (created.CompanyId, founderId);
    }

    private static async Task<BankId> OpenBankAsync(IMediator mediator)
    {
        var result = await mediator.Send(new OpenBankCommand("Shops Test Bank", 0.20m, 0.02m));
        return result.Id;
    }

    private async Task<BankAccountId> OpenPersonalBankAccountAsync(IMediator mediator, CharacterId characterId)
    {
        var bankId = await OpenBankAsync(mediator);
        var result = await mediator.Send(new OpenBankAccountCommand(bankId, characterId));

        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenBankAccountResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.BankAccountId;
    }

    private async Task<BankAccountId> OpenCorporateBankAccountAsync(IMediator mediator, CompanyId companyId)
    {
        var bankId = await OpenBankAsync(mediator);
        var result = await mediator.Send(new OpenCorporateBankAccountCommand(bankId, companyId));

        Assert.True(result is OpenCorporateBankAccountResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenCorporateBankAccountResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.BankAccountId;
    }

    private async Task<ItemId> CreateItemAsync(IMediator mediator)
    {
        // Prefab class names are unique across the catalog, and these tests run repeatedly against a
        // long-lived database, so mint a fresh one per call rather than colliding with the last run.
        var result = await mediator.Send(new CreateItemCommand("9mm Ammo Box", $"ELRPG_Test_AmmoBox_{Guid.NewGuid():N}"));

        Assert.True(result is CreateItemResult.Created, $"Expected Created, got {result}");
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.ItemId;
    }

    private async Task<ShopId> OpenPersonalShopAsync(IMediator mediator, CharacterId characterId)
    {
        var payoutAccountId = await OpenPersonalBankAccountAsync(mediator, characterId);
        var result = await mediator.Send(new OpenShopCommand(ShopOwnerType.Personal, characterId, null, "Test Shop", payoutAccountId));

        Assert.True(result is OpenShopResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenShopResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.ShopId;
    }

    private static async Task<ShopListingId> AddListingAsync(IMediator mediator, ShopId shopId, CharacterId actingCharacterId)
    {
        var itemResult = await mediator.Send(new CreateItemCommand("9mm Ammo Box", $"ELRPG_Test_AmmoBox_{Guid.NewGuid():N}"));
        Assert.True(itemResult is CreateItemResult.Created, $"Expected Created, got {itemResult}");
        if (itemResult is not CreateItemResult.Created createdItem)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new AddListingCommand(shopId, createdItem.ItemId, 5m, 10, actingCharacterId));

        Assert.True(result is AddListingResult.Added, $"Expected Added, got {result}");
        if (result is not AddListingResult.Added added)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return added.ListingId;
    }

    private async Task<ShopId> OpenCorporateShopAsync(IMediator mediator, CompanyId companyId, CharacterId founderId)
    {
        var payoutAccountId = await OpenCorporateBankAccountAsync(mediator, companyId);
        var result = await mediator.Send(new OpenShopCommand(ShopOwnerType.Corporate, null, companyId, "Test Depot", payoutAccountId));

        Assert.True(result is OpenShopResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenShopResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.ShopId;
    }
}
