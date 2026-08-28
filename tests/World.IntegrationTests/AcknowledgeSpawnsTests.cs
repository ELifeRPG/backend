using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Infrastructure.Common;
using Marten;
using Marten.Linq.SoftDeletes;
using Marten.Patching;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ELifeRPG.World.IntegrationTests.AckResults;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Covers task 5's
/// <c>POST /api/inventory/acks</c> path — <c>AcknowledgeSpawnsCommand</c> dispatched through Mediator.
/// Every test here builds at least one provider via <c>TestServices.BuildProvider(clientId)</c>: the
/// clientId is what both Characters' own <c>ICurrentGameServer</c> (used by <c>CreateCharacterCommand</c>
/// to stamp <c>Character.CurrentServerId</c>) and World's own <c>ICurrentGameServer</c> (used by the ack
/// handler to resolve the calling server) resolve to the same deterministic GameServerId from — see
/// <c>FixedCurrentGameServer</c> in TestServices.cs. Two providers built with different clientIds are
/// therefore two different, real gameservers as far as the server guard is concerned, even though both
/// point at the same Postgres database.
///
/// Outcomes are asserted with <c>is</c> pattern matching (same style as GrantItemsTests), not
/// <c>Assert.IsType</c>: a <c>union</c> case's runtime type is the declaring union type itself (the
/// case is a compiler-recognized pattern, not a distinct CLR subtype), so a strict
/// <c>GetType() == typeof(TCase)</c> check never matches.
/// </summary>
public sealed class AcknowledgeSpawnsTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static async Task<CharacterId> CreateCharacterAsync(ServiceProvider provider, string name)
    {
        await using var scope = provider.CreateAsyncScope();
        var accountId = (await TestAccounts.CreateAsync(scope.ServiceProvider)).Id;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CreateCharacterCommand(accountId, name));
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException($"Expected Created, got {result}");
        }

        return created.CharacterId;
    }

    private static async Task<ItemId> CreateCatalogItemAsync(ServiceProvider provider, string? prefabClassName = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CreateItemCommand(
            "Test Item", prefabClassName ?? $"Test_{Guid.NewGuid():N}"));
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException($"Expected Created, got {result}");
        }

        return created.ItemId;
    }

    private static async Task<ItemInstanceId> GrantOneAsync(ServiceProvider provider, ItemId itemId, CharacterId owner)
    {
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GrantItemsCommand(
            itemId, 1, owner, ItemOrigin.ShopPurchase, new OriginRef("Shops", Guid.NewGuid().ToString())));
        if (result is not GrantItemsResult.Granted granted)
        {
            throw new InvalidOperationException($"Expected Granted, got {result}");
        }

        return granted.Instances[0].InstanceId;
    }

    [Fact]
    public async Task Ack_ForAnUnknownInstanceId_ReturnsNotFoundAndCreatesNothing()
    {
        var unknownId = new ItemInstanceId(Guid.NewGuid());

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var currentGameServer = scope.ServiceProvider.GetRequiredService<ICurrentGameServer>();
        var serverId = await currentGameServer.GetIdAsync(CancellationToken.None);

        var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(serverId, [new InstanceAckRequest(unknownId, [])])));

        var outcome = Assert.Single(results);
        Assert.Equal(unknownId, outcome.InstanceId);
        Assert.True(outcome.Outcome is AckOutcome.NotFound, $"Expected NotFound, got {outcome.Outcome}");

        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var found = await repository.FindByIdAsync(unknownId, CancellationToken.None);
        Assert.Null(found);
    }

    [Fact]
    public async Task Ack_ForACharacterOnAnotherServer_IsRejected()
    {
        var homeServerProvider = TestServices.BuildProvider("home-server");
        var awayServerProvider = TestServices.BuildProvider("away-server");
        await using var _1 = homeServerProvider;
        await using var _2 = awayServerProvider;

        var owner = await CreateCharacterAsync(homeServerProvider, "Home Character");
        var itemId = await CreateCatalogItemAsync(homeServerProvider);
        var instanceId = await GrantOneAsync(homeServerProvider, itemId, owner);

        await using var awayScope = awayServerProvider.CreateAsyncScope();
        var mediator = awayScope.ServiceProvider.GetRequiredService<IMediator>();
        var awayServerId = await awayScope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(awayServerId, [new InstanceAckRequest(instanceId, [])])));

        var outcome = Assert.Single(results);
        Assert.True(outcome.Outcome is AckOutcome.WrongServer, $"Expected WrongServer, got {outcome.Outcome}");

        // Rejected, not merely reported as rejected: PendingSpawn must be untouched.
        await using var readScope = homeServerProvider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await repository.FindByIdAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.PendingSpawn);
        Assert.Null(reloaded.RootGameServerId);
    }

    [Fact]
    public async Task Ack_ReplayedForTheSameInstance_IsIdempotent()
    {
        var owner = await CreateCharacterAsync(_provider, "Replay Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var instanceId = await GrantOneAsync(_provider, itemId, owner);

        await using var scope1 = _provider.CreateAsyncScope();
        var mediator1 = scope1.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope1.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var firstResults = Acknowledged(await mediator1.Send(new AcknowledgeSpawnsCommand(serverId, [new InstanceAckRequest(instanceId, [])])));
        var firstOutcome = Assert.Single(firstResults);
        Assert.True(firstOutcome.Outcome is AckOutcome.Cleared, $"Expected Cleared, got {firstOutcome.Outcome}");

        await using var scope2 = _provider.CreateAsyncScope();
        var mediator2 = scope2.ServiceProvider.GetRequiredService<IMediator>();
        var secondResults = Acknowledged(await mediator2.Send(new AcknowledgeSpawnsCommand(serverId, [new InstanceAckRequest(instanceId, [])])));
        var secondOutcome = Assert.Single(secondResults);
        Assert.True(secondOutcome.Outcome is AckOutcome.AlreadyCleared, $"Expected AlreadyCleared, got {secondOutcome.Outcome}");

        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await repository.FindByIdAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.PendingSpawn);
        Assert.Equal(serverId, reloaded.RootGameServerId);
    }

    [Fact]
    public async Task Ack_DeclaringChildren_MintsThemParentedToTheAckedInstance()
    {
        var owner = await CreateCharacterAsync(_provider, "Parent Character");
        var parentItemId = await CreateCatalogItemAsync(_provider);
        var childItemId = await CreateCatalogItemAsync(_provider);
        var parentInstanceId = await GrantOneAsync(_provider, parentItemId, owner);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var ack = new InstanceAckRequest(parentInstanceId, [new AckChildRequest(childItemId, "mag-1")]);
        var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(serverId, [ack])));

        var outcome = Assert.Single(results);
        if (outcome.Outcome is not AckOutcome.Cleared cleared)
        {
            throw new InvalidOperationException($"Expected Cleared, got {outcome.Outcome}");
        }

        var child = Assert.Single(cleared.Children);
        Assert.Equal(childItemId, child.ItemId);
        Assert.Equal("mag-1", child.Slot);
        if (child.Outcome is not AckChildOutcome.Minted minted)
        {
            throw new InvalidOperationException($"Expected Minted, got {child.Outcome}");
        }

        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var childInstance = await repository.FindByIdAsync(minted.InstanceId, CancellationToken.None);

        Assert.NotNull(childInstance);
        Assert.Equal(childItemId, childInstance.ItemId);
        Assert.Equal(ParentKind.Container, childInstance.ParentKind);
        Assert.Equal(parentInstanceId, childInstance.ContainerInstanceId);
        Assert.Equal("mag-1", childInstance.Slot);
        Assert.Equal(owner, childInstance.RootCharacterId);
        Assert.False(childInstance.PendingSpawn);
    }

    [Fact]
    public async Task Ack_ReplayedWithTheSameChildren_ReturnsTheSameChildIdsNotASecondSet()
    {
        var owner = await CreateCharacterAsync(_provider, "Replay Children Character");
        var parentItemId = await CreateCatalogItemAsync(_provider);
        var childItemId = await CreateCatalogItemAsync(_provider);
        var parentInstanceId = await GrantOneAsync(_provider, parentItemId, owner);

        await using var scope1 = _provider.CreateAsyncScope();
        var mediator1 = scope1.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope1.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var ack = new InstanceAckRequest(parentInstanceId, [new AckChildRequest(childItemId, "mag-1")]);
        var firstResults = Acknowledged(await mediator1.Send(new AcknowledgeSpawnsCommand(serverId, [ack])));
        var firstOutcome = Assert.Single(firstResults);
        if (firstOutcome.Outcome is not AckOutcome.Cleared firstCleared)
        {
            throw new InvalidOperationException($"Expected Cleared, got {firstOutcome.Outcome}");
        }

        var firstChildOutcome = Assert.Single(firstCleared.Children).Outcome;
        if (firstChildOutcome is not AckChildOutcome.Minted firstChild)
        {
            throw new InvalidOperationException($"Expected Minted, got {firstChildOutcome}");
        }

        // A distinct dispatch (separate handler instance — Mediator handlers are transient), so this
        // exercises the DB-backed idempotency (IItemInstanceRepository.FindChildrenAsync), not just an
        // in-memory cache local to one Handle() call.
        await using var scope2 = _provider.CreateAsyncScope();
        var mediator2 = scope2.ServiceProvider.GetRequiredService<IMediator>();
        var secondResults = Acknowledged(await mediator2.Send(new AcknowledgeSpawnsCommand(serverId, [ack])));
        var secondOutcome = Assert.Single(secondResults);
        if (secondOutcome.Outcome is not AckOutcome.AlreadyCleared alreadyCleared)
        {
            throw new InvalidOperationException($"Expected AlreadyCleared, got {secondOutcome.Outcome}");
        }

        var secondChildOutcome = Assert.Single(alreadyCleared.Children).Outcome;
        if (secondChildOutcome is not AckChildOutcome.Minted secondChild)
        {
            throw new InvalidOperationException($"Expected Minted, got {secondChildOutcome}");
        }

        Assert.Equal(firstChild.InstanceId, secondChild.InstanceId);

        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var children = await repository.FindChildrenAsync(parentInstanceId, CancellationToken.None);
        Assert.Single(children);
    }

    [Fact]
    public async Task Ack_DeclaringAChildWithAnUncatalogedItemId_ReportsItemNotInCatalogWithoutFailingTheAck()
    {
        var owner = await CreateCharacterAsync(_provider, "Uncataloged Child Character");
        var parentItemId = await CreateCatalogItemAsync(_provider);
        var parentInstanceId = await GrantOneAsync(_provider, parentItemId, owner);
        var uncatalogedChildItemId = new ItemId(Guid.NewGuid());

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var ack = new InstanceAckRequest(parentInstanceId, [new AckChildRequest(uncatalogedChildItemId, "mag-1")]);
        var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(serverId, [ack])));

        var outcome = Assert.Single(results);
        if (outcome.Outcome is not AckOutcome.Cleared cleared)
        {
            throw new InvalidOperationException($"Expected Cleared, got {outcome.Outcome}");
        }

        var child = Assert.Single(cleared.Children);
        Assert.True(child.Outcome is AckChildOutcome.ItemNotInCatalog, $"Expected ItemNotInCatalog, got {child.Outcome}");

        // The parent itself must still have cleared normally — an uncataloged child must not 500 the
        // whole ack, and must not stop the parent's own PendingSpawn from clearing.
        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await repository.FindByIdAsync(parentInstanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.PendingSpawn);
    }

    [Fact]
    public async Task Ack_ForARemovedByStaffInstance_ReturnsRemovedByStaffAndDoesNotResurrectIt()
    {
        var owner = await CreateCharacterAsync(_provider, "Tombstoned Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var instanceId = await GrantOneAsync(_provider, itemId, owner);

        // No domain method sets RemovedByStaff before task 9's staff tooling — patch it directly, same
        // reasoning InventoryReadsTests documents for its own Patch usage.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Patch<ItemInstance>(instanceId.Value).Set(x => x.RemovedByStaff, true);
            await session.SaveChangesAsync();
        }

        await using var ackScope = _provider.CreateAsyncScope();
        var mediator = ackScope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await ackScope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(serverId, [new InstanceAckRequest(instanceId, [])])));

        var outcome = Assert.Single(results);
        Assert.True(outcome.Outcome is AckOutcome.RemovedByStaff, $"Expected RemovedByStaff, got {outcome.Outcome}");

        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await repository.FindByIdAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.PendingSpawn); // never un-tombstoned into a delivered state
    }

    /// <summary>
    /// Task 2 fix round 4, item 2. Converting the ack write to a patch stopped it resurrecting a row a
    /// snapshot delete had removed — but a declared child is an <i>insert</i>, not an update, so it
    /// still landed. The result was an orphan: live, not pending, carrying the deleted parent's
    /// <c>RootCharacterId</c>, answering the carried-inventory read while its
    /// <c>ContainerInstanceId</c> pointed at a row that no longer exists. Exactly the state the
    /// snapshot path's delete cascade exists to prevent, reached through a door that cascade cannot
    /// see.
    ///
    /// The race only exists <i>inside</i> the handler — a delete that lands before it runs is already
    /// handled, since the initial load filters soft-deleted rows — so the test drives it from inside,
    /// through <c>InstrumentedItemInstanceRepository</c>'s post-load hook. Asserting on the
    /// reconciliation any other way would only assert that it exists.
    /// </summary>
    [Fact]
    public async Task Ack_WhenTheInstanceIsDeletedAfterTheHandlerLoadedIt_MintsNoOrphanedChildrenAndReportsNotFound()
    {
        var deleteOnNextLoad = false;
        ItemInstanceId parentInstanceId = default;
        ServiceProvider provider = null!;

        provider = TestServices.BuildProvider("ack-orphan-server", services =>
        {
            services.AddSingleton<RepositoryCallCounts>();
            services.AddScoped<MartenItemInstanceRepository>();
            services.AddScoped<IItemInstanceRepository>(sp => new InstrumentedItemInstanceRepository(
                sp.GetRequiredService<MartenItemInstanceRepository>(),
                sp.GetRequiredService<RepositoryCallCounts>(),
                afterLoadMany: async () =>
                {
                    if (!deleteOnNextLoad)
                    {
                        return;
                    }

                    // Fire once, on the handler's *first* batched load — the one that decides the ack
                    // is valid — so the delete commits before children are minted and before the
                    // reconciliation's own re-read.
                    deleteOnNextLoad = false;

                    // Raw session on purpose: going through the repository would re-enter this hook.
                    var store = provider.GetRequiredService<IWorldStore>();
                    await using var session = store.LightweightSession();
                    session.Delete<ItemInstance>(parentInstanceId);
                    await session.SaveChangesAsync();
                }));
        });

        await using var _ = provider;

        var owner = await CreateCharacterAsync(provider, "Ack Orphan Character");
        var parentItemId = await CreateCatalogItemAsync(provider);
        var childItemId = await CreateCatalogItemAsync(provider);
        parentInstanceId = await GrantOneAsync(provider, parentItemId, owner);

        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        deleteOnNextLoad = true;

        var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(
            serverId, [new InstanceAckRequest(parentInstanceId, [new AckChildRequest(childItemId, "magazine")])])));

        var outcome = Assert.Single(results);
        Assert.True(outcome.Outcome is AckOutcome.NotFound, $"Expected NotFound, got {outcome.Outcome}");

        // The parent stayed deleted — the patch matched nothing, as it should.
        await using var readScope = provider.CreateAsyncScope();
        var store = readScope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();
        Assert.Null(await session.Query<ItemInstance>().Where(x => x.Id == parentInstanceId).SingleOrDefaultAsync());

        // And nothing was minted underneath it. Checked against every row, deleted ones included, so
        // this cannot pass by the child having been created and then filtered out of view.
        var anyChild = await session.Query<ItemInstance>()
            .Where(x => x.ContainerInstanceId != null && x.ContainerInstanceId!.Value.Value == parentInstanceId.Value && x.MaybeDeleted())
            .ToListAsync();
        Assert.Empty(anyChild);

        // The orphan's concrete harm, had one been minted: it would answer this read.
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var carried = await repository.FindCarriedByRootCharacterAsync(owner, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Empty(carried);
    }

    /// <summary>Review round 1, B-2: a slot already minted for a different itemId must be reported, not silently adopted under the caller's own (wrong) itemId.</summary>
    [Fact]
    public async Task Ack_DeclaringAChildForASlotAlreadyMintedWithADifferentItemId_ReportsSlotItemMismatch()
    {
        var owner = await CreateCharacterAsync(_provider, "Mismatch Character");
        var parentItemId = await CreateCatalogItemAsync(_provider);
        var childItemA = await CreateCatalogItemAsync(_provider);
        var childItemB = await CreateCatalogItemAsync(_provider);
        var parentInstanceId = await GrantOneAsync(_provider, parentItemId, owner);

        await using var scope1 = _provider.CreateAsyncScope();
        var mediator1 = scope1.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope1.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var firstAck = new InstanceAckRequest(parentInstanceId, [new AckChildRequest(childItemA, "mag-1")]);
        var firstResults = Acknowledged(await mediator1.Send(new AcknowledgeSpawnsCommand(serverId, [firstAck])));
        if (Assert.Single(firstResults).Outcome is not AckOutcome.Cleared firstCleared)
        {
            throw new InvalidOperationException("Expected Cleared.");
        }

        if (Assert.Single(firstCleared.Children).Outcome is not AckChildOutcome.Minted firstMinted)
        {
            throw new InvalidOperationException("Expected Minted.");
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var mediator2 = scope2.ServiceProvider.GetRequiredService<IMediator>();
        var secondAck = new InstanceAckRequest(parentInstanceId, [new AckChildRequest(childItemB, "mag-1")]);
        var secondResults = Acknowledged(await mediator2.Send(new AcknowledgeSpawnsCommand(serverId, [secondAck])));

        if (Assert.Single(secondResults).Outcome is not AckOutcome.AlreadyCleared alreadyCleared)
        {
            throw new InvalidOperationException("Expected AlreadyCleared.");
        }

        var secondChild = Assert.Single(alreadyCleared.Children);
        if (secondChild.Outcome is not AckChildOutcome.SlotItemMismatch mismatch)
        {
            throw new InvalidOperationException($"Expected SlotItemMismatch, got {secondChild.Outcome}");
        }

        Assert.Equal(childItemA, mismatch.ExistingItemId);

        // No second row was created for the mismatched request, and the original child is untouched.
        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var children = await repository.FindChildrenAsync(parentInstanceId, CancellationToken.None);
        var onlyChild = Assert.Single(children);
        Assert.Equal(firstMinted.InstanceId, onlyChild.Id);
        Assert.Equal(childItemA, onlyChild.ItemId);
    }

    /// <summary>
    /// Review round 1, B-1: two concurrent acks for the same parent+slot both seeing no existing child
    /// and both trying to mint one must still converge on exactly one child row — proving the partial
    /// unique index + AcknowledgeSpawnsHandler's reconciliation, not just the sequential-replay case
    /// the other tests here cover. Assertions hold regardless of whether the two calls actually
    /// overlapped at the Postgres level or one fully completed before the other started — either way,
    /// both must report the same single child instance id.
    /// </summary>
    [Fact]
    public async Task Ack_ConcurrentAcksDeclaringTheSameChildSlot_MintOnlyOneChildInstance()
    {
        var owner = await CreateCharacterAsync(_provider, "Concurrent Child Character");
        var parentItemId = await CreateCatalogItemAsync(_provider);
        var childItemId = await CreateCatalogItemAsync(_provider);
        var parentInstanceId = await GrantOneAsync(_provider, parentItemId, owner);

        await using var guardScope = _provider.CreateAsyncScope();
        var serverId = await guardScope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var ack = new InstanceAckRequest(parentInstanceId, [new AckChildRequest(childItemId, "mag-1")]);

        async Task<ItemInstanceId> RunAckAsync()
        {
            await using var scope = _provider.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(serverId, [ack])));
            var outcome = Assert.Single(results);

            var children = outcome.Outcome switch
            {
                AckOutcome.Cleared cleared => cleared.Children,
                AckOutcome.AlreadyCleared alreadyCleared => alreadyCleared.Children,
                _ => throw new InvalidOperationException($"Expected Cleared or AlreadyCleared, got {outcome.Outcome}"),
            };

            if (Assert.Single(children).Outcome is not AckChildOutcome.Minted minted)
            {
                throw new InvalidOperationException("Expected Minted.");
            }

            return minted.InstanceId;
        }

        var mintedIds = await Task.WhenAll(RunAckAsync(), RunAckAsync());

        Assert.Equal(mintedIds[0], mintedIds[1]);

        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var children2 = await repository.FindChildrenAsync(parentInstanceId, CancellationToken.None);
        Assert.Single(children2);
    }

    /// <summary>
    /// Whole-branch review, I3: <c>FindChildrenAsync</c> was the only read in the module that did not
    /// filter <c>RemovedByStaff</c>, and its only consumer is this handler's idempotency cache. So a
    /// replayed ack found the staff-tombstoned child by slot and handed its id back as
    /// <see cref="AckChildOutcome.Minted"/> — telling the mod to adopt an instance staff had explicitly
    /// removed, which is the sticky tombstone (correctness core, mechanism 3) undone from the read side.
    /// The correct behaviour is to treat the slot as empty and mint a fresh child.
    /// </summary>
    [Fact]
    public async Task Ack_ReplayedAfterItsChildWasRemovedByStaff_DoesNotHandBackTheTombstonedChild()
    {
        var owner = await CreateCharacterAsync(_provider, "Tombstoned Child Character");
        var parentItemId = await CreateCatalogItemAsync(_provider);
        var childItemId = await CreateCatalogItemAsync(_provider);
        var parentInstanceId = await GrantOneAsync(_provider, parentItemId, owner);

        var ack = new InstanceAckRequest(parentInstanceId, [new AckChildRequest(childItemId, "mag-1")]);

        ItemInstanceId firstChildId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
            var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(serverId, [ack])));

            if (Assert.Single(results).Outcome is not AckOutcome.Cleared cleared
                || Assert.Single(cleared.Children).Outcome is not AckChildOutcome.Minted minted)
            {
                throw new InvalidOperationException("Expected Cleared with a Minted child.");
            }

            firstChildId = minted.InstanceId;
        }

        // Staff removes the child. No domain method sets RemovedByStaff before task 9's staff tooling —
        // patch it directly, same as Ack_ForARemovedByStaffInstance_... above.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Patch<ItemInstance>(firstChildId.Value).Set(x => x.RemovedByStaff, true);
            await session.SaveChangesAsync();
        }

        await using var replayScope = _provider.CreateAsyncScope();
        var replayMediator = replayScope.ServiceProvider.GetRequiredService<IMediator>();
        var replayServerId = await replayScope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
        var replayResults = Acknowledged(await replayMediator.Send(new AcknowledgeSpawnsCommand(replayServerId, [ack])));

        if (Assert.Single(replayResults).Outcome is not AckOutcome.AlreadyCleared alreadyCleared
            || Assert.Single(alreadyCleared.Children).Outcome is not AckChildOutcome.Minted replayMinted)
        {
            throw new InvalidOperationException("Expected AlreadyCleared with a Minted child.");
        }

        Assert.NotEqual(firstChildId, replayMinted.InstanceId);

        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var children = await repository.FindChildrenAsync(parentInstanceId, CancellationToken.None);
        Assert.Equal(replayMinted.InstanceId, Assert.Single(children).Id);
    }

    /// <summary>
    /// Whole-branch review, I5: grants are capped by MaxInstancesPerGrant and pending pages by
    /// MaxPendingPageSize, but the ack endpoint capped nothing at all. The design spec makes an
    /// over-sized batch a first-class <c>batch_too_large</c> rejection enforced on <b>counts</b>, and
    /// publishes the caps on GET /api/inventory/limits so the Bridge chunks instead of discovering them.
    /// </summary>
    [Fact]
    public async Task Ack_WithMoreEntriesThanMaxAcksPerBatch_IsRejectedAsBatchTooLarge()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
        var settings = await mediator.Send(new WorldSettingsQuery());

        var tooMany = Enumerable
            .Range(0, settings.MaxAcksPerBatch + 1)
            .Select(_ => new InstanceAckRequest(new ItemInstanceId(Guid.NewGuid()), []))
            .ToList();

        var result = await mediator.Send(new AcknowledgeSpawnsCommand(serverId, tooMany));

        if (result is not AcknowledgeSpawnsResult.BatchTooLarge tooLarge)
        {
            throw new InvalidOperationException($"Expected BatchTooLarge, got {result}");
        }

        Assert.Equal("acks", tooLarge.Field);
        Assert.Equal(settings.MaxAcksPerBatch + 1, tooLarge.Requested);
        Assert.Equal(settings.MaxAcksPerBatch, tooLarge.Max);
    }

    /// <summary>The other axis of I5's cap — the child mint fan-out under any single acked parent.</summary>
    [Fact]
    public async Task Ack_WithMoreChildrenThanMaxChildrenPerAck_IsRejectedAsBatchTooLarge()
    {
        var owner = await CreateCharacterAsync(_provider, "Too Many Children Character");
        var parentItemId = await CreateCatalogItemAsync(_provider);
        var childItemId = await CreateCatalogItemAsync(_provider);
        var parentInstanceId = await GrantOneAsync(_provider, parentItemId, owner);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
        var settings = await mediator.Send(new WorldSettingsQuery());

        var children = Enumerable
            .Range(0, settings.MaxChildrenPerAck + 1)
            .Select(i => new AckChildRequest(childItemId, $"slot-{i}"))
            .ToList();

        var result = await mediator.Send(new AcknowledgeSpawnsCommand(
            serverId, [new InstanceAckRequest(parentInstanceId, children)]));

        if (result is not AcknowledgeSpawnsResult.BatchTooLarge tooLarge)
        {
            throw new InvalidOperationException($"Expected BatchTooLarge, got {result}");
        }

        Assert.Equal("children", tooLarge.Field);
        Assert.Equal(settings.MaxChildrenPerAck + 1, tooLarge.Requested);

        // Rejected whole, before anything was read or written: the parent is still pending and no child
        // was minted.
        await using var readScope = _provider.CreateAsyncScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var reloaded = await repository.FindByIdAsync(parentInstanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.PendingSpawn);
        Assert.Empty(await repository.FindChildrenAsync(parentInstanceId, CancellationToken.None));
    }

    /// <summary>
    /// The second half of I5: <c>ResolveChildrenAsync</c> called the catalog resolver inside its
    /// per-child loop, and <c>ItemCatalogResolver</c> dispatches the <i>batched</i>
    /// <c>ItemCatalogEntriesQuery</c> with one id at a time and no memoisation — so N children of the
    /// same item were N cross-module round trips inside one open World session, against a design that
    /// mandates one batched catalog check. This asserts the resolver is called exactly once for a batch
    /// with many children spanning several ack entries, via a counting decorator over the real one.
    /// </summary>
    [Fact]
    public async Task Ack_DeclaringManyChildren_ResolvesTheCatalogInOneBatchedCall()
    {
        var owner = await CreateCharacterAsync(_provider, "Batched Catalog Character");
        var childItemId = await CreateCatalogItemAsync(_provider);
        var otherChildItemId = await CreateCatalogItemAsync(_provider);

        var parentIds = new List<ItemInstanceId>();
        for (var i = 0; i < 3; i++)
        {
            var parentItemId = await CreateCatalogItemAsync(_provider);
            parentIds.Add(await GrantOneAsync(_provider, parentItemId, owner));
        }

        var counter = new CountingItemCatalogResolver();
        await using var provider = TestServices.BuildProvider(configureServices: services =>
            services.AddScoped<IItemCatalogResolver>(sp =>
            {
                counter.Inner = ActivatorUtilities.CreateInstance<ItemCatalogResolver>(sp);
                return counter;
            }));

        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var acks = parentIds
            .Select(parentId => new InstanceAckRequest(parentId, [
                new AckChildRequest(childItemId, "mag-1"),
                new AckChildRequest(otherChildItemId, "mag-2"),
                new AckChildRequest(childItemId, "mag-3"),
            ]))
            .ToList();

        var results = Acknowledged(await mediator.Send(new AcknowledgeSpawnsCommand(serverId, acks)));

        Assert.Equal(3, results.Count);
        Assert.All(results, x => Assert.True(x.Outcome is AckOutcome.Cleared, $"Expected Cleared, got {x.Outcome}"));

        // Nine declared children across three parents, two distinct itemIds — one batched dispatch.
        Assert.Equal(1, counter.BatchedCalls);
        Assert.Equal(0, counter.SingleCalls);
        Assert.Equal(2, counter.LastBatchSize);
    }

    /// <summary>
    /// Hand-written counting decorator — no mocking library in this repo (ARCHITECTURE.md §9e). Delegates
    /// to the real <see cref="ItemCatalogResolver"/> so the ack under test still resolves real catalog
    /// entries; it only records how the handler asked.
    /// </summary>
    private sealed class CountingItemCatalogResolver : IItemCatalogResolver
    {
        public IItemCatalogResolver Inner = null!;

        public int SingleCalls;
        public int BatchedCalls;
        public int LastBatchSize;

        public ValueTask<string?> ResolvePrefabClassNameAsync(ItemId itemId, CancellationToken cancellationToken)
        {
            SingleCalls++;
            return Inner.ResolvePrefabClassNameAsync(itemId, cancellationToken);
        }

        public ValueTask<IReadOnlyDictionary<ItemId, string>> ResolvePrefabClassNamesAsync(
            IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken)
        {
            BatchedCalls++;
            LastBatchSize = itemIds.Distinct().Count();
            return Inner.ResolvePrefabClassNamesAsync(itemIds, cancellationToken);
        }
    }
}
