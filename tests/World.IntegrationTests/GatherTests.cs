using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Domain.Skills;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Gathering;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Items;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Covers task 7's
/// <c>POST /api/gathering/actions</c> path — <c>GatherCommand</c> dispatched through Mediator, which
/// orchestrates one <c>ICrossModuleTransaction</c> across Characters (skill XP) and World (the item
/// grant) so the two can never diverge. Mirrors Shops.IntegrationTests/PurchaseListingTests.cs (this
/// module's own precheck-then-transaction shape) and World.IntegrationTests/GrantItemsTests.cs (the
/// grant assertions).
/// </summary>
public sealed class GatherTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Gather_GrantsTheItemAndRecordsSkillXpInOneCommit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new GatherCommand(serverId, characterId, nameof(SkillAction.MinedOreDeposit), itemId, 1));

        if (result is not GatherResult.Gathered gathered)
        {
            throw new InvalidOperationException($"Expected Gathered, got {result}");
        }

        var gain = Assert.Single(gathered.Gains);
        Assert.Equal(SkillType.Mining, gain.Skill);
        Assert.Equal(25, gain.XpGained);
        Assert.Equal(25, gain.NewTotalXp);

        var granted = Assert.Single(gathered.GrantedInstances);
        Assert.Equal(itemId, granted.ItemId);

        // Both legs actually committed, not just returned in the handler's in-memory response.
        var characterSkillsRepository = scope.ServiceProvider.GetRequiredService<ICharacterSkillsRepository>();
        var characterSkills = await characterSkillsRepository.FindByCharacterIdAsync(characterId, CancellationToken.None);
        Assert.NotNull(characterSkills);
        Assert.Equal(25, characterSkills.TotalXpBySkill[SkillType.Mining]);

        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stored = await itemInstanceRepository.FindByIdAsync(granted.InstanceId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(characterId, stored.RootCharacterId);
        Assert.Equal(ItemOrigin.Gathered, stored.Origin);
        Assert.Equal(new OriginRef("Gathering", nameof(SkillAction.MinedOreDeposit)), stored.OriginRef);
        Assert.True(stored.PendingSpawn);
    }

    [Fact]
    public async Task Gather_ForAQuantityGreaterThanOne_MintsThatManyDiscreteInstancesAndScalesXp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var characterId = await CreateCharacterAsync(mediator);

        // "Fifty swings are fifty entities" — five here is plenty to prove no stacking, without
        // approaching WorldSettings.MaxInstancesPerGrant.
        var result = await mediator.Send(new GatherCommand(serverId, characterId, nameof(SkillAction.ChoppedTree), itemId, 5));

        if (result is not GatherResult.Gathered gathered)
        {
            throw new InvalidOperationException($"Expected Gathered, got {result}");
        }

        var gain = Assert.Single(gathered.Gains);
        Assert.Equal(SkillType.Woodcutting, gain.Skill);
        Assert.Equal(100, gain.XpGained); // SkillActionCatalog: ChoppedTree = 20 XP * 5

        Assert.Equal(5, gathered.GrantedInstances.Count);
        Assert.Equal(5, gathered.GrantedInstances.Select(x => x.InstanceId).Distinct().Count());

        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var stored = await itemInstanceRepository.LoadManyAsync(
            gathered.GrantedInstances.Select(x => x.InstanceId).ToList(), CancellationToken.None);
        Assert.Equal(5, stored.Count);
        Assert.All(stored, x => Assert.Equal(characterId, x.RootCharacterId));
    }

    [Fact]
    public async Task Gather_ForAMultiSkillAction_GrantsEveryRewardedSkillInTheSameCommit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var characterId = await CreateCharacterAsync(mediator);

        // SkillActionCatalog: ForgedIngot rewards both Blacksmithing (40) and Mining (5) — proves a
        // multi-skill action grants every reward in the same commit as the item.
        var result = await mediator.Send(new GatherCommand(serverId, characterId, nameof(SkillAction.ForgedIngot), itemId, 1));

        if (result is not GatherResult.Gathered gathered)
        {
            throw new InvalidOperationException($"Expected Gathered, got {result}");
        }

        Assert.Equal(2, gathered.Gains.Count);
        Assert.Contains(gathered.Gains, g => g.Skill == SkillType.Blacksmithing && g.XpGained == 40);
        Assert.Contains(gathered.Gains, g => g.Skill == SkillType.Mining && g.XpGained == 5);
        Assert.Single(gathered.GrantedInstances);
    }

    [Fact]
    public async Task Gather_ForAnUnknownAction_ReturnsUnknownActionAndGrantsNeitherLeg()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new GatherCommand(serverId, characterId, "NotARealSkillAction", itemId, 1));

        Assert.True(result is GatherResult.UnknownAction, $"Expected UnknownAction, got {result}");

        // Rejected purely in memory, before any dispatch — neither the skill XP nor the item was ever
        // granted, proving the two legs can't diverge even on this earliest rejection path.
        var characterSkillsRepository = scope.ServiceProvider.GetRequiredService<ICharacterSkillsRepository>();
        Assert.Null(await characterSkillsRepository.FindByCharacterIdAsync(characterId, CancellationToken.None));

        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        Assert.Empty(await itemInstanceRepository.FindByRootCharacterAsync(characterId, CancellationToken.None));
    }

    [Fact]
    public async Task Gather_ForAnUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var unknownCharacterId = new CharacterId(Guid.NewGuid());

        var result = await mediator.Send(new GatherCommand(serverId, unknownCharacterId, nameof(SkillAction.CaughtFish), itemId, 1));

        Assert.True(result is GatherResult.CharacterNotFound, $"Expected CharacterNotFound, got {result}");
    }

    [Fact]
    public async Task Gather_ForAnUncatalogedItemId_ReturnsItemNotInCatalogAndGrantsNoSkillXp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var characterId = await CreateCharacterAsync(mediator);
        var uncatalogedItemId = new ItemId(Guid.NewGuid());

        var result = await mediator.Send(new GatherCommand(serverId, characterId, nameof(SkillAction.HarvestedCrop), uncatalogedItemId, 1));

        Assert.True(result is GatherResult.ItemNotInCatalog, $"Expected ItemNotInCatalog, got {result}");
        if (result is GatherResult.ItemNotInCatalog notInCatalog)
        {
            Assert.Equal(uncatalogedItemId, notInCatalog.ItemId);
        }

        // Caught at the catalog-resolution precheck, before transactionFactory.BeginAsync — no skill
        // XP was ever recorded for an item that can't be granted (the two legs can't diverge).
        var characterSkillsRepository = scope.ServiceProvider.GetRequiredService<ICharacterSkillsRepository>();
        Assert.Null(await characterSkillsRepository.FindByCharacterIdAsync(characterId, CancellationToken.None));
    }

    [Fact]
    public async Task Gather_ExceedingMaxInstancesPerGrant_IsRejectedBeforeAnyXpOrItemMoves()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var characterId = await CreateCharacterAsync(mediator);

        var settings = await mediator.Send(new WorldSettingsQuery());
        var tooMany = settings.MaxInstancesPerGrant + 1;

        var result = await mediator.Send(new GatherCommand(serverId, characterId, nameof(SkillAction.ScavengedSalvage), itemId, tooMany));

        if (result is not GatherResult.GrantTooLarge grantTooLarge)
        {
            throw new InvalidOperationException($"Expected GrantTooLarge, got {result}");
        }

        Assert.Equal(tooMany, grantTooLarge.Requested);
        Assert.Equal(settings.MaxInstancesPerGrant, grantTooLarge.MaxInstancesPerGrant);

        // The cap is checked at the precheck, before transactionFactory.BeginAsync — no transaction was
        // ever opened, so neither the skill XP nor the item grant happened.
        var characterSkillsRepository = scope.ServiceProvider.GetRequiredService<ICharacterSkillsRepository>();
        Assert.Null(await characterSkillsRepository.FindByCharacterIdAsync(characterId, CancellationToken.None));

        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        Assert.Empty(await itemInstanceRepository.FindByRootCharacterAsync(characterId, CancellationToken.None));
    }

    /// <summary>
    /// Whole-branch review, I1: gathering was the one gameserver-scoped inventory write on this branch
    /// with no server guard — the ack and spawn-failed paths both have one. Without it, gameserver A
    /// could mint items and skill XP for a character playing on gameserver B using nothing but its own
    /// scope, which is exactly what the design's correctness core (mechanism 4) exists to prevent.
    ///
    /// Two providers with different client ids are two different, real gameservers as far as the guard
    /// is concerned — see AcknowledgeSpawnsTests' own class doc comment and FixedCurrentGameServer.
    /// </summary>
    [Fact]
    public async Task Gather_ForACharacterOnAnotherServer_IsRejectedAndGrantsNeitherLeg()
    {
        await using var homeServerProvider = TestServices.BuildProvider("gather-home-server");
        await using var awayServerProvider = TestServices.BuildProvider("gather-away-server");

        await using var homeScope = homeServerProvider.CreateAsyncScope();
        var homeMediator = homeScope.ServiceProvider.GetRequiredService<IMediator>();
        var itemId = await CreateCatalogItemAsync(homeMediator);
        var characterId = await CreateCharacterOnAsync(homeServerProvider, homeMediator);

        await using var awayScope = awayServerProvider.CreateAsyncScope();
        var awayMediator = awayScope.ServiceProvider.GetRequiredService<IMediator>();
        var awayServerId = await CurrentServerIdAsync(awayScope.ServiceProvider);

        var result = await awayMediator.Send(
            new GatherCommand(awayServerId, characterId, nameof(SkillAction.MinedOreDeposit), itemId, 1));

        Assert.True(result is GatherResult.WrongServer, $"Expected WrongServer, got {result}");

        // Rejected at the precheck, before transactionFactory.BeginAsync — neither leg moved.
        var characterSkillsRepository = homeScope.ServiceProvider.GetRequiredService<ICharacterSkillsRepository>();
        Assert.Null(await characterSkillsRepository.FindByCharacterIdAsync(characterId, CancellationToken.None));

        var itemInstanceRepository = homeScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        Assert.Empty(await itemInstanceRepository.FindByRootCharacterAsync(characterId, CancellationToken.None));
    }

    /// <summary>
    /// Whole-branch review, I7: <c>Quantity</c> was only bounded from above. Zero succeeded and appended
    /// a permanent 0-XP SkillXpGranted event while minting nothing — an unreconcilable record of a
    /// gather that never happened. The purchase path gets this invariant free from ShopListing.Purchase's
    /// own guard, so the two orchestrators had diverged.
    /// </summary>
    [Fact]
    public async Task Gather_ForAQuantityOfZero_IsRejectedAndGrantsNeitherLeg()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new GatherCommand(serverId, characterId, nameof(SkillAction.MinedOreDeposit), itemId, 0));

        if (result is not GatherResult.InvalidQuantity invalidQuantity)
        {
            throw new InvalidOperationException($"Expected InvalidQuantity, got {result}");
        }

        Assert.Equal(0, invalidQuantity.Requested);

        var characterSkillsRepository = scope.ServiceProvider.GetRequiredService<ICharacterSkillsRepository>();
        Assert.Null(await characterSkillsRepository.FindByCharacterIdAsync(characterId, CancellationToken.None));

        var itemInstanceRepository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        Assert.Empty(await itemInstanceRepository.FindByRootCharacterAsync(characterId, CancellationToken.None));
    }

    /// <summary>
    /// The other half of I7: a negative quantity reached <c>CharacterSkills.GrantXp</c> and threw
    /// <c>ArgumentOutOfRangeException</c> — a 500 for what is plainly a bad request.
    /// </summary>
    [Fact]
    public async Task Gather_ForANegativeQuantity_IsRejectedRatherThanThrowing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new GatherCommand(serverId, characterId, nameof(SkillAction.MinedOreDeposit), itemId, -1));

        if (result is not GatherResult.InvalidQuantity invalidQuantity)
        {
            throw new InvalidOperationException($"Expected InvalidQuantity, got {result}");
        }

        Assert.Equal(-1, invalidQuantity.Requested);

        var characterSkillsRepository = scope.ServiceProvider.GetRequiredService<ICharacterSkillsRepository>();
        Assert.Null(await characterSkillsRepository.FindByCharacterIdAsync(characterId, CancellationToken.None));
    }

    /// <summary>
    /// Task 7 review round 1 (Important): the four rejection tests above all fail at the precheck,
    /// before <c>transactionFactory.BeginAsync</c> is ever reached, so none of them prove
    /// <c>GatherHandler</c>'s own cross-module rollback — the entire justification for routing gathering
    /// through <c>ICrossModuleTransaction</c> in the first place. This test drives a fault <i>inside</i>
    /// the open transaction, via a swapped-in <see cref="IItemInstanceRepositoryFactory"/>, and
    /// dispatches the real <c>GatherCommand</c> through <c>IMediator</c> — proving <c>GatherHandler</c>'s
    /// actual sequencing, not a hand-rolled reproduction of it (contrast
    /// Banking.IntegrationTests/PurchaseCompanySharesCommandTests.cs's
    /// <c>Purchase_WhenCompanySideFailsAfterBankingSideFlushed_RollsBackBankingWrite</c>, which manually
    /// replays the handler's steps instead — that test is proving
    /// <c>NpgsqlCrossModuleTransaction</c> itself rolls back a flushed write, which this task does not
    /// need to re-prove; this test only needs to prove <c>GatherHandler</c> reaches that same
    /// uncommitted-disposal outcome via its own real code path).
    ///
    /// The fault sits in the fake's <c>SaveChangesAsync</c>, not its <c>GrantAsync</c>: <c>GatherHandler</c>
    /// defers <i>both</i> legs' <c>SaveChangesAsync</c> calls until after both legs' in-memory work is
    /// queued — mirroring <c>PurchaseListingHandler</c>'s identical "nothing is flushed until every
    /// append/grant has succeeded" shape (see <c>GatherResult.ItemNotInCatalog</c>'s doc comment). A
    /// fault inside <c>GrantAsync</c> itself would therefore fire <i>before</i>
    /// <c>characterSkillsRepository.SaveChangesAsync</c> ever runs — proving only "nothing flushes at
    /// all," not "an already-flushed write rolls back." Throwing from the item leg's own
    /// <c>SaveChangesAsync</c> instead means the skill-XP leg has already durably flushed into the
    /// still-open, uncommitted transaction by the time the fault hits, which is the scenario under
    /// review.
    /// </summary>
    [Fact]
    public async Task Gather_WhenTheItemGrantLegFailsAfterSkillXpFlushed_RollsBackTheSkillXp()
    {
        await using var provider = TestServices.BuildProvider(configureServices: services =>
            services.Replace(ServiceDescriptor.Scoped<IItemInstanceRepositoryFactory>(_ => new FaultyItemInstanceRepositoryFactory())));

        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await CurrentServerIdAsync(scope.ServiceProvider);
        var itemId = await CreateCatalogItemAsync(mediator);
        var characterId = await CreateCharacterAsync(mediator);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Send(new GatherCommand(serverId, characterId, nameof(SkillAction.MinedOreDeposit), itemId, 1)).AsTask());

        // Fresh repository read, not the in-memory CharacterSkills GatherHandler mutated — proves the
        // rollback durably reached Postgres rather than merely that the in-process object was never
        // told about a commit. This character is fresh to this test, so Found-with-zero-XP is not a
        // possible false pass here: a prior successful gather would have started this stream, so
        // finding none at all is the only way this assertion can pass.
        var characterSkillsRepository = scope.ServiceProvider.GetRequiredService<ICharacterSkillsRepository>();
        Assert.Null(await characterSkillsRepository.FindByCharacterIdAsync(characterId, CancellationToken.None));
    }

    /// <summary>
    /// Hand-written fake, same convention as <c>FixedCurrentGameServer</c> in TestServices.cs — no
    /// mocking library in this repo (ARCHITECTURE.md §9e). Used only by
    /// <see cref="Gather_WhenTheItemGrantLegFailsAfterSkillXpFlushed_RollsBackTheSkillXp"/>.
    /// </summary>
    private sealed class FaultyItemInstanceRepositoryFactory : IItemInstanceRepositoryFactory
    {
        public IItemInstanceRepository CreateFor(CrossModuleSessionHandle handle) => new FaultyItemInstanceRepository();
    }

    /// <summary>
    /// Every member GatherHandler doesn't touch throws, so an accidental new dependency on this fake
    /// surfaces immediately rather than silently no-op'ing. See the covering test's doc comment for why
    /// the fault sits in <see cref="SaveChangesAsync"/> rather than <see cref="GrantAsync(ItemId,string?,int,CharacterId,ItemOrigin,OriginRef?,CancellationToken)"/>.
    /// </summary>
    private sealed class FaultyItemInstanceRepository : IItemInstanceRepository
    {
        public ValueTask<ItemInstance?> FindByIdAsync(ItemInstanceId id, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindByRootCharacterAsync(CharacterId rootCharacterId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindCarriedByRootCharacterAsync(CharacterId rootCharacterId, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindPendingByRootCharacterAsync(
            CharacterId rootCharacterId, int limit, int maxDeliveryAttempts, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> LoadManyAsync(IReadOnlyList<ItemInstanceId> ids, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindChildrenAsync(ItemInstanceId containerInstanceId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public ValueTask<IReadOnlyList<ItemInstance>> FindUndeliverableAsync(int maxDeliveryAttempts, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public void Store(ItemInstance instance)
        {
            // GrantAsync's real implementation queues minted rows here before its own SaveChangesAsync;
            // this fake's GrantAsync never calls it (see below), so this is unreachable too, but is a
            // no-op rather than a throw since a void method has no meaningful "not supported" signal.
        }

        public void RecordDeliveryAttempt(ItemInstance instance, DateTimeOffset now)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public void RecordSpawnFailure(ItemInstance instance, SpawnFailureReason reason, DateTimeOffset now)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public void Eject(ItemInstance instance)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        public void SoftDelete(ItemInstance instance)
            => throw new NotSupportedException("Not exercised by GatherHandler.");

        // The fault: fires only once GatherHandler has already called
        // characterSkillsRepository.SaveChangesAsync (see the covering test's doc comment).
        public ValueTask SaveChangesAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Simulated failure in the item-grant leg's flush, after the skill-XP leg's own SaveChangesAsync already ran.");

        public ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
            ItemId itemId, int quantity, CharacterId ownerCharacterId, ItemOrigin origin, OriginRef? originRef, CancellationToken cancellationToken)
            => throw new NotSupportedException("GatherHandler only ever uses the prefab-taking overload below.");

        // The prefab-taking overload GatherHandler actually calls — succeeds (pure in-memory, matching
        // the real MartenItemInstanceRepository's own "no I/O here" contract) so the handler proceeds
        // to both SaveChangesAsync calls, where the fault above actually fires.
        public ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
            ItemId itemId, string? prefabClassName, int quantity, CharacterId ownerCharacterId, ItemOrigin origin, OriginRef? originRef, CancellationToken cancellationToken)
        {
            IReadOnlyList<GrantedInstance> granted = Enumerable.Range(0, quantity)
                .Select(_ => new GrantedInstance(new ItemInstanceId(Guid.NewGuid()), itemId, prefabClassName ?? "Test_Faulty"))
                .ToList();
            return ValueTask.FromResult(granted);
        }
    }

    // The calling gameserver's own id, as FixedCurrentGameServer resolves it — the same deterministic
    // value CreateCharacterCommand stamped onto Character.CurrentServerId in this provider, so the
    // gather server guard (GatherResult.WrongServer) passes for these characters. Fully qualified
    // because Characters.Application.Common and World.Application.Common both define ICurrentGameServer
    // and this file imports both.
    private static async Task<GameServerId> CurrentServerIdAsync(IServiceProvider services)
        => await services.GetRequiredService<ELifeRPG.World.Application.Common.ICurrentGameServer>()
            .GetIdAsync(CancellationToken.None);

    private static async Task<ItemId> CreateCatalogItemAsync(IMediator mediator, string? prefabClassName = null)
    {
        var result = await mediator.Send(new CreateItemCommand(
            "Test Ore", prefabClassName ?? $"Test_{Guid.NewGuid():N}"));

        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException($"Expected Created, got {result}");
        }

        return created.ItemId;
    }

    // Accounts come from portal signup, not from joining the gameserver — see TestAccounts. Uses this
    // test class's own _provider (not the caller's scope) to mint the account, same pattern as
    // Shops.IntegrationTests/PurchaseListingTests.cs's CreateActiveAccountAsync/CreateCharacterAsync.
    // Same as CreateCharacterAsync below, but mints the account from the given provider rather than this
    // class's own — the server-guard test needs the character created (and so CurrentServerId stamped)
    // through the *home* server's provider.
    private static async Task<CharacterId> CreateCharacterOnAsync(ServiceProvider provider, IMediator mediator)
    {
        using var accountScope = provider.CreateScope();
        var accountId = (await TestAccounts.CreateAsync(accountScope.ServiceProvider)).Id;

        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Gather Guard Character"));
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException($"Expected Created, got {result}");
        }

        return created.CharacterId;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        using var accountScope = _provider.CreateScope();
        var accountId = (await TestAccounts.CreateAsync(accountScope.ServiceProvider)).Id;

        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Gather Test Character"));
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException($"Expected Created, got {result}");
        }

        return created.CharacterId;
    }
}
