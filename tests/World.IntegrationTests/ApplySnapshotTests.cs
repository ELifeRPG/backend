using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Domain.Snapshots;
using ELifeRPG.World.Infrastructure.Common;
using Marten;
using Marten.Linq.SoftDeletes;
using Marten.Patching;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). Covers
/// <c>POST /api/inventory/snapshots</c> — its validation and rejection taxonomy (task 1) and its
/// revision-last-write-wins diff-and-write (task 2) — dispatched as <c>ApplySnapshotCommand</c>
/// through Mediator, same style as <c>AcknowledgeSpawnsTests</c>.
///
/// Almost every test here has to <b>grant</b> its instances first, and that is the point rather than
/// setup noise: the backend is the sole minter of an <c>ItemInstanceId</c>, so an upsert naming an id
/// no grant ever issued is <c>UnknownInstance</c> and creates nothing — see
/// <see cref="ApplySnapshot_ForAnIdTheBackendNeverIssued_IsUnknownInstanceAndCreatesNothing"/>. A test
/// that wants an upsert to actually apply must therefore start from a real granted row.
/// </summary>
public sealed class ApplySnapshotTests : IAsyncLifetime
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

    private static async Task<ItemId> CreateCatalogItemAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CreateItemCommand("Test Item", $"Test_{Guid.NewGuid():N}"));
        if (result is not CreateItemResult.Created created)
        {
            throw new InvalidOperationException($"Expected Created, got {result}");
        }

        return created.ItemId;
    }

    /// <summary>
    /// Mints a real, backend-issued instance — the only way an upsert can ever apply, since this path
    /// never inserts. The row lands <c>PendingSpawn</c> at <c>Revision</c> 0 with a null
    /// <c>RootGameServerId</c>, exactly as <c>ItemInstance.Register</c> documents.
    /// </summary>
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

    /// <summary>
    /// Grants and then acks, leaving a settled row: not pending, <c>Revision</c> 0,
    /// <c>RootGameServerId</c> stamped to this provider's gameserver. Most of the revision-LWW tests
    /// need this rather than a bare grant, because a still-<c>PendingSpawn</c> row deliberately
    /// bypasses the revision comparison entirely (the implicit-ack path).
    /// </summary>
    private static async Task<ItemInstanceId> GrantAndAckAsync(ServiceProvider provider, ItemId itemId, CharacterId owner)
    {
        var instanceId = await GrantOneAsync(provider, itemId, owner);

        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var result = await mediator.Send(new AcknowledgeSpawnsCommand(serverId, [new InstanceAckRequest(instanceId, [])]));
        if (result is not AcknowledgeSpawnsResult.Acknowledged)
        {
            throw new InvalidOperationException($"Expected Acknowledged, got {result}");
        }

        return instanceId;
    }

    private static async Task<ItemInstance?> LoadAsync(ServiceProvider provider, ItemInstanceId instanceId)
    {
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        return await repository.FindByIdAsync(instanceId, CancellationToken.None);
    }

    /// <summary>
    /// Reads a row back the way every ordinary query does — soft-deleted rows excluded by Marten's own
    /// <c>SoftDeletedWithIndex()</c> behaviour. Deliberately not <c>FindByIdAsync</c>: that goes
    /// through Marten's <c>LoadAsync</c>, which is a direct id fetch and still returns a soft-deleted
    /// document, so it cannot answer "is this row gone".
    /// </summary>
    private static async Task<ItemInstance?> LoadLiveAsync(ServiceProvider provider, ItemInstanceId instanceId)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();
        return await session.Query<ItemInstance>().Where(x => x.Id == instanceId).SingleOrDefaultAsync();
    }

    /// <summary>Reads a row back <i>including</i> soft-deleted ones — the only way to assert on what a delete actually wrote, since every normal read filters them out.</summary>
    private static async Task<ItemInstance?> LoadIncludingDeletedAsync(ServiceProvider provider, ItemInstanceId instanceId)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
        await using var session = store.QuerySession();
        return await session.Query<ItemInstance>()
            .Where(x => x.Id == instanceId && x.MaybeDeleted())
            .SingleOrDefaultAsync();
    }

    private static async Task<ApplySnapshotResult.Applied> ApplyAsync(ServiceProvider provider, ApplySnapshotCommand command)
    {
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(command);
        if (result is not ApplySnapshotResult.Applied applied)
        {
            throw new InvalidOperationException($"Expected Applied, got {result}");
        }

        return applied;
    }

    private static async Task<GameServerId> ServerIdAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
    }

    private static SnapshotUpsertRequest CharacterUpsert(
        ItemInstanceId instanceId,
        ItemId itemId,
        CharacterId characterId,
        long revision = 1,
        IReadOnlyDictionary<string, string>? attributes = null)
        => new(instanceId, revision, itemId, ParentKind.Character, characterId, null, null, null, null, null, attributes ?? new Dictionary<string, string>());

    private static SnapshotUpsertRequest ContainerUpsert(ItemInstanceId instanceId, ItemId itemId, ItemInstanceId containerInstanceId, long revision = 1)
        => new(instanceId, revision, itemId, ParentKind.Container, null, null, containerInstanceId, null, null, null, new Dictionary<string, string>());

    private static SnapshotUpsertRequest WorldUpsert(ItemInstanceId instanceId, ItemId itemId, long revision = 1)
        => new(
            instanceId,
            revision,
            itemId,
            ParentKind.World,
            null,
            null,
            null,
            new WorldTransform(new WorldVector3(10f, 20f, 30f), new WorldVector3(0f, 90f, 0f)),
            null,
            null,
            new Dictionary<string, string>());

    private static ApplySnapshotCommand CharacterScopedCommand(
        GameServerId serverId,
        CharacterId scopeCharacterId,
        IReadOnlyList<SnapshotUpsertRequest> upserts,
        IReadOnlyList<SnapshotDeleteRequest>? deletes = null)
        => new(serverId, Guid.NewGuid(), SnapshotScopeKind.Character, scopeCharacterId, null, null, SnapshotMode.Partial, upserts, deletes ?? []);

    /// <summary>Same as the overload above, but with an explicit <c>batchId</c> — task 3's replay tests need to send the exact same id twice.</summary>
    private static ApplySnapshotCommand CharacterScopedCommand(
        GameServerId serverId,
        Guid batchId,
        CharacterId scopeCharacterId,
        IReadOnlyList<SnapshotUpsertRequest> upserts,
        IReadOnlyList<SnapshotDeleteRequest>? deletes = null)
        => new(serverId, batchId, SnapshotScopeKind.Character, scopeCharacterId, null, null, SnapshotMode.Partial, upserts, deletes ?? []);

    /// <summary>Task 3: a <c>Full</c>-mode, <c>Character</c>-scoped batch carrying <paramref name="sequence"/> — the shape the <c>ScopeCursor</c> gate checks.</summary>
    private static ApplySnapshotCommand FullScopedCommand(
        GameServerId serverId,
        CharacterId scopeCharacterId,
        long sequence,
        IReadOnlyList<SnapshotUpsertRequest>? upserts = null,
        IReadOnlyList<SnapshotDeleteRequest>? deletes = null)
        => new(serverId, Guid.NewGuid(), SnapshotScopeKind.Character, scopeCharacterId, null, sequence, SnapshotMode.Full, upserts ?? [], deletes ?? []);

    /// <summary>Fix round 1, item 2: the <c>Container</c>-scope counterpart — the ScopeCursor gate's other half, checked from a different pipeline position (inside <c>ApplyAsync</c>) with a different <c>BuildKey</c> arm.</summary>
    private static ApplySnapshotCommand FullContainerScopedCommand(
        GameServerId serverId,
        ItemInstanceId scopeContainerInstanceId,
        long sequence,
        IReadOnlyList<SnapshotUpsertRequest>? upserts = null,
        IReadOnlyList<SnapshotDeleteRequest>? deletes = null)
        => new(serverId, Guid.NewGuid(), SnapshotScopeKind.Container, null, scopeContainerInstanceId, sequence, SnapshotMode.Full, upserts ?? [], deletes ?? []);

    /// <summary>Dispatches without assuming <c>Applied</c> — needed for task 3's batch-level rejections (<c>StaleSequence</c>) that <see cref="ApplyAsync"/> can't express.</summary>
    private static async Task<ApplySnapshotResult> DispatchAsync(ServiceProvider provider, ApplySnapshotCommand command)
    {
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }

    [Fact]
    public async Task ApplySnapshot_WithDuplicateInstanceIdsInOneBatch_RejectsTheWholeBatch()
    {
        var owner = await CreateCharacterAsync(_provider, "Duplicate Batch Character");
        var itemId = await CreateCatalogItemAsync(_provider);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var dupeId = new ItemInstanceId(Guid.NewGuid());
        var command = CharacterScopedCommand(
            serverId,
            owner,
            [CharacterUpsert(dupeId, itemId, owner), CharacterUpsert(dupeId, itemId, owner, revision: 2)]);

        var result = await mediator.Send(command);

        if (result is not ApplySnapshotResult.DuplicateInstanceId duplicate)
        {
            throw new InvalidOperationException($"Expected DuplicateInstanceId, got {result}");
        }

        Assert.Equal(dupeId, duplicate.InstanceId);
    }

    [Fact]
    public async Task ApplySnapshot_WithADeleteRepeatingAnUpsertsInstanceId_RejectsTheWholeBatch()
    {
        var owner = await CreateCharacterAsync(_provider, "Cross Array Duplicate Character");
        var itemId = await CreateCatalogItemAsync(_provider);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var dupeId = new ItemInstanceId(Guid.NewGuid());
        var command = CharacterScopedCommand(
            serverId,
            owner,
            [CharacterUpsert(dupeId, itemId, owner)],
            [new SnapshotDeleteRequest(dupeId, 1, DeleteReason.Consumed)]);

        var result = await mediator.Send(command);

        if (result is not ApplySnapshotResult.DuplicateInstanceId duplicate)
        {
            throw new InvalidOperationException($"Expected DuplicateInstanceId, got {result}");
        }

        Assert.Equal(dupeId, duplicate.InstanceId);
    }

    [Fact]
    public async Task ApplySnapshot_WithMoreUpsertsThanMaxUpsertsPerBatch_IsRejectedAsBatchTooLarge()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
        var settings = await mediator.Send(new WorldSettingsQuery());

        var owner = new CharacterId(Guid.NewGuid());
        var itemId = new ItemId(Guid.NewGuid());
        var tooMany = Enumerable.Range(0, settings.MaxUpsertsPerBatch + 1)
            .Select(_ => CharacterUpsert(new ItemInstanceId(Guid.NewGuid()), itemId, owner))
            .ToList();

        var result = await mediator.Send(CharacterScopedCommand(serverId, owner, tooMany));

        if (result is not ApplySnapshotResult.BatchTooLarge tooLarge)
        {
            throw new InvalidOperationException($"Expected BatchTooLarge, got {result}");
        }

        Assert.Equal("upserts", tooLarge.Field);
        Assert.Equal(settings.MaxUpsertsPerBatch + 1, tooLarge.Requested);
        Assert.Equal(settings.MaxUpsertsPerBatch, tooLarge.Max);
    }

    [Fact]
    public async Task ApplySnapshot_WithMoreDeletesThanMaxDeletesPerBatch_IsRejectedAsBatchTooLarge()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
        var settings = await mediator.Send(new WorldSettingsQuery());

        var owner = new CharacterId(Guid.NewGuid());
        var tooManyDeletes = Enumerable.Range(0, settings.MaxDeletesPerBatch + 1)
            .Select(_ => new SnapshotDeleteRequest(new ItemInstanceId(Guid.NewGuid()), 1, DeleteReason.Consumed))
            .ToList();

        var result = await mediator.Send(CharacterScopedCommand(serverId, owner, [], tooManyDeletes));

        if (result is not ApplySnapshotResult.BatchTooLarge tooLarge)
        {
            throw new InvalidOperationException($"Expected BatchTooLarge, got {result}");
        }

        Assert.Equal("deletes", tooLarge.Field);
        Assert.Equal(settings.MaxDeletesPerBatch + 1, tooLarge.Requested);
        Assert.Equal(settings.MaxDeletesPerBatch, tooLarge.Max);
    }

    [Fact]
    public async Task ApplySnapshot_WithAnUncatalogedItemId_RejectsThatInstanceAndAppliesTheRest()
    {
        var owner = await CreateCharacterAsync(_provider, "Uncataloged Snapshot Character");
        var knownItemId = await CreateCatalogItemAsync(_provider);
        var unknownItemId = new ItemId(Guid.NewGuid());
        var serverId = await ServerIdAsync(_provider);

        var goodInstanceId = await GrantAndAckAsync(_provider, knownItemId, owner);
        var badInstanceId = new ItemInstanceId(Guid.NewGuid());

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [CharacterUpsert(goodInstanceId, knownItemId, owner), CharacterUpsert(badInstanceId, unknownItemId, owner)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(badInstanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.UnknownItem, rejection.Reason);

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(0, applied.SkippedNoOp);
        Assert.Equal(0, applied.Deleted);
        Assert.False(applied.ReplayOfPriorBatch);

        var stored = await LoadAsync(_provider, goodInstanceId);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    public async Task ApplySnapshot_ForACharacterOnAnotherServer_RejectsThatInstanceAndAppliesTheRest()
    {
        var homeProvider = TestServices.BuildProvider("home-snapshot-server");
        var awayProvider = TestServices.BuildProvider("away-snapshot-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var homeCharacter = await CreateCharacterAsync(homeProvider, "Home Snapshot Character");
        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Snapshot Character");
        var itemId = await CreateCatalogItemAsync(homeProvider);

        await using var scope = homeProvider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var homeServerId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var onServerInstanceId = await GrantAndAckAsync(homeProvider, itemId, homeCharacter);
        var offServerInstanceId = await GrantOneAsync(awayProvider, itemId, awayCharacter);

        var command = CharacterScopedCommand(
            homeServerId,
            homeCharacter,
            [
                CharacterUpsert(onServerInstanceId, itemId, homeCharacter),
                CharacterUpsert(offServerInstanceId, itemId, awayCharacter),
            ]);

        var result = await mediator.Send(command);

        if (result is not ApplySnapshotResult.Applied applied)
        {
            throw new InvalidOperationException($"Expected Applied, got {result}");
        }

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(offServerInstanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.NotOnThisServer, rejection.Reason);
        Assert.Equal(1, applied.AppliedCount);
    }

    [Fact]
    public async Task ApplySnapshot_WhenTheScopeCharacterIsOnAnotherServer_ReturnsWrongServer()
    {
        var homeProvider = TestServices.BuildProvider("home-scope-server");
        var awayProvider = TestServices.BuildProvider("away-scope-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Scope Character");

        await using var scope = homeProvider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var homeServerId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var result = await mediator.Send(CharacterScopedCommand(homeServerId, awayCharacter, []));

        Assert.True(result is ApplySnapshotResult.WrongServer, $"Expected WrongServer, got {result}");
    }

    [Fact]
    public async Task ApplySnapshot_WithASelfReferencingContainerParent_RejectsThatInstanceAsCycleDetected()
    {
        var owner = await CreateCharacterAsync(_provider, "Cycle Snapshot Character");
        var itemId = await CreateCatalogItemAsync(_provider);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var selfId = new ItemInstanceId(Guid.NewGuid());
        var command = CharacterScopedCommand(serverId, owner, [ContainerUpsert(selfId, itemId, selfId)]);

        var result = await mediator.Send(command);

        if (result is not ApplySnapshotResult.Applied applied)
        {
            throw new InvalidOperationException($"Expected Applied, got {result}");
        }

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(selfId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.CycleDetected, rejection.Reason);
    }

    [Fact]
    public async Task ApplySnapshot_WithATransitiveContainerCycleWithinTheBatch_RejectsBothInstances()
    {
        var owner = await CreateCharacterAsync(_provider, "Transitive Cycle Snapshot Character");
        var itemId = await CreateCatalogItemAsync(_provider);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var a = new ItemInstanceId(Guid.NewGuid());
        var b = new ItemInstanceId(Guid.NewGuid());

        // a is parented into b, and b is parented into a — a cycle that only closes once both upserts
        // are considered together.
        var command = CharacterScopedCommand(
            serverId,
            owner,
            [ContainerUpsert(a, itemId, b), ContainerUpsert(b, itemId, a)]);

        var result = await mediator.Send(command);

        if (result is not ApplySnapshotResult.Applied applied)
        {
            throw new InvalidOperationException($"Expected Applied, got {result}");
        }

        Assert.Equal(2, applied.Rejected.Count);
        Assert.All(applied.Rejected, x => Assert.Equal(SnapshotRejectionReason.CycleDetected, x.Reason));
        Assert.Contains(applied.Rejected, x => x.InstanceId == a);
        Assert.Contains(applied.Rejected, x => x.InstanceId == b);
    }

    [Fact]
    public async Task ApplySnapshot_WithAttributesExceedingTheKeyLimit_RejectsThatInstance()
    {
        var owner = await CreateCharacterAsync(_provider, "Attribute Limit Snapshot Character");
        var itemId = await CreateCatalogItemAsync(_provider);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);

        var tooMany = Enumerable.Range(0, ItemAttributes.MaxKeys + 1).ToDictionary(i => $"key{i}", i => $"value{i}");
        var instanceId = new ItemInstanceId(Guid.NewGuid());

        var command = CharacterScopedCommand(serverId, owner, [CharacterUpsert(instanceId, itemId, owner, attributes: tooMany)]);

        var result = await mediator.Send(command);

        if (result is not ApplySnapshotResult.Applied applied)
        {
            throw new InvalidOperationException($"Expected Applied, got {result}");
        }

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(instanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.AttributeLimit, rejection.Reason);
    }

    /// <summary>
    /// The sole-minter rule, and the single most important test in this file: an upsert naming an id
    /// the backend never issued is rejected and <b>creates nothing</b>. Nothing in Reforger splits or
    /// stacks, so the mod has no legitimate reason to mint an id — which makes this the strongest
    /// anti-duplication lever the design has. There is no parent kind and no flag that makes it legal.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_ForAnIdTheBackendNeverIssued_IsUnknownInstanceAndCreatesNothing()
    {
        var owner = await CreateCharacterAsync(_provider, "Sole Minter Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var inventedId = new ItemInstanceId(Guid.NewGuid());

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(inventedId, itemId, owner)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(inventedId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.UnknownInstance, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);

        // Not merely "absent from the live read" — absent including soft-deleted rows, so this can't
        // pass by the row having been created and then filtered out.
        Assert.Null(await LoadIncludingDeletedAsync(_provider, inventedId));
    }

    /// <summary>A world-parented upsert of an invented id is rejected identically — the rule has no per-parent-kind exception.</summary>
    [Fact]
    public async Task ApplySnapshot_ForAnIdTheBackendNeverIssuedParentedToTheWorld_IsAlsoUnknownInstance()
    {
        var owner = await CreateCharacterAsync(_provider, "World Sole Minter Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var inventedId = new ItemInstanceId(Guid.NewGuid());

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [WorldUpsert(inventedId, itemId)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.UnknownInstance, rejection.Reason);
        Assert.Null(await LoadIncludingDeletedAsync(_provider, inventedId));
    }

    [Fact]
    public async Task ApplySnapshot_WithARevisionLowerThanTheStoredOne_SkipsItAndCountsItAsNoOp()
    {
        var owner = await CreateCharacterAsync(_provider, "Stale Revision Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: 5)]));

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: 4, attributes: new Dictionary<string, string> { ["paint"] = "green" })]));

        Assert.Empty(applied.Rejected);
        Assert.Equal(0, applied.AppliedCount);
        Assert.Equal(1, applied.SkippedNoOp);

        var stored = await LoadAsync(_provider, instanceId);
        Assert.NotNull(stored);
        Assert.Equal(5, stored.Revision);
        Assert.Empty(stored.Attributes.Values);
    }

    [Fact]
    public async Task ApplySnapshot_WithAnEqualRevisionAndIdenticalContent_IsANoOp()
    {
        var owner = await CreateCharacterAsync(_provider, "Idempotent Revision Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var upsert = CharacterUpsert(instanceId, itemId, owner, revision: 3);

        var first = await ApplyAsync(_provider, CharacterScopedCommand(serverId, owner, [upsert]));
        Assert.Equal(1, first.AppliedCount);

        var second = await ApplyAsync(_provider, CharacterScopedCommand(serverId, owner, [upsert]));

        Assert.Empty(second.Rejected);
        Assert.Equal(0, second.AppliedCount);
        Assert.Equal(1, second.SkippedNoOp);
    }

    [Fact]
    public async Task ApplySnapshot_WithAnEqualRevisionAndDifferentContent_IsAnIdentityConflict()
    {
        var owner = await CreateCharacterAsync(_provider, "Identity Conflict Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: 7)]));

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [CharacterUpsert(instanceId, itemId, owner, revision: 7, attributes: new Dictionary<string, string> { ["serial"] = "A-1" })]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(instanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.IdentityConflict, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);
        Assert.Equal(0, applied.SkippedNoOp);

        // Never a silent overwrite: the conflicting content did not land.
        var stored = await LoadAsync(_provider, instanceId);
        Assert.NotNull(stored);
        Assert.Empty(stored.Attributes.Values);
    }

    /// <summary>A UUIDv4 collision (or a mod bug) becomes an alert, never an item swap.</summary>
    [Fact]
    public async Task ApplySnapshot_WithADifferentItemIdOnAKnownInstance_IsAnIdentityConflict()
    {
        var owner = await CreateCharacterAsync(_provider, "Item Swap Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var otherItemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, otherItemId, owner, revision: 2)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(instanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.IdentityConflict, rejection.Reason);

        var stored = await LoadAsync(_provider, instanceId);
        Assert.NotNull(stored);
        Assert.Equal(itemId, stored.ItemId);
        Assert.Equal(0, stored.Revision);
    }

    [Fact]
    public async Task ApplySnapshot_ForARemovedByStaffInstance_IsRejectedAndNotResurrected()
    {
        var owner = await CreateCharacterAsync(_provider, "Tombstoned Snapshot Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        // No domain method sets RemovedByStaff before the staff tooling lands — patch it directly, the
        // same way AcknowledgeSpawnsTests does for its own tombstone test.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Patch<ItemInstance>(instanceId.Value).Set(x => x.RemovedByStaff, true);
            await session.SaveChangesAsync();
        }

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: 9)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.RemovedByStaff, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);

        var stored = await LoadAsync(_provider, instanceId);
        Assert.NotNull(stored);
        Assert.True(stored.RemovedByStaff);
        Assert.Equal(0, stored.Revision);
    }

    /// <summary>
    /// The implicit-ack path: the mod reporting a still-pending instance <i>is</i> proof it adopted
    /// it, which is what lets a lost <c>POST /api/inventory/acks</c> recover without the item being
    /// re-spawned. Also covers "a pending row's first upsert applies regardless of revision" — the
    /// upsert here carries revision 0, the same value the backend minted the row at, so an unguarded
    /// last-write-wins comparison would silently discard it and strand the row in the pending queue
    /// forever.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_ForAPendingInstanceAtTheSameRevision_AppliesAndClearsPendingSpawn()
    {
        var owner = await CreateCharacterAsync(_provider, "Implicit Ack Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantOneAsync(_provider, itemId, owner);

        var granted = await LoadAsync(_provider, instanceId);
        Assert.NotNull(granted);
        Assert.True(granted.PendingSpawn);
        Assert.Equal(0, granted.Revision);
        Assert.Null(granted.RootGameServerId);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: 0)]));

        Assert.Empty(applied.Rejected);
        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(0, applied.SkippedNoOp);

        var stored = await LoadAsync(_provider, instanceId);
        Assert.NotNull(stored);
        Assert.False(stored.PendingSpawn);
        Assert.Equal(serverId, stored.RootGameServerId);
        Assert.Equal(owner, stored.RootCharacterId);
    }

    /// <summary>
    /// Worked example C: a granted item consumed before its ack ever landed. The delete must clear
    /// <c>PendingSpawn</c> as it removes the row — leaving the flag set would re-offer, and re-spawn,
    /// a legitimately-consumed item at the character's next login.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_DeletingAPendingInstance_ClearsTheFlagAsItDeletes()
    {
        var owner = await CreateCharacterAsync(_provider, "Consumed Before Ack Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantOneAsync(_provider, itemId, owner);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [], [new SnapshotDeleteRequest(instanceId, 0, DeleteReason.Consumed)]));

        Assert.Empty(applied.Rejected);
        Assert.Equal(1, applied.Deleted);
        // Nothing was inside it, so nothing went with it.
        Assert.Equal(0, applied.CascadeDeleted);

        Assert.Null(await LoadLiveAsync(_provider, instanceId));

        var tombstoned = await LoadIncludingDeletedAsync(_provider, instanceId);
        Assert.NotNull(tombstoned);
        Assert.False(tombstoned.PendingSpawn);
    }

    [Fact]
    public async Task ApplySnapshot_WithADeleteRevisionLowerThanTheStoredOne_IsRejectedAsStaleRevision()
    {
        var owner = await CreateCharacterAsync(_provider, "Stale Delete Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: 6)]));

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [], [new SnapshotDeleteRequest(instanceId, 5, DeleteReason.Destroyed)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(instanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.StaleRevision, rejection.Reason);
        Assert.Equal(0, applied.Deleted);

        Assert.NotNull(await LoadLiveAsync(_provider, instanceId));
    }

    [Fact]
    public async Task ApplySnapshot_DeletingAnIdTheBackendNeverIssued_IsUnknownInstance()
    {
        var owner = await CreateCharacterAsync(_provider, "Unknown Delete Character");
        var serverId = await ServerIdAsync(_provider);
        var inventedId = new ItemInstanceId(Guid.NewGuid());

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [], [new SnapshotDeleteRequest(inventedId, 1, DeleteReason.Despawned)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.UnknownInstance, rejection.Reason);
        Assert.Equal(0, applied.Deleted);
    }

    /// <summary>
    /// The descendant cascade. <c>ItemInstance.MoveToContainer</c> propagates forward only, so moving a
    /// container leaves everything nested inside it with stale roots until something holding the whole
    /// chain rewrites them. Here a crate is moved from its owner onto the ground, and the bag inside it
    /// — a row this batch never mentions, loaded only because it is the upserted item's own container —
    /// must lose its <c>RootCharacterId</c> and pick up the crate's ground TTL. All three fields
    /// travel; the TTL is the one that breaks quietly.
    ///
    /// The bag is also the case that must be written as an atomic patch rather than a document
    /// replacement: it is a row the batch otherwise says nothing about, and replacing it wholesale
    /// from a loaded copy is exactly the lost update that resurrects a <c>PendingSpawn</c> flag.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_MovingAContainerToTheWorld_RewritesItsDescendantsRootFields()
    {
        var owner = await CreateCharacterAsync(_provider, "Container Cascade Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var bagId = await GrantAndAckAsync(_provider, itemId, owner);
        var trinketId = await GrantAndAckAsync(_provider, itemId, owner);

        // Nest them first: crate (on the character) > bag > trinket.
        var nested = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [ContainerUpsert(bagId, itemId, crateId), ContainerUpsert(trinketId, itemId, bagId)]));
        Assert.Empty(nested.Rejected);
        Assert.Equal(2, nested.AppliedCount);

        var nestedBag = await LoadAsync(_provider, bagId);
        Assert.NotNull(nestedBag);
        Assert.Equal(owner, nestedBag.RootCharacterId);
        Assert.Null(nestedBag.ExpiresAt);

        // Now drop the crate. The batch names the crate and the trinket; the bag in between is pulled
        // in only as the trinket's container, and is never upserted.
        var dropped = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [WorldUpsert(crateId, itemId), ContainerUpsert(trinketId, itemId, bagId, revision: 2)]));
        Assert.Empty(dropped.Rejected);
        Assert.Equal(2, dropped.AppliedCount);

        var crate = await LoadAsync(_provider, crateId);
        Assert.NotNull(crate);
        Assert.Null(crate.RootCharacterId);
        Assert.Equal(serverId, crate.RootGameServerId);
        Assert.NotNull(crate.ExpiresAt);

        var bag = await LoadAsync(_provider, bagId);
        Assert.NotNull(bag);
        Assert.Null(bag.RootCharacterId);
        Assert.Equal(serverId, bag.RootGameServerId);
        Assert.Equal(crate.ExpiresAt, bag.ExpiresAt);

        // Regression cover on the fields a root rewrite must leave alone. Note what this does NOT
        // prove: the bag was loaded fresh inside the same handler, so a whole-document Store() would
        // have written these same values and this assertion would pass unchanged. It is a tripwire for
        // a future change that starts mutating them, not evidence that the write was a patch. (The
        // patch-versus-replacement requirement is a concurrency property — a stale in-memory copy
        // racing another writer — which no single-threaded test of this shape can observe; see the
        // task report.)
        Assert.Equal(1, bag.Revision);
        Assert.False(bag.PendingSpawn);

        var trinket = await LoadAsync(_provider, trinketId);
        Assert.NotNull(trinket);
        Assert.Null(trinket.RootCharacterId);
        Assert.Equal(serverId, trinket.RootGameServerId);
        Assert.Equal(crate.ExpiresAt, trinket.ExpiresAt);
    }

    /// <summary>
    /// The same cascade, but the batch names <b>only</b> the crate — no descendant is mentioned at all,
    /// which is the realistic case: the mod is under no obligation to re-report the inside of a crate
    /// it merely moved. Reaching them needs the downward chain walk.
    ///
    /// The harm this prevents is concrete rather than cosmetic. <c>RootCharacterId</c> is the hot
    /// inventory read, so a crate that changes hands with stale contents surfaces those contents in the
    /// <i>previous</i> player's inventory.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_MovingAContainerWithoutMentioningItsContents_StillRewritesTheirRootFields()
    {
        var owner = await CreateCharacterAsync(_provider, "Unmentioned Descendants Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var bagId = await GrantAndAckAsync(_provider, itemId, owner);
        var trinketId = await GrantAndAckAsync(_provider, itemId, owner);

        var nested = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [ContainerUpsert(bagId, itemId, crateId), ContainerUpsert(trinketId, itemId, bagId)]));
        Assert.Empty(nested.Rejected);

        // One entry. Nothing names the bag or the trinket.
        var dropped = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [WorldUpsert(crateId, itemId)]));
        Assert.Empty(dropped.Rejected);
        Assert.Equal(1, dropped.AppliedCount);

        var crate = await LoadAsync(_provider, crateId);
        Assert.NotNull(crate);
        Assert.NotNull(crate.ExpiresAt);

        foreach (var descendantId in new[] { bagId, trinketId })
        {
            var descendant = await LoadAsync(_provider, descendantId);
            Assert.NotNull(descendant);
            Assert.Null(descendant.RootCharacterId);
            Assert.Equal(serverId, descendant.RootGameServerId);
            Assert.Equal(crate.ExpiresAt, descendant.ExpiresAt);
        }

        // And the concrete consequence: they no longer answer the owner's carried-inventory read.
        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var carried = await repository.FindCarriedByRootCharacterAsync(owner, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.DoesNotContain(carried, x => x.Id == bagId || x.Id == trinketId || x.Id == crateId);
    }

    /// <summary>
    /// The half of the cycle guard that needs the load. Task 1's in-batch walk stops the moment a chain
    /// leaves the batch; merging the stored rows' own parent edges in is what closes a loop that runs
    /// batch → storage → batch.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_WithAContainerCycleThatClosesThroughStoredRows_IsRejectedAsCycleDetected()
    {
        var owner = await CreateCharacterAsync(_provider, "Stored Cycle Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var outerId = await GrantAndAckAsync(_provider, itemId, owner);
        var innerId = await GrantAndAckAsync(_provider, itemId, owner);

        // Stored state: inner is inside outer.
        var nested = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(innerId, itemId, outerId)]));
        Assert.Empty(nested.Rejected);

        // Now claim outer is inside inner. That edge is fine on its own — the loop only closes against
        // the stored inner → outer edge, which no in-batch walk can see.
        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(outerId, itemId, innerId, revision: 2)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(outerId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.CycleDetected, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);
    }

    /// <summary>
    /// The non-character half of the server guard. A container-parented upsert names no character at
    /// all, so the only thing that can say where the instance currently is, is its own stored,
    /// denormalised <c>RootGameServerId</c>.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_ForAContainerParentedInstanceOnAnotherServer_IsRejectedAsNotOnThisServer()
    {
        var homeProvider = TestServices.BuildProvider("home-container-guard-server");
        var awayProvider = TestServices.BuildProvider("away-container-guard-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var homeCharacter = await CreateCharacterAsync(homeProvider, "Home Container Guard Character");
        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Container Guard Character");
        var itemId = await CreateCatalogItemAsync(homeProvider);

        var homeServerId = await ServerIdAsync(homeProvider);
        var awayCrateId = await GrantAndAckAsync(awayProvider, itemId, awayCharacter);
        var awayItemId = await GrantAndAckAsync(awayProvider, itemId, awayCharacter);

        var applied = await ApplyAsync(homeProvider, CharacterScopedCommand(
            homeServerId, homeCharacter, [ContainerUpsert(awayItemId, itemId, awayCrateId)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(awayItemId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.NotOnThisServer, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);
    }

    /// <summary>
    /// The same guard one level up, and the case the row's own field can't answer: a still-pending row
    /// carries a null <c>RootGameServerId</c> by construction (that is what makes it adoptable by the
    /// implicit-ack path), so without checking the container it is being nested into, any gameserver
    /// could nest it into a crate sitting on a different map.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_NestingAPendingInstanceIntoAContainerOnAnotherServer_IsRejectedAsNotOnThisServer()
    {
        var homeProvider = TestServices.BuildProvider("home-parent-guard-server");
        var awayProvider = TestServices.BuildProvider("away-parent-guard-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var homeCharacter = await CreateCharacterAsync(homeProvider, "Home Parent Guard Character");
        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Parent Guard Character");
        var itemId = await CreateCatalogItemAsync(homeProvider);

        var homeServerId = await ServerIdAsync(homeProvider);
        var awayCrateId = await GrantAndAckAsync(awayProvider, itemId, awayCharacter);

        // Granted but never acked, so its own RootGameServerId is still null and the row-level guard
        // has nothing to reject on.
        var pendingId = await GrantOneAsync(homeProvider, itemId, homeCharacter);
        var pending = await LoadAsync(homeProvider, pendingId);
        Assert.NotNull(pending);
        Assert.Null(pending.RootGameServerId);

        var applied = await ApplyAsync(homeProvider, CharacterScopedCommand(
            homeServerId, homeCharacter, [ContainerUpsert(pendingId, itemId, awayCrateId)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(pendingId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.NotOnThisServer, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);

        // And the pending flag it would have adopted is untouched — a rejected upsert never clears it.
        var stored = await LoadAsync(homeProvider, pendingId);
        Assert.NotNull(stored);
        Assert.True(stored.PendingSpawn);
    }

    /// <summary>A delete names no parent either, so it guards the same way.</summary>
    [Fact]
    public async Task ApplySnapshot_DeletingAnInstanceOnAnotherServer_IsRejectedAsNotOnThisServer()
    {
        var homeProvider = TestServices.BuildProvider("home-delete-guard-server");
        var awayProvider = TestServices.BuildProvider("away-delete-guard-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var homeCharacter = await CreateCharacterAsync(homeProvider, "Home Delete Guard Character");
        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Delete Guard Character");
        var itemId = await CreateCatalogItemAsync(homeProvider);

        var homeServerId = await ServerIdAsync(homeProvider);
        var awayInstanceId = await GrantAndAckAsync(awayProvider, itemId, awayCharacter);

        var applied = await ApplyAsync(homeProvider, CharacterScopedCommand(
            homeServerId, homeCharacter, [], [new SnapshotDeleteRequest(awayInstanceId, 1, DeleteReason.Traded)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.NotOnThisServer, rejection.Reason);
        Assert.Equal(0, applied.Deleted);

        Assert.NotNull(await LoadLiveAsync(awayProvider, awayInstanceId));
    }

    /// <summary>
    /// A <c>Container</c>-scoped batch has no character to check, so its scope is guarded against the
    /// container row's own stored <c>RootGameServerId</c> — the counterpart to the character-scope
    /// check. Fails the whole batch, same as the character case: a batch whose declared subject isn't
    /// reachable from here is meaningless whatever its entries say.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_WhenTheScopeContainerIsOnAnotherServer_ReturnsWrongServer()
    {
        var homeProvider = TestServices.BuildProvider("home-scope-container-server");
        var awayProvider = TestServices.BuildProvider("away-scope-container-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Scope Container Character");
        var itemId = await CreateCatalogItemAsync(awayProvider);
        var awayCrateId = await GrantAndAckAsync(awayProvider, itemId, awayCharacter);

        var homeServerId = await ServerIdAsync(homeProvider);

        await using var scope = homeProvider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ApplySnapshotCommand(
            homeServerId, Guid.NewGuid(), SnapshotScopeKind.Container, null, awayCrateId, null, SnapshotMode.Partial, [], []));

        Assert.True(result is ApplySnapshotResult.WrongServer, $"Expected WrongServer, got {result}");
    }

    [Fact]
    public async Task ApplySnapshot_WithANegativeRevision_IsRejectedAsValueOutOfRange()
    {
        var owner = await CreateCharacterAsync(_provider, "Negative Revision Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: -1)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(instanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.ValueOutOfRange, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.5f)]
    [InlineData(float.NaN)]
    public async Task ApplySnapshot_WithADurabilityOutsideTheZeroToOneFraction_IsRejectedAsValueOutOfRange(float durability)
    {
        var owner = await CreateCharacterAsync(_provider, $"Durability Character {durability}");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var upsert = new SnapshotUpsertRequest(
            instanceId, 1, itemId, ParentKind.Character, owner, null, null, null, durability, null, new Dictionary<string, string>());

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(serverId, owner, [upsert]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.ValueOutOfRange, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);
    }

    [Fact]
    public async Task ApplySnapshot_WithNegativeAmmo_IsRejectedAsValueOutOfRange()
    {
        var owner = await CreateCharacterAsync(_provider, "Negative Ammo Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var upsert = new SnapshotUpsertRequest(
            instanceId, 1, itemId, ParentKind.Character, owner, null, null, null, null, -3, new Dictionary<string, string>());

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(serverId, owner, [upsert]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.ValueOutOfRange, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);
    }

    /// <summary>
    /// The bounds check runs in the in-memory phase, before the load round trip — so a batch whose only
    /// problem is a nonsense scalar never reads a row at all. Proven from the outside by a negative
    /// revision on an id the backend never issued: if the load ran first this would come back
    /// <c>UnknownInstance</c>, since that check is the one that consults storage.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_WithAnOutOfRangeScalarOnAnUnknownId_ReportsTheBoundsFailureRatherThanTheStoredLookup()
    {
        var owner = await CreateCharacterAsync(_provider, "Bounds Precedence Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(new ItemInstanceId(Guid.NewGuid()), itemId, owner, revision: -5)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.ValueOutOfRange, rejection.Reason);
    }

    /// <summary>The delete array's revision is bounded the same way.</summary>
    [Fact]
    public async Task ApplySnapshot_WithANegativeDeleteRevision_IsRejectedAsValueOutOfRange()
    {
        var owner = await CreateCharacterAsync(_provider, "Negative Delete Revision Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [], [new SnapshotDeleteRequest(instanceId, -1, DeleteReason.Unknown)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.ValueOutOfRange, rejection.Reason);
        Assert.Equal(0, applied.Deleted);
        Assert.NotNull(await LoadLiveAsync(_provider, instanceId));
    }

    /// <summary>
    /// Task 2 review, item 1 — the guard that had to be added rather than merely documented. A freshly
    /// granted row is <c>PendingSpawn</c> with a <b>null</b> <c>RootGameServerId</c>, so the
    /// stored-root server check can never reject it, and nothing else in the batch names a character.
    /// Without a scope check, any gameserver holding the id could world-parent another server's paid,
    /// undelivered grant onto its own map: the descendant resolution would strip
    /// <c>RootCharacterId</c>, stamp the caller as the delivery server and start a despawn timer, and
    /// the character who was owed the item would never receive it.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FromAForeignServerWorldParentingAnotherCharactersPendingGrant_IsRejectedAsNotOnThisServer()
    {
        var homeProvider = TestServices.BuildProvider("home-pending-theft-server");
        var awayProvider = TestServices.BuildProvider("away-pending-theft-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var homeCharacter = await CreateCharacterAsync(homeProvider, "Home Pending Theft Character");
        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Pending Theft Character");
        var itemId = await CreateCatalogItemAsync(homeProvider);

        // Paid for and owed to the home character; never delivered, so RootGameServerId is null.
        var pendingId = await GrantOneAsync(homeProvider, itemId, homeCharacter);
        var beforeTheAttempt = await LoadAsync(homeProvider, pendingId);
        Assert.NotNull(beforeTheAttempt);
        Assert.True(beforeTheAttempt.PendingSpawn);
        Assert.Null(beforeTheAttempt.RootGameServerId);

        // The away server describes its own character's inventory — a scope that passes the
        // batch-level guard — while naming an instance owed to somebody on another server.
        var awayServerId = await ServerIdAsync(awayProvider);
        var applied = await ApplyAsync(awayProvider, CharacterScopedCommand(
            awayServerId, awayCharacter, [WorldUpsert(pendingId, itemId)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(pendingId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.NotOnThisServer, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);

        // Still owed, still rootless, still not despawning.
        var after = await LoadAsync(homeProvider, pendingId);
        Assert.NotNull(after);
        Assert.True(after.PendingSpawn);
        Assert.Equal(homeCharacter, after.RootCharacterId);
        Assert.Null(after.RootGameServerId);
        Assert.Null(after.ExpiresAt);
        Assert.Equal(ParentKind.Character, after.ParentKind);
    }

    /// <summary>The same hole on the delete side: a foreign server must not be able to destroy another character's paid, undelivered grant either.</summary>
    [Fact]
    public async Task ApplySnapshot_FromAForeignServerDeletingAnotherCharactersPendingGrant_IsRejectedAsNotOnThisServer()
    {
        var homeProvider = TestServices.BuildProvider("home-pending-delete-server");
        var awayProvider = TestServices.BuildProvider("away-pending-delete-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var homeCharacter = await CreateCharacterAsync(homeProvider, "Home Pending Delete Character");
        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Pending Delete Character");
        var itemId = await CreateCatalogItemAsync(homeProvider);

        var pendingId = await GrantOneAsync(homeProvider, itemId, homeCharacter);
        var awayServerId = await ServerIdAsync(awayProvider);

        var applied = await ApplyAsync(awayProvider, CharacterScopedCommand(
            awayServerId, awayCharacter, [], [new SnapshotDeleteRequest(pendingId, 0, DeleteReason.Consumed)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(SnapshotRejectionReason.NotOnThisServer, rejection.Reason);
        Assert.Equal(0, applied.Deleted);

        var after = await LoadLiveAsync(homeProvider, pendingId);
        Assert.NotNull(after);
        Assert.True(after.PendingSpawn);
    }

    /// <summary>
    /// The <c>Container</c>-scope form of the same hole. A pending container has a null
    /// <c>RootGameServerId</c> too, and a <c>Container</c> scope carries no character to fall back on
    /// the way a <c>Character</c> scope does — so a container nobody has taken delivery of yet is not
    /// somewhere any gameserver can claim to be standing.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_WhenTheScopeContainerIsStillPending_ReturnsWrongServer()
    {
        var owner = await CreateCharacterAsync(_provider, "Pending Scope Container Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var pendingCrateId = await GrantOneAsync(_provider, itemId, owner);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ApplySnapshotCommand(
            serverId, Guid.NewGuid(), SnapshotScopeKind.Container, null, pendingCrateId, null, SnapshotMode.Partial, [], []));

        Assert.True(result is ApplySnapshotResult.WrongServer, $"Expected WrongServer, got {result}");
    }

    /// <summary>
    /// Task 2 review, item 2. The spec settles the policy: soft-deleting a container soft-deletes its
    /// descendants, and a row whose <c>ContainerInstanceId</c> points at a deleted row must never be
    /// reachable. Without the cascade a child of a deleted crate keeps its <c>RootCharacterId</c> and
    /// is still returned by the carried-inventory read, parented to a row that no longer exists.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_DeletingAContainer_CascadesTheSoftDeleteToItsDescendants()
    {
        var owner = await CreateCharacterAsync(_provider, "Cascade Delete Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var bagId = await GrantAndAckAsync(_provider, itemId, owner);
        var trinketId = await GrantAndAckAsync(_provider, itemId, owner);

        var nested = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [ContainerUpsert(bagId, itemId, crateId), ContainerUpsert(trinketId, itemId, bagId)]));
        Assert.Empty(nested.Rejected);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [], [new SnapshotDeleteRequest(crateId, 1, DeleteReason.Destroyed)]));

        Assert.Empty(applied.Rejected);
        // Only the delete the caller actually asked for is counted in `deleted`, so its own arithmetic
        // over the deletes array still closes — and the bag and the trinket that went with it are
        // reported in `cascadeDeleted` rather than left for the caller to infer.
        Assert.Equal(1, applied.Deleted);
        Assert.Equal(2, applied.CascadeDeleted);

        foreach (var id in new[] { crateId, bagId, trinketId })
        {
            Assert.Null(await LoadLiveAsync(_provider, id));
            Assert.NotNull(await LoadIncludingDeletedAsync(_provider, id));
        }

        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var carried = await repository.FindCarriedByRootCharacterAsync(owner, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.DoesNotContain(carried, x => x.Id == crateId || x.Id == bagId || x.Id == trinketId);
    }

    /// <summary>
    /// The cascade walks <i>post-diff</i> parent edges, which is what makes a batch that both moves
    /// things and deletes a container deterministic rather than an accident of statement ordering: an
    /// instance moved <b>into</b> the doomed crate goes with it, one moved <b>out</b> of it survives,
    /// and neither outcome depends on where the entries sat in the request arrays.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_DeletingAContainerTheSameBatchMovesThingsIntoAndOutOf_ResolvesByPostBatchParentage()
    {
        var owner = await CreateCharacterAsync(_provider, "Move And Delete Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var movedInId = await GrantAndAckAsync(_provider, itemId, owner);
        var movedOutId = await GrantAndAckAsync(_provider, itemId, owner);

        // movedOut starts inside the crate.
        var nested = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(movedOutId, itemId, crateId)]));
        Assert.Empty(nested.Rejected);

        // One batch: movedIn goes in, movedOut comes out onto the character, and the crate is deleted.
        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [
                ContainerUpsert(movedInId, itemId, crateId),
                CharacterUpsert(movedOutId, itemId, owner, revision: 2),
            ],
            [new SnapshotDeleteRequest(crateId, 1, DeleteReason.Destroyed)]));

        Assert.Empty(applied.Rejected);
        Assert.Equal(1, applied.Deleted);
        Assert.Equal(1, applied.CascadeDeleted);
        // movedIn was applied and then cascaded out of existence, so it is not counted as applied —
        // nothing the upsert said survived. movedOut is the one real write.
        Assert.Equal(1, applied.AppliedCount);

        Assert.Null(await LoadLiveAsync(_provider, crateId));
        Assert.Null(await LoadLiveAsync(_provider, movedInId));

        var survivor = await LoadLiveAsync(_provider, movedOutId);
        Assert.NotNull(survivor);
        Assert.Equal(ParentKind.Character, survivor.ParentKind);
        Assert.Equal(owner, survivor.RootCharacterId);
    }

    /// <summary>
    /// Task 2 review, item 5. A rejected upsert will never be written, so asserting its declared
    /// parent edge when asking "will the post-batch stored graph be acyclic" invents a loop that
    /// neither stored nor post-batch state contains — and rejects a perfectly valid sibling for it.
    ///
    /// Shape: the bag is stored inside the crate. The batch tries to put the crate inside the bag
    /// (rejected at the catalog step, before the stored graph is ever consulted) and, independently,
    /// to put a trinket inside the bag. Only the second edge is real, and the trinket must apply.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_WithARejectedUpsertDeclaringAConflictingParent_DoesNotFalselyRejectAValidSibling()
    {
        var owner = await CreateCharacterAsync(_provider, "False Cycle Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var uncatalogedItemId = new ItemId(Guid.NewGuid());
        var serverId = await ServerIdAsync(_provider);

        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var bagId = await GrantAndAckAsync(_provider, itemId, owner);
        var trinketId = await GrantAndAckAsync(_provider, itemId, owner);

        // Stored: bag inside crate.
        var nested = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(bagId, itemId, crateId)]));
        Assert.Empty(nested.Rejected);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [
                // Rejected UnknownItem at the catalog step — its "crate inside bag" edge is never
                // going to be written, and neither the batch alone nor storage alone contains a cycle.
                ContainerUpsert(crateId, uncatalogedItemId, bagId, revision: 2),
                ContainerUpsert(trinketId, itemId, bagId),
            ]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(crateId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.UnknownItem, rejection.Reason);

        Assert.Equal(1, applied.AppliedCount);

        var trinket = await LoadAsync(_provider, trinketId);
        Assert.NotNull(trinket);
        Assert.Equal(ParentKind.Container, trinket.ParentKind);
        Assert.Equal(bagId, trinket.ContainerInstanceId);
        Assert.Equal(owner, trinket.RootCharacterId);
    }

    private static ServiceProvider BuildCountingProvider(string clientId, out RepositoryCallCounts counts)
    {
        var tally = new RepositoryCallCounts();
        counts = tally;
        return TestServices.BuildProvider(clientId, services =>
        {
            services.AddSingleton(tally);
            services.AddScoped<MartenItemInstanceRepository>();
            services.AddScoped<IItemInstanceRepository>(sp =>
                new InstrumentedItemInstanceRepository(sp.GetRequiredService<MartenItemInstanceRepository>(), tally));
        });
    }

    /// <summary>
    /// Task 2 review, item 4. The id set handed to the load is filtered by the rejections, so a batch
    /// whose every entry was already rejected in the in-memory phase reads no instance row at all.
    /// Asserted rather than argued: without the filter this issues one <c>LoadManyAsync</c> for ids
    /// whose verdicts are already settled, and "a malformed batch never touches Postgres" becomes
    /// "almost never".
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_WhereEveryEntryWasAlreadyRejected_ReadsNoInstanceRow()
    {
        var provider = BuildCountingProvider("counting-no-read-server", out var counts);
        await using var _ = provider;

        var owner = await CreateCharacterAsync(provider, "No Read Character");
        var itemId = await CreateCatalogItemAsync(provider);
        var serverId = await ServerIdAsync(provider);

        // Two real, stored rows — so a load would genuinely find something — each rejected in memory
        // before the load: one on scalar bounds, one on the attribute cap.
        var boundsId = await GrantAndAckAsync(provider, itemId, owner);
        var attributesId = await GrantAndAckAsync(provider, itemId, owner);
        var tooManyAttributes = Enumerable.Range(0, ItemAttributes.MaxKeys + 1).ToDictionary(i => $"key{i}", i => $"value{i}");

        counts.Reset();

        var applied = await ApplyAsync(provider, CharacterScopedCommand(
            serverId,
            owner,
            [
                CharacterUpsert(boundsId, itemId, owner, revision: -1),
                CharacterUpsert(attributesId, itemId, owner, attributes: tooManyAttributes),
            ],
            [new SnapshotDeleteRequest(new ItemInstanceId(Guid.NewGuid()), -2, DeleteReason.Unknown)]));

        Assert.Equal(3, applied.Rejected.Count);
        Assert.Equal(0, applied.AppliedCount);
        Assert.Equal(0, applied.Deleted);

        Assert.Equal(0, counts.LoadManyCalls);
        Assert.Equal(0, counts.FindChildrenOfManyCalls);
    }

    /// <summary>
    /// Task 2 review, item 3. Reaching ancestors and descendants added queries beyond the primary
    /// load, and the property that has to survive is that they are bounded by container depth rather
    /// than by how big the batch is — never a load per instance.
    ///
    /// Runs the same one-deep, one-container shape at two very different batch sizes and asserts the
    /// query count is identical.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_WithManyMoreEntries_IssuesTheSameNumberOfQueriesAsWithFew()
    {
        var provider = BuildCountingProvider("counting-bounded-server", out var counts);
        await using var _ = provider;

        var owner = await CreateCharacterAsync(provider, "Bounded Query Character");
        var itemId = await CreateCatalogItemAsync(provider);
        var serverId = await ServerIdAsync(provider);

        async Task<int> QueryCountForAsync(int entryCount)
        {
            var crateId = await GrantAndAckAsync(provider, itemId, owner);
            var upserts = new List<SnapshotUpsertRequest>();
            for (var i = 0; i < entryCount; i++)
            {
                upserts.Add(ContainerUpsert(await GrantAndAckAsync(provider, itemId, owner), itemId, crateId));
            }

            counts.Reset();
            var applied = await ApplyAsync(provider, CharacterScopedCommand(serverId, owner, upserts));
            Assert.Empty(applied.Rejected);
            Assert.Equal(entryCount, applied.AppliedCount);

            return counts.LoadManyCalls + counts.FindChildrenOfManyCalls;
        }

        var small = await QueryCountForAsync(2);
        var large = await QueryCountForAsync(20);

        Assert.Equal(small, large);

        // And the absolute bound: one primary load, plus at most MaxContainerDepth on each walk.
        Assert.InRange(large, 1, 1 + (2 * ItemInstance.MaxContainerDepth));
    }

    /// <summary>
    /// The safety net for the hand-maintained field list on <c>IItemInstanceRepository.WriteAppliedSnapshot</c>.
    /// A patch writes only what it names, so the failure mode a whole-document write cannot have is a
    /// field silently keeping its old value — and because this test is what everyone will trust to
    /// catch that, a field it fails to discriminate on is worse than one it never claimed to cover.
    ///
    /// Every field in the patch is therefore observed <b>changing</b>, and every field that can
    /// legitimately go both ways is observed in both directions. Three batches, because two are not
    /// enough: batch 1 sets everything on a still-pending row, batch 2 drops every optional value and
    /// moves it to the world, batch 3 brings it back onto the character. Dropping any single
    /// <c>.Set</c> from the patch list turns at least one assertion here red.
    ///
    /// The subject is granted-but-not-acked on purpose. An already-acked row is <c>PendingSpawn</c>
    /// false and carries the calling server in <c>RootGameServerId</c> at every observation point, so
    /// those two <c>.Set</c> calls could be deleted with the test still green — which was true of the
    /// first version of this test (fix round 3, item 2).
    ///
    /// Two directions are deliberately absent because the path must never produce them:
    /// <c>PendingSpawn</c> false → true (a snapshot only ever clears it) and <c>RootGameServerId</c>
    /// value → null (an applied row is live and must always have a server root — see
    /// <c>RootResolver</c>).
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_Applying_PersistsEveryFieldTheSnapshotCarriesAndNoOthers()
    {
        var owner = await CreateCharacterAsync(_provider, "Field Coverage Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var crateId = await GrantAndAckAsync(_provider, itemId, owner);

        // Not acked: PendingSpawn true and RootGameServerId null, so both have somewhere to move to.
        var instanceId = await GrantOneAsync(_provider, itemId, owner);

        var granted = await LoadAsync(_provider, instanceId);
        Assert.NotNull(granted);
        Assert.True(granted.PendingSpawn);
        Assert.Null(granted.RootGameServerId);
        Assert.Null(granted.Transform);
        Assert.Null(granted.ExpiresAt);
        var registeredAt = granted.RegisteredAt;
        var lastSeenAtBefore = granted.LastSeenAt;

        // ---- Batch 1: nested in the crate, every optional value set ----
        var attributes = new Dictionary<string, string> { ["serial"] = "SN-4471", ["paint"] = "olive" };
        var first = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [new SnapshotUpsertRequest(instanceId, 3, itemId, ParentKind.Container, null, "sidearm", crateId, null, 0.42f, 17, attributes)]));
        Assert.Empty(first.Rejected);
        Assert.Equal(1, first.AppliedCount);

        var nested = await LoadAsync(_provider, instanceId);
        Assert.NotNull(nested);
        Assert.Equal(3, nested.Revision);
        Assert.Equal(ParentKind.Container, nested.ParentKind);
        Assert.Equal(crateId, nested.ContainerInstanceId);
        Assert.Equal("sidearm", nested.Slot);
        Assert.Null(nested.OwnerCharacterId);
        Assert.Null(nested.Transform);
        Assert.Equal(0.42f, nested.Durability);
        Assert.Equal(17, nested.Ammo);
        Assert.Equal(attributes, nested.Attributes.Values);
        Assert.Equal(owner, nested.RootCharacterId);
        Assert.Null(nested.ExpiresAt);
        Assert.True(nested.LastSeenAt > lastSeenAtBefore, $"Expected LastSeenAt to advance from {lastSeenAtBefore}, got {nested.LastSeenAt}.");
        Assert.True(nested.UpdatedAt >= nested.LastSeenAt);

        // The two the previous version of this test could not see move.
        Assert.False(nested.PendingSpawn);
        Assert.Equal(serverId, nested.RootGameServerId);

        // Immutable and backend-owned fields the patch deliberately never names.
        Assert.Equal(ItemOrigin.ShopPurchase, nested.Origin);
        Assert.NotNull(nested.OriginRef);
        Assert.Equal(registeredAt, nested.RegisteredAt);
        Assert.Equal(itemId, nested.ItemId);
        Assert.False(nested.RemovedByStaff);
        Assert.Equal(0, nested.SpawnFailureCount);

        // ---- Batch 2: on the ground, every optional value dropped ----
        var transform = new WorldTransform(new WorldVector3(11f, 22f, 33f), new WorldVector3(0f, 45f, 0f));
        var second = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [new SnapshotUpsertRequest(instanceId, 4, itemId, ParentKind.World, null, null, null, transform, null, null, new Dictionary<string, string>())]));
        Assert.Empty(second.Rejected);
        Assert.Equal(1, second.AppliedCount);

        var dropped = await LoadAsync(_provider, instanceId);
        Assert.NotNull(dropped);
        Assert.Equal(4, dropped.Revision);
        Assert.Equal(ParentKind.World, dropped.ParentKind);
        Assert.Equal(transform, dropped.Transform);
        Assert.Null(dropped.ContainerInstanceId);
        Assert.Null(dropped.Slot);
        Assert.Null(dropped.OwnerCharacterId);
        Assert.Null(dropped.Durability);
        Assert.Null(dropped.Ammo);
        Assert.Empty(dropped.Attributes.Values);
        Assert.Null(dropped.RootCharacterId);
        Assert.Equal(serverId, dropped.RootGameServerId);
        Assert.NotNull(dropped.ExpiresAt);

        // ---- Batch 3: picked back up, so Transform and ExpiresAt are seen going value -> null ----
        var pickedUpAttributes = new Dictionary<string, string> { ["serial"] = "SN-4471" };
        var third = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [new SnapshotUpsertRequest(instanceId, 5, itemId, ParentKind.Character, owner, "chest", null, null, 0.31f, 4, pickedUpAttributes)]));
        Assert.Empty(third.Rejected);
        Assert.Equal(1, third.AppliedCount);

        var carried = await LoadAsync(_provider, instanceId);
        Assert.NotNull(carried);
        Assert.Equal(5, carried.Revision);
        Assert.Equal(ParentKind.Character, carried.ParentKind);
        Assert.Equal(owner, carried.OwnerCharacterId);
        Assert.Equal("chest", carried.Slot);
        Assert.Equal(0.31f, carried.Durability);
        Assert.Equal(4, carried.Ammo);
        Assert.Equal(pickedUpAttributes, carried.Attributes.Values);
        Assert.Equal(owner, carried.RootCharacterId);
        Assert.Equal(serverId, carried.RootGameServerId);

        // The pair this batch exists for: an item on a character's person carries no transform and
        // never despawns, so both must come back off a row that had them.
        Assert.Null(carried.Transform);
        Assert.Null(carried.ExpiresAt);
    }

    /// <summary>
    /// Task 2 fix round 3, item 1 — the theft hole one level up. A still-pending container carries a
    /// null <c>RootGameServerId</c>, so <c>IsOnAnotherServer</c> is as vacuous on the <i>parent</i> as
    /// it was on the row itself, and this is the container-parent counterpart of the rule the scope
    /// check already applies. Nobody has taken delivery of that crate, so it is not somewhere anything
    /// can be put.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_ParentedIntoAContainerNobodyHasTakenDeliveryOf_IsRejectedAsNotOnThisServer()
    {
        var owner = await CreateCharacterAsync(_provider, "Pending Parent Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var pendingCrateId = await GrantOneAsync(_provider, itemId, owner);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(instanceId, itemId, pendingCrateId)]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(instanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.NotOnThisServer, rejection.Reason);
        Assert.Equal(0, applied.AppliedCount);

        var stored = await LoadAsync(_provider, instanceId);
        Assert.NotNull(stored);
        Assert.Equal(ParentKind.Character, stored.ParentKind);
    }

    /// <summary>
    /// ...but not when the same batch is adopting that container too, which is the honest path: a mod
    /// that spawns a granted crate and reports what is inside it in one snapshot. Run both ways round,
    /// because the guard reads the batch's intended set rather than what has applied so far — checking
    /// the running state instead would make the answer depend on which entry came first in an array
    /// the wire contract gives no ordering to.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplySnapshot_AdoptingAPendingContainerAndItsContentsInOneBatch_AppliesBothWhicheverOrderTheyArriveIn(bool containerFirst)
    {
        var owner = await CreateCharacterAsync(_provider, $"Adopt Together Character {containerFirst}");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var crateId = await GrantOneAsync(_provider, itemId, owner);
        var contentId = await GrantAndAckAsync(_provider, itemId, owner);

        var crateEntry = CharacterUpsert(crateId, itemId, owner);
        var contentEntry = ContainerUpsert(contentId, itemId, crateId);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            containerFirst ? [crateEntry, contentEntry] : [contentEntry, crateEntry]));

        Assert.Empty(applied.Rejected);
        Assert.Equal(2, applied.AppliedCount);

        var crate = await LoadAsync(_provider, crateId);
        Assert.NotNull(crate);
        Assert.False(crate.PendingSpawn);
        Assert.Equal(serverId, crate.RootGameServerId);

        var content = await LoadAsync(_provider, contentId);
        Assert.NotNull(content);
        Assert.Equal(crateId, content.ContainerInstanceId);
        Assert.Equal(serverId, content.RootGameServerId);
        Assert.Equal(owner, content.RootCharacterId);
    }

    /// <summary>
    /// The second lock on the same door (fix round 3, item 1, the resolver half). Applying an upsert
    /// clears <c>PendingSpawn</c>, so the row goes live — and a live row with a null
    /// <c>RootGameServerId</c> satisfies <i>neither</i> server guard: <c>IsOnAnotherServer</c> is
    /// vacuous on null and the pending-scope check no longer applies once the flag is gone. Any
    /// gameserver in the hive could then world-parent it onto its own ground, which is the finding this
    /// task already closed, reconstituted in two steps.
    ///
    /// The parent guard blocks the one-hop form. This covers the form it cannot see: an ancestor
    /// <i>further up</i> than the batch names, whose own root is null. That state is no longer
    /// reachable through this path — which is the point of a defence-in-depth check — so the setup
    /// forces it directly, the same escape hatch the tombstone tests use for a flag no domain method
    /// writes yet.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_NestedBeneathAnAncestorWithNoServerRoot_StillGivesTheAppliedRowAServerRoot()
    {
        var owner = await CreateCharacterAsync(_provider, "Rootless Ancestor Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var bagId = await GrantAndAckAsync(_provider, itemId, owner);
        var trinketId = await GrantAndAckAsync(_provider, itemId, owner);

        // Force the bag to look like a delivered row that never got a server root — not pending, so the
        // parent guard passes it, and null-rooted, so IsOnAnotherServer passes it too.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Patch<ItemInstance>(bagId.Value).Set(x => x.RootGameServerId, (GameServerId?)null);
            await session.SaveChangesAsync();
        }

        var bagBefore = await LoadAsync(_provider, bagId);
        Assert.NotNull(bagBefore);
        Assert.False(bagBefore.PendingSpawn);
        Assert.Null(bagBefore.RootGameServerId);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(trinketId, itemId, bagId)]));

        Assert.Empty(applied.Rejected);
        Assert.Equal(1, applied.AppliedCount);

        var trinket = await LoadAsync(_provider, trinketId);
        Assert.NotNull(trinket);
        Assert.False(trinket.PendingSpawn);

        // The assertion the whole guard exists for: live, therefore rooted somewhere real.
        Assert.Equal(serverId, trinket.RootGameServerId);
    }

    /// <summary>
    /// Task 2 fix round 4, item 1. The container-parent guard has to ask whether a container's own
    /// entry <b>applied</b>, not whether the batch <i>meant</i> to upsert it. Reading intent lets a
    /// deliberately doomed entry unlock the guard for its sibling:
    ///
    /// <code>
    /// upserts = [ world-parent the victim's undelivered crate,   // rejected: not the scope character's
    ///             nest my own item into that same crate ]        // unlocked by the entry above
    /// </code>
    ///
    /// One <c>Character</c>-scoped batch on the attacker's own, genuinely on-server character. The
    /// first entry never applies, so the crate stays pending; if the second is let through,
    /// <c>RootResolver</c> hands the attacker's item the crate's <c>RootCharacterId</c> through
    /// <c>Current()</c> and it surfaces in the <b>victim's</b> carried inventory. An unauthorised
    /// cross-account ownership transfer.
    ///
    /// Both orders, because the fix must not reintroduce a dependency on where the entries sit.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplySnapshot_NestingIntoAPendingCrateUnlockedByARejectedEntry_RejectsBothWhicheverOrderTheyArriveIn(bool decoyFirst)
    {
        var attacker = await CreateCharacterAsync(_provider, $"Attacker Character {decoyFirst}");
        var victim = await CreateCharacterAsync(_provider, $"Victim Character {decoyFirst}");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        // The victim's paid, undelivered crate, and the attacker's own ordinary item.
        var victimsCrateId = await GrantOneAsync(_provider, itemId, victim);
        var attackersItemId = await GrantAndAckAsync(_provider, itemId, attacker);

        var decoy = WorldUpsert(victimsCrateId, itemId);
        var theft = ContainerUpsert(attackersItemId, itemId, victimsCrateId);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            attacker,
            decoyFirst ? [decoy, theft] : [theft, decoy]));

        Assert.Equal(2, applied.Rejected.Count);
        Assert.All(applied.Rejected, x => Assert.Equal(SnapshotRejectionReason.NotOnThisServer, x.Reason));
        Assert.Equal(0, applied.AppliedCount);

        // The attacker's item never moved.
        var attackersItem = await LoadAsync(_provider, attackersItemId);
        Assert.NotNull(attackersItem);
        Assert.Equal(ParentKind.Character, attackersItem.ParentKind);
        Assert.Equal(attacker, attackersItem.RootCharacterId);

        // The victim's crate is untouched and still owed to them.
        var crate = await LoadAsync(_provider, victimsCrateId);
        Assert.NotNull(crate);
        Assert.True(crate.PendingSpawn);
        Assert.Equal(victim, crate.RootCharacterId);

        // And the assertion the whole finding is about.
        await using var scope = _provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IItemInstanceRepository>();
        var victimsInventory = await repository.FindCarriedByRootCharacterAsync(victim, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.DoesNotContain(victimsInventory, x => x.Id == attackersItemId);
    }

    /// <summary>An upsert nesting into a container the backend never issued has no chain to resolve roots through, so it is rejected rather than written with dangling parentage.</summary>
    [Fact]
    public async Task ApplySnapshot_ParentedIntoAContainerTheBackendNeverIssued_IsUnknownInstance()
    {
        var owner = await CreateCharacterAsync(_provider, "Dangling Container Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(instanceId, itemId, new ItemInstanceId(Guid.NewGuid()))]));

        var rejection = Assert.Single(applied.Rejected);
        Assert.Equal(instanceId, rejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.UnknownInstance, rejection.Reason);

        var stored = await LoadAsync(_provider, instanceId);
        Assert.NotNull(stored);
        Assert.Equal(ParentKind.Character, stored.ParentKind);
    }

    /// <summary>
    /// Task 3's central idempotency guarantee. Without replay protection, resending the identical
    /// batch would be an ordinary no-op under revision LWW — same revision, identical content, so it
    /// would come back <c>appliedCount: 0</c>, <c>skippedNoOp: 1</c> — a <b>different</b> body than
    /// the first response. Replay protection must instead return the exact original counts and the
    /// exact original <c>rejected</c> array, with only <c>replayOfPriorBatch</c> flipped true, per the
    /// design brief's "a replay that returns different content is worse than no idempotency."
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_ReplayingTheSameBatchId_ReturnsTheIdenticalBodyAndAppliesNothingTwice()
    {
        var owner = await CreateCharacterAsync(_provider, "Replay Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);
        var inventedId = new ItemInstanceId(Guid.NewGuid());

        var batchId = Guid.NewGuid();
        var command = CharacterScopedCommand(
            serverId,
            batchId,
            owner,
            [CharacterUpsert(instanceId, itemId, owner, revision: 4), CharacterUpsert(inventedId, itemId, owner)]);

        var first = await ApplyAsync(_provider, command);
        Assert.False(first.ReplayOfPriorBatch);
        Assert.Equal(1, first.AppliedCount);
        Assert.Equal(0, first.SkippedNoOp);
        var firstRejection = Assert.Single(first.Rejected);
        Assert.Equal(inventedId, firstRejection.InstanceId);
        Assert.Equal(SnapshotRejectionReason.UnknownInstance, firstRejection.Reason);

        var replay = await ApplyAsync(_provider, command);

        Assert.True(replay.ReplayOfPriorBatch);
        Assert.Equal(first.BatchId, replay.BatchId);
        Assert.Equal(first.Sequence, replay.Sequence);
        Assert.Equal(first.AppliedCount, replay.AppliedCount);
        Assert.Equal(first.SkippedNoOp, replay.SkippedNoOp);
        Assert.Equal(first.Deleted, replay.Deleted);
        Assert.Equal(first.CascadeDeleted, replay.CascadeDeleted);
        Assert.Equal(first.Rejected, replay.Rejected);

        // And genuinely applied nothing twice: the stored row is exactly where the first application
        // left it, and the invented id still never came into existence.
        var stored = await LoadAsync(_provider, instanceId);
        Assert.NotNull(stored);
        Assert.Equal(4, stored.Revision);
        Assert.Null(await LoadIncludingDeletedAsync(_provider, inventedId));
    }

    /// <summary>
    /// The <c>ScopeCursor</c> gate. A second <c>Full</c> batch for the same scope naming a sequence
    /// that is not strictly greater than the one already applied is rejected whole, and the rejection
    /// carries <c>lastAppliedSequence</c> so the Bridge can tell how far behind it fell — a genuinely
    /// lower sequence and an exactly-equal one are both stale, since a monotonic gate has to reject
    /// both to mean anything.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithAStaleSequence_IsRejectedWithLastAppliedSequence()
    {
        var owner = await CreateCharacterAsync(_provider, "Stale Sequence Character");
        var serverId = await ServerIdAsync(_provider);

        var first = await ApplyAsync(_provider, FullScopedCommand(serverId, owner, sequence: 5));
        Assert.Equal(5, first.Sequence);

        var equal = await DispatchAsync(_provider, FullScopedCommand(serverId, owner, sequence: 5));
        if (equal is not ApplySnapshotResult.StaleSequence equalStale)
        {
            throw new InvalidOperationException($"Expected StaleSequence, got {equal}");
        }

        Assert.Equal(5, equalStale.LastAppliedSequence);

        var lower = await DispatchAsync(_provider, FullScopedCommand(serverId, owner, sequence: 3));
        if (lower is not ApplySnapshotResult.StaleSequence lowerStale)
        {
            throw new InvalidOperationException($"Expected StaleSequence, got {lower}");
        }

        Assert.Equal(5, lowerStale.LastAppliedSequence);
    }

    /// <summary>
    /// A <c>Partial</c> batch carries no <c>sequence</c> at all and is ordered by each instance's own
    /// <c>revision</c> instead — it must never be blocked by a <c>ScopeCursor</c> some earlier
    /// <c>Full</c> batch on the very same scope advanced, however far ahead that cursor sits.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_PartialMode_NeedsNoSequenceAndIsNotBlockedByAFullModeCursor()
    {
        var owner = await CreateCharacterAsync(_provider, "Partial Ignores Cursor Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        // Advance this scope's Full-mode cursor well ahead of anything relevant to a Partial batch,
        // which carries no sequence to compare against it in the first place. The Full batch reports
        // the instance verbatim (task 4): a Full batch's silence about a row now soft-deletes it, so an
        // empty payload here would sweep away the very row the Partial batch below is about — this
        // test is about the cursor, not the sweep, and the same-revision, same-content upsert keeps the
        // row exactly as it was while still saying "this is everything in this scope".
        var full = await ApplyAsync(_provider, FullScopedCommand(
            serverId, owner, sequence: 100, upserts: [CharacterUpsert(instanceId, itemId, owner, revision: 0)]));
        Assert.Equal(100, full.Sequence);
        Assert.Equal(0, full.Swept);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: 1)]));

        Assert.Empty(applied.Rejected);
        Assert.Equal(1, applied.AppliedCount);
        Assert.Null(applied.Sequence);
    }

    /// <summary>
    /// The reasoning <see cref="Domain.Snapshots.ScopeCursor"/>'s own doc comment records: a per-scope
    /// cursor means one scope's ordering problem never touches another's. Character B's cursor has
    /// never been touched, so its own first <c>Full</c> reconcile at a low sequence is not stale —
    /// whatever Character A's unrelated cursor already advanced to. A single per-server counter would
    /// have made this fail.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullMode_TwoDifferentScopesDoNotBlockEachOther()
    {
        var characterA = await CreateCharacterAsync(_provider, "Independent Scope Character A");
        var characterB = await CreateCharacterAsync(_provider, "Independent Scope Character B");
        var serverId = await ServerIdAsync(_provider);

        var appliedA = await ApplyAsync(_provider, FullScopedCommand(serverId, characterA, sequence: 50));
        Assert.Equal(50, appliedA.Sequence);

        var appliedB = await ApplyAsync(_provider, FullScopedCommand(serverId, characterB, sequence: 1));
        Assert.Equal(1, appliedB.Sequence);
    }

    // ---- Fix round 1 ----

    /// <summary>
    /// Fix round 1, item 1. Unlike <c>revision</c> (no upper bound), a poisoned <c>sequence</c> can
    /// never self-heal — a monotonic gate cannot be rewound — so one batch naming a value above
    /// <see cref="ScopeCursor.MaxSequence"/> would otherwise pin the scope's cursor at the ceiling
    /// forever, permanently denying every future <c>Full</c> reconcile of it. Checked purely in memory,
    /// before any Postgres touch.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithASequenceAboveTheSanityCeiling_IsRejectedAsSequenceOutOfRange()
    {
        var owner = await CreateCharacterAsync(_provider, "Sequence Ceiling Character");
        var serverId = await ServerIdAsync(_provider);

        var result = await DispatchAsync(_provider, FullScopedCommand(serverId, owner, sequence: ScopeCursor.MaxSequence + 1));

        if (result is not ApplySnapshotResult.SequenceOutOfRange outOfRange)
        {
            throw new InvalidOperationException($"Expected SequenceOutOfRange, got {result}");
        }

        Assert.Equal(ScopeCursor.MaxSequence + 1, outOfRange.Requested);
        Assert.Equal(ScopeCursor.MaxSequence, outOfRange.Max);

        // The ceiling itself is still a legal sequence — only strictly-above is rejected.
        var atCeiling = await ApplyAsync(_provider, FullScopedCommand(serverId, owner, sequence: ScopeCursor.MaxSequence));
        Assert.Equal(ScopeCursor.MaxSequence, atCeiling.Sequence);
    }

    /// <summary>
    /// Fix round 1, item 2. The <c>Container</c>-scope half of the sequence gate has a different
    /// pipeline position (inside <c>ApplyAsync</c>, after the primary load and the container
    /// <c>WrongServer</c> check) and a different <c>ScopeCursor.BuildKey</c> arm than the
    /// <c>Character</c> half every other <c>Full</c>-mode test in this file exercises — this is its
    /// only coverage.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeContainerScope_AdvancesTheCursorThenRejectsAStaleSuccessor()
    {
        var owner = await CreateCharacterAsync(_provider, "Container Full Scope Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);

        var first = await ApplyAsync(_provider, FullContainerScopedCommand(serverId, crateId, sequence: 1));
        Assert.Equal(1, first.Sequence);

        var stale = await DispatchAsync(_provider, FullContainerScopedCommand(serverId, crateId, sequence: 1));
        if (stale is not ApplySnapshotResult.StaleSequence staleSequence)
        {
            throw new InvalidOperationException($"Expected StaleSequence, got {stale}");
        }

        Assert.Equal(1, staleSequence.LastAppliedSequence);

        // And a genuinely higher sequence still advances it.
        var second = await ApplyAsync(_provider, FullContainerScopedCommand(serverId, crateId, sequence: 2));
        Assert.Equal(2, second.Sequence);
    }

    /// <summary>
    /// Fix round 1, item 2's ordering property, made explicit: the Container-scope sequence gate must
    /// run only <b>after</b> the container's own reachability is proven, exactly like the Character
    /// half. A different gameserver naming the same container and the same (already-stale) sequence
    /// must never see <c>StaleSequence</c> — that would leak <c>LastAppliedSequence</c> for a scope
    /// this caller was never shown to be allowed to ask about — it must see <c>WrongServer</c> instead,
    /// exactly as if no cursor existed for it to compare against at all.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeContainerScopeOnAnotherServer_ReturnsWrongServerNeverStaleSequence()
    {
        var homeProvider = TestServices.BuildProvider("home-full-container-order-server");
        var awayProvider = TestServices.BuildProvider("away-full-container-order-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Full Container Order Character");
        var itemId = await CreateCatalogItemAsync(awayProvider);
        var awayCrateId = await GrantAndAckAsync(awayProvider, itemId, awayCharacter);

        var awayServerId = await ServerIdAsync(awayProvider);
        var established = await ApplyAsync(awayProvider, FullContainerScopedCommand(awayServerId, awayCrateId, sequence: 1));
        Assert.Equal(1, established.Sequence);

        var homeServerId = await ServerIdAsync(homeProvider);
        var result = await DispatchAsync(homeProvider, FullContainerScopedCommand(homeServerId, awayCrateId, sequence: 1));

        Assert.True(result is ApplySnapshotResult.WrongServer, $"Expected WrongServer, got {result}");
    }

    /// <summary>
    /// Fix round 1, item 3. A <c>batchId</c> match recorded under a <i>different</i> calling gameserver
    /// must be treated as a miss, not a hit — otherwise any server that merely learns another server's
    /// <c>batchId</c> could read back that batch's entire stored body (every <c>instanceId</c> and
    /// rejection reason) for a scope it was never shown to be allowed to ask about.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_ReplayingABatchIdUnderADifferentGameServerId_IsNeverTreatedAsAReplay()
    {
        var owner = await CreateCharacterAsync(_provider, "Replay Scope Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var otherServerId = new GameServerId(Guid.NewGuid());
        var instanceId = await GrantAndAckAsync(_provider, itemId, owner);

        var batchId = Guid.NewGuid();
        var original = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, batchId, owner, [CharacterUpsert(instanceId, itemId, owner, revision: 9)]));
        Assert.False(original.ReplayOfPriorBatch);
        Assert.Equal(1, original.AppliedCount);

        // Same batchId, same content — but claiming to come from a gameserver that never actually
        // applied it.
        var repeated = await DispatchAsync(_provider, new ApplySnapshotCommand(
            otherServerId, batchId, SnapshotScopeKind.Character, owner, null, null, SnapshotMode.Partial,
            [CharacterUpsert(instanceId, itemId, owner, revision: 9)], []));

        // Whatever else this batch resolves to (here: WrongServer, since `owner` was never on
        // `otherServerId`), the one outcome that must never happen is a replay of the real batch's
        // stored body under a caller who was never shown to be entitled to it.
        Assert.True(repeated is not ApplySnapshotResult.Applied { ReplayOfPriorBatch: true });
    }

    /// <summary>
    /// Fix round 1, item 4. <c>ApplySnapshotCommand.Sequence</c> is only guaranteed non-null by the
    /// endpoint's own "sequence is required when mode is Full" validation — a direct, non-HTTP caller
    /// (exactly what every test in this file already is) can construct a <c>Full</c> command with a
    /// null <c>Sequence</c> and reach the handler regardless. It must fail with a deliberate, labelled
    /// <see cref="InvalidOperationException"/> rather than an unlabelled <c>Nullable.Value</c> crash.
    /// Targets a virgin scope on purpose: the gate's own <c>cursor is null</c> short-circuit means the
    /// gate itself never dereferences <c>Sequence</c> for a scope's first-ever batch, so only the
    /// unconditional read on the <c>ScopeCursor.AdvanceAsync</c> path at the very end would have
    /// crashed without the fix.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeCommandConstructedDirectlyWithoutASequence_ThrowsADeliberateException()
    {
        var owner = await CreateCharacterAsync(_provider, "Missing Sequence Character");
        var serverId = await ServerIdAsync(_provider);

        var command = new ApplySnapshotCommand(
            serverId, Guid.NewGuid(), SnapshotScopeKind.Character, owner, null, null, SnapshotMode.Full, [], []);

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(command).AsTask());
        Assert.Contains("Sequence", exception.Message);
    }

    /// <summary>
    /// Fix round 1, item 7, end to end through the handler. Two <c>Full</c> batches race the same
    /// (virgin) scope at the same sequence. Asserted loosely on purpose, because which branch fires
    /// depends on real interleaving this test does not control: if the two batches genuinely overlap at
    /// the Postgres level, the loser's <c>SaveChangesAsync</c> throws <c>ScopeCursorConflictException</c>
    /// (mapped here to <c>ConcurrentReconcile</c>, the one retryable outcome on this endpoint); if they
    /// happen not to overlap at all, the second is instead caught by the ordinary, non-concurrency
    /// stale-sequence gate, since both name the same sequence. What must be true regardless of timing:
    /// exactly one <c>Applied</c>, never an unhandled exception, and the loser is one of the two
    /// expected shapes. See <c>ScopeCursorConcurrencyTests</c> for the deterministic, timing-independent
    /// pin of the underlying Marten behaviour this depends on.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_TwoConcurrentFullBatchesRacingTheSameScope_ExactlyOneApplies()
    {
        var owner = await CreateCharacterAsync(_provider, "Concurrent Full Race Character");
        var serverId = await ServerIdAsync(_provider);

        var command1 = FullScopedCommand(serverId, owner, sequence: 1);
        var command2 = FullScopedCommand(serverId, owner, sequence: 1);

        var results = await Task.WhenAll(DispatchAsync(_provider, command1), DispatchAsync(_provider, command2));

        var appliedCount = results.Count(r => r is ApplySnapshotResult.Applied);
        var conflictCount = results.Count(r => r is ApplySnapshotResult.ConcurrentReconcile);
        var staleCount = results.Count(r => r is ApplySnapshotResult.StaleSequence);

        Assert.Equal(1, appliedCount);
        Assert.Equal(1, conflictCount + staleCount);
    }

    // ---- Fix round 2 ----

    /// <summary>
    /// Fix round 2, item 4. Symmetric with the ceiling: a negative <c>sequence</c> would self-heal the
    /// same way a negative <c>revision</c> does (task 2's own reasoning), so this is a
    /// reasoning-cost argument rather than a correctness one — a bounds check that already exists
    /// should not carry a documented, asymmetric hole in it.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithANegativeSequence_IsRejectedAsSequenceOutOfRange()
    {
        var owner = await CreateCharacterAsync(_provider, "Negative Sequence Character");
        var serverId = await ServerIdAsync(_provider);

        var result = await DispatchAsync(_provider, FullScopedCommand(serverId, owner, sequence: -1));

        if (result is not ApplySnapshotResult.SequenceOutOfRange outOfRange)
        {
            throw new InvalidOperationException($"Expected SequenceOutOfRange, got {result}");
        }

        Assert.Equal(-1, outOfRange.Requested);
        Assert.Equal(ScopeCursor.MaxSequence, outOfRange.Max);
    }

    /// <summary>
    /// Fix round 2, item 3 — the write half of the cross-tenant leak fix round 1 only half-closed.
    /// Before the composite <c>AppliedBatch</c> key, two different gameservers legitimately applying
    /// two entirely different, individually valid batches that merely happen to carry the same
    /// client-chosen <c>batchId</c> value would collide on the same stored row: whichever applied
    /// second would silently overwrite the first's idempotency record with its own body, so the first
    /// server's own later replay of its own <c>batchId</c> would miss and re-apply instead of returning
    /// its original response. The composite key (<c>{gameServerId}:{batchId}</c>) makes them different
    /// rows from the start, so this can no longer happen.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_TwoServersApplyingDifferentBatchesUnderTheSameBatchId_DoNotCorruptEachOthersReplay()
    {
        var homeProvider = TestServices.BuildProvider("home-composite-key-server");
        var awayProvider = TestServices.BuildProvider("away-composite-key-server");
        await using var _1 = homeProvider;
        await using var _2 = awayProvider;

        var homeCharacter = await CreateCharacterAsync(homeProvider, "Home Composite Key Character");
        var awayCharacter = await CreateCharacterAsync(awayProvider, "Away Composite Key Character");
        var itemId = await CreateCatalogItemAsync(homeProvider);

        var homeServerId = await ServerIdAsync(homeProvider);
        var awayServerId = await ServerIdAsync(awayProvider);

        var homeInstanceId = await GrantAndAckAsync(homeProvider, itemId, homeCharacter);
        var awayInstanceId = await GrantAndAckAsync(awayProvider, itemId, awayCharacter);

        // The same literal batchId value, deliberately, on two unrelated, individually valid batches
        // from two different gameservers.
        var sharedBatchId = Guid.NewGuid();

        var homeOriginal = await ApplyAsync(homeProvider, CharacterScopedCommand(
            homeServerId, sharedBatchId, homeCharacter, [CharacterUpsert(homeInstanceId, itemId, homeCharacter, revision: 4)]));
        Assert.False(homeOriginal.ReplayOfPriorBatch);
        Assert.Equal(1, homeOriginal.AppliedCount);

        var awayOriginal = await ApplyAsync(awayProvider, CharacterScopedCommand(
            awayServerId, sharedBatchId, awayCharacter, [CharacterUpsert(awayInstanceId, itemId, awayCharacter, revision: 7)]));
        Assert.False(awayOriginal.ReplayOfPriorBatch);
        Assert.Equal(1, awayOriginal.AppliedCount);

        // Each server's own replay of its own batchId must return exactly its own original body —
        // never the other server's, and never a fresh re-application.
        var homeReplay = await ApplyAsync(homeProvider, CharacterScopedCommand(
            homeServerId, sharedBatchId, homeCharacter, [CharacterUpsert(homeInstanceId, itemId, homeCharacter, revision: 4)]));
        Assert.True(homeReplay.ReplayOfPriorBatch);
        Assert.Equal(homeOriginal.AppliedCount, homeReplay.AppliedCount);
        Assert.Equal(homeOriginal.Rejected, homeReplay.Rejected);

        var awayReplay = await ApplyAsync(awayProvider, CharacterScopedCommand(
            awayServerId, sharedBatchId, awayCharacter, [CharacterUpsert(awayInstanceId, itemId, awayCharacter, revision: 7)]));
        Assert.True(awayReplay.ReplayOfPriorBatch);
        Assert.Equal(awayOriginal.AppliedCount, awayReplay.AppliedCount);
        Assert.Equal(awayOriginal.Rejected, awayReplay.Rejected);

        // And the rows themselves stayed exactly where each server's own batch left them — proof
        // neither write ever touched the other's.
        var homeStored = await LoadAsync(homeProvider, homeInstanceId);
        Assert.NotNull(homeStored);
        Assert.Equal(4, homeStored.Revision);

        var awayStored = await LoadAsync(awayProvider, awayInstanceId);
        Assert.NotNull(awayStored);
        Assert.Equal(7, awayStored.Revision);
    }

    // ---- Task 4: the Full-mode sweep, the empty-payload guard, and SuspiciousReconcile ----

    /// <summary>
    /// Grants <paramref name="count"/> instances and acks them all in one batch, leaving that many
    /// settled, live, non-pending rows rooted at <paramref name="owner"/> — the "prior inventory" the
    /// sweep and its guard are measured against.
    /// </summary>
    private static async Task<IReadOnlyList<ItemInstanceId>> GrantAndAckManyAsync(
        ServiceProvider provider, ItemId itemId, CharacterId owner, int count)
    {
        List<ItemInstanceId> instanceIds;

        await using (var scope = provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var granted = await mediator.Send(new GrantItemsCommand(
                itemId, count, owner, ItemOrigin.ShopPurchase, new OriginRef("Shops", Guid.NewGuid().ToString())));

            if (granted is not GrantItemsResult.Granted grantedInstances)
            {
                throw new InvalidOperationException($"Expected Granted, got {granted}");
            }

            instanceIds = grantedInstances.Instances.Select(x => x.InstanceId).ToList();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var serverId = await scope.ServiceProvider.GetRequiredService<ICurrentGameServer>().GetIdAsync(CancellationToken.None);
            var acked = await mediator.Send(new AcknowledgeSpawnsCommand(
                serverId, instanceIds.Select(id => new InstanceAckRequest(id, [])).ToList()));

            if (acked is not AcknowledgeSpawnsResult.Acknowledged)
            {
                throw new InvalidOperationException($"Expected Acknowledged, got {acked}");
            }
        }

        return instanceIds;
    }

    private static async Task<SuspiciousReconcile?> LoadSuspiciousReconcileAsync(ServiceProvider provider, string id)
    {
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISuspiciousReconcileRepository>();
        return await repository.FindAsync(id, CancellationToken.None);
    }

    private static async Task<AppliedBatch?> LoadAppliedBatchAsync(ServiceProvider provider, string key)
    {
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAppliedBatchRepository>();
        return await repository.FindAsync(key, CancellationToken.None);
    }

    /// <summary>
    /// The sweep itself: a <c>Full</c> batch means "this is everything in this scope", so the scope's
    /// live rows the payload never mentioned are soft-deleted. Counted in <c>swept</c> rather than
    /// folded into <c>deleted</c> (which counts only entries the <c>deletes</c> array asked for) or
    /// <c>cascadeDeleted</c> (descendants of those), so all three numbers keep meaning what they say.
    ///
    /// Soft, never hard, and that is the whole recovery story: this design has no leases, so soft
    /// delete plus the retention window is the only undo a bad reconcile has.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithRowsAbsentFromThePayload_SoftDeletesThem()
    {
        var owner = await CreateCharacterAsync(_provider, "Full Sweep Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceIds = await GrantAndAckManyAsync(_provider, itemId, owner, 3);

        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId, owner, sequence: 1, upserts: [CharacterUpsert(instanceIds[0], itemId, owner, revision: 1)]));

        Assert.Empty(applied.Rejected);
        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(2, applied.Swept);
        Assert.Equal(0, applied.Deleted);
        Assert.Equal(0, applied.CascadeDeleted);

        Assert.NotNull(await LoadLiveAsync(_provider, instanceIds[0]));
        Assert.Null(await LoadLiveAsync(_provider, instanceIds[1]));
        Assert.Null(await LoadLiveAsync(_provider, instanceIds[2]));

        // Recoverable, not gone: the row is still there behind the soft-delete filter.
        Assert.NotNull(await LoadIncludingDeletedAsync(_provider, instanceIds[1]));
    }

    /// <summary>
    /// The counterpart, and the reason <c>Full</c> is a separate mode at all: a <c>Partial</c> batch is
    /// a delta and says nothing whatsoever about what it omits, so it must never sweep. Same payload,
    /// same scope, same absent rows — opposite outcome.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_PartialModeWithRowsAbsentFromThePayload_SweepsNothing()
    {
        var owner = await CreateCharacterAsync(_provider, "Partial No Sweep Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceIds = await GrantAndAckManyAsync(_provider, itemId, owner, 3);

        var applied = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(instanceIds[0], itemId, owner, revision: 1)]));

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(0, applied.Swept);

        Assert.NotNull(await LoadLiveAsync(_provider, instanceIds[1]));
        Assert.NotNull(await LoadLiveAsync(_provider, instanceIds[2]));
    }

    /// <summary>
    /// One of the design's four named correctness mechanisms, and the one this task most has to get
    /// right. A <c>PendingSpawn</c> row's entity does not exist in the game yet — the backend minted
    /// it, nothing has spawned it, the mod has never seen it — so its absence from a snapshot carries
    /// no information at all. Sweeping it would destroy a paid, undelivered item on the strength of
    /// evidence that was never offered.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeSweep_NeverDeletesAPendingSpawnRow()
    {
        var owner = await CreateCharacterAsync(_provider, "Pending Survives Sweep Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var settledId = await GrantAndAckAsync(_provider, itemId, owner);
        var pendingId = await GrantOneAsync(_provider, itemId, owner);

        // An empty payload: the mod reports this character as holding nothing at all.
        var applied = await ApplyAsync(_provider, FullScopedCommand(serverId, owner, sequence: 1));

        // Exactly one row swept — the settled one. The pending row was never a candidate.
        Assert.Equal(1, applied.Swept);
        Assert.Null(await LoadLiveAsync(_provider, settledId));

        var stillPending = await LoadLiveAsync(_provider, pendingId);
        Assert.NotNull(stillPending);
        Assert.True(stillPending.PendingSpawn);
        Assert.False(stillPending.RemovedByStaff);
    }

    /// <summary>
    /// The other row a sweep must leave alone. A staff tombstone is deliberately still a live row —
    /// that is what makes it sticky, since a later upsert of that id has to find it and be rejected
    /// <c>RemovedByStaff</c> rather than resurrect anything. Soft-deleting it would make every read
    /// return nothing and turn the very next upsert into <c>UnknownInstance</c>: the sticky tombstone
    /// quietly undone by a sweep that meant no harm.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeSweep_NeverDeletesAStaffRemovedTombstone()
    {
        var owner = await CreateCharacterAsync(_provider, "Tombstone Survives Sweep Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var tombstonedId = await GrantAndAckAsync(_provider, itemId, owner);

        // Same direct patch every other tombstone test in this file uses — no domain method sets this
        // before the staff tooling lands.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Patch<ItemInstance>(tombstonedId.Value).Set(x => x.RemovedByStaff, true);
            await session.SaveChangesAsync();
        }

        var applied = await ApplyAsync(_provider, FullScopedCommand(serverId, owner, sequence: 1));

        Assert.Equal(0, applied.Swept);

        var stored = await LoadLiveAsync(_provider, tombstonedId);
        Assert.NotNull(stored);
        Assert.True(stored.RemovedByStaff);

        // ...and the tombstone is still sticky afterwards: an upsert finds it and is rejected, rather
        // than coming back UnknownInstance against a row a sweep had quietly removed.
        var afterwards = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [CharacterUpsert(tombstonedId, itemId, owner, revision: 9)]));
        Assert.Equal(SnapshotRejectionReason.RemovedByStaff, Assert.Single(afterwards.Rejected).Reason);
    }

    /// <summary>
    /// The third exclusion, and the reason the sweep deliberately does <i>not</i> run through the
    /// cascade an explicit delete uses. A mod that reports the magazine but forgets the rifle it sits
    /// in must not lose the rifle: the magazine would be left parented to a row that no longer exists
    /// while still answering the carried-inventory read, since <c>RootCharacterId</c> resolves through
    /// the chain. Rather than reaching further, the sweep stops short.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeSweep_NeverDeletesAContainerAReportedRowIsNestedIn()
    {
        var owner = await CreateCharacterAsync(_provider, "Nested Survivor Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var nestedId = await GrantAndAckAsync(_provider, itemId, owner);

        // Put the nested row inside the crate first, so both are ordinary stored state before the Full
        // batch runs.
        var moved = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(nestedId, itemId, crateId, revision: 1)]));
        Assert.Equal(1, moved.AppliedCount);

        // The Full batch reports the nested row and never mentions the crate it is in.
        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId, owner, sequence: 1, upserts: [ContainerUpsert(nestedId, itemId, crateId, revision: 2)]));

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(0, applied.Swept);

        Assert.NotNull(await LoadLiveAsync(_provider, crateId));
        Assert.NotNull(await LoadLiveAsync(_provider, nestedId));
    }

    /// <summary>
    /// The <c>Container</c>-scope half of the sweep — a different scope enumeration entirely (a bounded
    /// downward walk, since a container's contents have no denormalised root the way a character's do),
    /// so it needs its own coverage. The scope container itself is never a candidate: a crate cannot
    /// report itself out of existence.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeContainerScope_SweepsItsUnreportedContentsButNeverItself()
    {
        var owner = await CreateCharacterAsync(_provider, "Container Sweep Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var keptId = await GrantAndAckAsync(_provider, itemId, owner);
        var droppedId = await GrantAndAckAsync(_provider, itemId, owner);

        var moved = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [ContainerUpsert(keptId, itemId, crateId, revision: 1), ContainerUpsert(droppedId, itemId, crateId, revision: 1)]));
        Assert.Equal(2, moved.AppliedCount);

        var applied = await ApplyAsync(_provider, FullContainerScopedCommand(
            serverId, crateId, sequence: 1, upserts: [ContainerUpsert(keptId, itemId, crateId, revision: 2)]));

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(1, applied.Swept);

        Assert.NotNull(await LoadLiveAsync(_provider, crateId));
        Assert.NotNull(await LoadLiveAsync(_provider, keptId));
        Assert.Null(await LoadLiveAsync(_provider, droppedId));
    }

    /// <summary>
    /// Review round 1's Critical, half one: <b>drop a full backpack</b>. The batch itself says the
    /// backpack moved out of the scope (onto the ground), and its five unreported contents are still
    /// <i>stored</i> as rooted at the character — so a sweep that decides membership from stored roots
    /// deletes every one of them and leaves an empty backpack lying there.
    ///
    /// Silent, ordinary, and entirely invisible to the guard: five rows is nowhere near the row
    /// threshold, so nothing would ever have been recorded for staff either. The fix is post-diff
    /// anchoring — a row whose chain no longer terminates in the scope has <i>left</i>, it has not gone.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeMovingAContainerOutOfTheScope_TakesItsContentsWithItRatherThanDeletingThem()
    {
        var owner = await CreateCharacterAsync(_provider, "Dropped Backpack Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var backpackId = await GrantAndAckAsync(_provider, itemId, owner);
        var contentIds = await GrantAndAckManyAsync(_provider, itemId, owner, 5);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, contentIds.Select(id => ContainerUpsert(id, itemId, backpackId, revision: 1)).ToList()));
        Assert.Equal(5, filled.AppliedCount);

        // One upsert: the backpack is now on the ground. The contents are not re-reported, and the mod
        // is under no obligation to re-report the inside of a container it merely moved.
        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId, owner, sequence: 1, upserts: [WorldUpsert(backpackId, itemId, revision: 2)]));

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(0, applied.Swept);

        foreach (var contentId in contentIds)
        {
            var stored = await LoadLiveAsync(_provider, contentId);
            Assert.NotNull(stored);
            Assert.Equal(backpackId, stored.ContainerInstanceId);
        }
    }

    /// <summary>
    /// Review round 1's Critical, half two: <b>hand a full crate to another character</b>. Same shape as
    /// the dropped backpack, but the chain now terminates on a <i>different character</i> rather than on
    /// the world — a separate arm of the anchoring check, and the one that proves the rule is "does the
    /// chain still end in <i>this</i> scope" rather than merely "is it still container-parented".
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeHandingAContainerToAnotherCharacter_KeepsItsContents()
    {
        var giver = await CreateCharacterAsync(_provider, "Crate Giver Character");
        var receiver = await CreateCharacterAsync(_provider, "Crate Receiver Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, giver);
        var contentIds = await GrantAndAckManyAsync(_provider, itemId, giver, 3);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, giver, contentIds.Select(id => ContainerUpsert(id, itemId, crateId, revision: 1)).ToList()));
        Assert.Equal(3, filled.AppliedCount);

        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId, giver, sequence: 1, upserts: [CharacterUpsert(crateId, itemId, receiver, revision: 2)]));

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(0, applied.Swept);

        foreach (var contentId in contentIds)
        {
            Assert.NotNull(await LoadLiveAsync(_provider, contentId));
        }

        // ...and they followed the crate rather than merely surviving: the denormalised root is what the
        // hot carried-inventory read answers from, so a contents row still pointing at the giver would
        // be the same bug wearing a different symptom.
        var reanchored = await LoadLiveAsync(_provider, contentIds[0]);
        Assert.NotNull(reanchored);
        Assert.Equal(receiver, reanchored.RootCharacterId);
    }

    /// <summary>
    /// The downward protection rule, which post-diff anchoring alone cannot supply: a crate that stays
    /// exactly where it is still anchors in scope, so its contents are still candidates on anchoring
    /// alone. The rule is a claim-of-knowledge one — the payload never mentions this crate at all, so it
    /// is claiming nothing whatsoever about the inside, and silence about a container you never looked
    /// in is not evidence of absence.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeSweep_LeavesTheContentsOfAContainerThePayloadNeverMentions()
    {
        var owner = await CreateCharacterAsync(_provider, "Unmentioned Crate Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var contentIds = await GrantAndAckManyAsync(_provider, itemId, owner, 3);
        var looseId = await GrantAndAckAsync(_provider, itemId, owner);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, contentIds.Select(id => ContainerUpsert(id, itemId, crateId, revision: 1)).ToList()));
        Assert.Equal(3, filled.AppliedCount);

        // The payload speaks only about the loose item. It never names the crate, so it says nothing
        // about what is inside it — but it does say the character holds nothing else loose.
        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId, owner, sequence: 1, upserts: [CharacterUpsert(looseId, itemId, owner, revision: 2)]));

        // The crate itself survives on the existing upward rule (its contents are survivors), and its
        // contents survive on this new downward one.
        Assert.Equal(0, applied.Swept);
        Assert.NotNull(await LoadLiveAsync(_provider, crateId));

        foreach (var contentId in contentIds)
        {
            Assert.NotNull(await LoadLiveAsync(_provider, contentId));
        }
    }

    /// <summary>
    /// The calibration half of the rule above, and the reason it is narrow rather than a blanket
    /// "never sweep container contents": once the payload <i>does</i> mention the crate, it is claiming
    /// to know what is in it, so contents it then omits are genuinely gone and stay sweepable. This is
    /// the contract the Bridge is held to — a <c>Full</c> must enumerate the contents of any container
    /// it mentions.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeSweep_StillSweepsUnreportedContentsOfAContainerThePayloadMentions()
    {
        var owner = await CreateCharacterAsync(_provider, "Mentioned Crate Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var keptId = await GrantAndAckAsync(_provider, itemId, owner);
        var consumedId = await GrantAndAckAsync(_provider, itemId, owner);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId,
            owner,
            [ContainerUpsert(keptId, itemId, crateId, revision: 1), ContainerUpsert(consumedId, itemId, crateId, revision: 1)]));
        Assert.Equal(2, filled.AppliedCount);

        // The crate is named this time, and it is still where it was — so the payload IS enumerating its
        // contents, and the one it leaves out really is gone.
        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId,
            owner,
            sequence: 1,
            upserts:
            [
                CharacterUpsert(crateId, itemId, owner, revision: 2),
                ContainerUpsert(keptId, itemId, crateId, revision: 2),
            ]));

        Assert.Equal(1, applied.Swept);
        Assert.NotNull(await LoadLiveAsync(_provider, crateId));
        Assert.NotNull(await LoadLiveAsync(_provider, keptId));
        Assert.Null(await LoadLiveAsync(_provider, consumedId));
    }

    /// <summary>
    /// Review round 1: an explicitly <i>deleted</i> row must not rescue its own unmentioned parent from
    /// the sweep. The upward protection rule treats anything outside the sweep set as a survivor, and
    /// the payload's named ids include its <c>deletes</c> — so a crate holding nothing but a row the
    /// batch just declared gone was being protected from beyond that row's grave.
    ///
    /// Sharper than the ordinary unmentioned-container case, because the payload <i>did</i> speak about
    /// that row and what it said was "this is gone".
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeSweep_IsNotProtectedByARowTheSameBatchDeleted()
    {
        var owner = await CreateCharacterAsync(_provider, "Deleted Child Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var childId = await GrantAndAckAsync(_provider, itemId, owner);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, [ContainerUpsert(childId, itemId, crateId, revision: 1)]));
        Assert.Equal(1, filled.AppliedCount);

        // The batch deletes the child and never mentions the crate. The crate is therefore unmentioned
        // and unprotected: nothing survives inside it.
        var applied = await ApplyAsync(_provider, new ApplySnapshotCommand(
            serverId,
            Guid.NewGuid(),
            SnapshotScopeKind.Character,
            owner,
            null,
            1,
            SnapshotMode.Full,
            [],
            [new SnapshotDeleteRequest(childId, 1, DeleteReason.Consumed)]));

        Assert.Equal(1, applied.Deleted);
        Assert.Equal(1, applied.Swept);
        Assert.Null(await LoadLiveAsync(_provider, childId));
        Assert.Null(await LoadLiveAsync(_provider, crateId));
    }

    /// <summary>
    /// The scenario the guard exists to make survivable, and the reason it is not merely a nicety: a
    /// gameserver that booted with a failed mod load, or one caught mid-split, will happily report an
    /// empty world for a scope it cannot actually see. Believing it costs the player their entire
    /// inventory in one commit, and soft delete plus the retention window is this leaseless design's
    /// only undo — an undo nobody is told to perform is not one, hence the <c>SuspiciousReconcile</c>
    /// record.
    ///
    /// Also pins the two properties that make the refusal recoverable rather than merely loud: not one
    /// row is touched, and the scope's <c>ScopeCursor</c> is <b>not</b> advanced, so the corrected
    /// reconcile is still accepted at the very same sequence once a human has looked.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithAnEmptyPayloadAgainstALargeInventory_IsRefusedAndRecorded()
    {
        var owner = await CreateCharacterAsync(_provider, "Suspicious Reconcile Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        // Comfortably past the default SuspiciousReconcileScopeRowsThreshold of 25.
        var instanceIds = await GrantAndAckManyAsync(_provider, itemId, owner, 30);

        var batchId = Guid.NewGuid();
        var result = await DispatchAsync(_provider, new ApplySnapshotCommand(
            serverId, batchId, SnapshotScopeKind.Character, owner, null, 1, SnapshotMode.Full, [], []));

        if (result is not ApplySnapshotResult.SuspiciousReconcile suspicious)
        {
            throw new InvalidOperationException($"Expected SuspiciousReconcile, got {result}");
        }

        Assert.Equal(30, suspicious.WouldHaveSwept);
        Assert.Equal(30, suspicious.ScopeRowCount);
        Assert.Equal(0, suspicious.Upserts);
        Assert.Equal(25, suspicious.ScopeRowsThreshold);
        Assert.Equal(3, suspicious.UpsertsThreshold);
        Assert.Equal(90, suspicious.SweptPercentThreshold);

        // Storage is exactly as it was — the guard runs before anything is deleted.
        foreach (var instanceId in instanceIds)
        {
            Assert.NotNull(await LoadLiveAsync(_provider, instanceId));
        }

        // ...and the refusal left something behind for a human to find.
        var record = await LoadSuspiciousReconcileAsync(_provider, SuspiciousReconcile.BuildKey(serverId, batchId));
        Assert.NotNull(record);
        Assert.Equal(batchId, record.BatchId);
        Assert.Equal(serverId, record.GameServerId);
        Assert.Equal(SnapshotScopeKind.Character, record.ScopeKind);
        Assert.Equal(owner, record.ScopeCharacterId);
        Assert.Equal(1, record.Sequence);
        Assert.Equal(30, record.WouldHaveSwept);
        Assert.Equal(30, record.ScopeRowCount);
        Assert.Equal(0, record.UpsertCount);
        Assert.Equal(25, record.ScopeRowsThreshold);
        Assert.Equal(3, record.UpsertsThreshold);
        Assert.Equal(90, record.SweptPercentThreshold);

        // The cursor was never advanced, so the corrected reconcile is accepted at the same sequence.
        var corrected = await ApplyAsync(_provider, FullScopedCommand(
            serverId,
            owner,
            sequence: 1,
            upserts: instanceIds.Select(id => CharacterUpsert(id, itemId, owner, revision: 1)).ToList()));

        Assert.Equal(30, corrected.AppliedCount);
        Assert.Equal(0, corrected.Swept);
    }

    /// <summary>
    /// The property the whole refusal rests on, checked against real Postgres rather than reasoned
    /// about: when the guard trips, the batch's own upserts must not have leaked into storage either.
    /// The three diff passes run <i>before</i> the guard (they have to — the sweep's protection rules
    /// read post-diff parentage), and they mutate the loaded <c>ItemInstance</c> objects in place. The
    /// claim is that this is harmless because World's session is a Marten <c>LightweightSession</c>
    /// with no dirty tracking, so nothing reaches Postgres without an explicit <c>Store</c>/<c>Patch</c>
    /// — and the refusal path queues neither before it saves.
    ///
    /// If that claim were wrong, the refusal would be a half-applied batch wearing a 422: some rows
    /// silently advanced, no <c>AppliedBatch</c> record to replay, and a Bridge told nothing happened.
    /// Two upserts, so the payload still sits under the near-empty threshold and the guard still fires.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_WhenTheGuardRefusesABatch_LeavesItsOwnUpsertsUnwrittenToo()
    {
        var owner = await CreateCharacterAsync(_provider, "Refused Upserts Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceIds = await GrantAndAckManyAsync(_provider, itemId, owner, 30);

        var batchId = Guid.NewGuid();
        var result = await DispatchAsync(_provider, new ApplySnapshotCommand(
            serverId,
            batchId,
            SnapshotScopeKind.Character,
            owner,
            null,
            1,
            SnapshotMode.Full,
            [
                CharacterUpsert(instanceIds[0], itemId, owner, revision: 77),
                CharacterUpsert(instanceIds[1], itemId, owner, revision: 88),
            ],
            []));

        if (result is not ApplySnapshotResult.SuspiciousReconcile suspicious)
        {
            throw new InvalidOperationException($"Expected SuspiciousReconcile, got {result}");
        }

        // 30 rows in scope, 2 of them named by the payload, so 28 would have been swept.
        Assert.Equal(28, suspicious.WouldHaveSwept);
        Assert.Equal(2, suspicious.Upserts);

        // The two rows the batch tried to advance are exactly where they were before it ran.
        foreach (var instanceId in instanceIds.Take(2))
        {
            var stored = await LoadLiveAsync(_provider, instanceId);
            Assert.NotNull(stored);
            Assert.Equal(0, stored.Revision);
        }

        // The staff record IS there...
        Assert.NotNull(await LoadSuspiciousReconcileAsync(_provider, SuspiciousReconcile.BuildKey(serverId, batchId)));

        // ...and the idempotency record is NOT, which is the separate claim: only an Applied batch is
        // ever recorded for replay, so nothing can hand this refusal back as though it had applied.
        Assert.Null(await LoadAppliedBatchAsync(_provider, AppliedBatch.BuildKey(serverId, batchId)));
    }

    /// <summary>
    /// The other side of the same guard, and the reason it keys on <i>large prior inventory plus
    /// near-empty payload</i> rather than on emptiness alone: "the player logged out naked" is an
    /// entirely real scenario. A character who genuinely holds a handful of things and now holds
    /// nothing must still reconcile to nothing.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithAnEmptyPayloadAgainstASmallInventory_StillReconciles()
    {
        var owner = await CreateCharacterAsync(_provider, "Logged Out Naked Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceIds = await GrantAndAckManyAsync(_provider, itemId, owner, 5);

        var batchId = Guid.NewGuid();
        var applied = await ApplyAsync(_provider, new ApplySnapshotCommand(
            serverId, batchId, SnapshotScopeKind.Character, owner, null, 1, SnapshotMode.Full, [], []));

        Assert.Equal(5, applied.Swept);
        Assert.Equal(0, applied.AppliedCount);

        foreach (var instanceId in instanceIds)
        {
            Assert.Null(await LoadLiveAsync(_provider, instanceId));
        }

        // Nothing was recorded for staff — this was an ordinary reconcile, not a refusal.
        Assert.Null(await LoadSuspiciousReconcileAsync(_provider, SuspiciousReconcile.BuildKey(serverId, batchId)));
    }

    /// <summary>
    /// A large sweep on its own is <i>not</i> suspicious — it is what an honest mass-loss reconcile
    /// looks like, and the guard must not stand in its way. 30 of a 60-row scope swept, which is well
    /// past the row threshold, but the payload accounts for the other half rather than for almost
    /// nothing, so neither evidence arm fires.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithALargeSweepButAnEvidencedPayload_IsNotSuspicious()
    {
        var owner = await CreateCharacterAsync(_provider, "Evidenced Large Sweep Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceIds = await GrantAndAckManyAsync(_provider, itemId, owner, 60);

        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId,
            owner,
            sequence: 1,
            upserts: instanceIds.Take(30).Select(id => CharacterUpsert(id, itemId, owner, revision: 1)).ToList()));

        Assert.Equal(30, applied.AppliedCount);
        Assert.Equal(30, applied.Swept);
    }

    /// <summary>
    /// Review round 1: the hole the near-empty arm leaves wide open on its own. That arm asks "how many
    /// rows did the mod name", which does not scale — three named items is thin evidence against a
    /// 30-row inventory and absurd evidence against a 300-row one, yet one constant answers both. So a
    /// mod naming three items could previously wipe an inventory of <i>any</i> size at all, and the one
    /// mechanism this whole task exists to provide would simply not fire.
    ///
    /// The proportional arm is what actually scales: 37 of 40 rows is 92.5% of the scope, past the 90%
    /// threshold, so this is refused even though three upserts clear the near-empty test outright.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeSweepingNearlyTheWholeScope_IsRefusedEvenWithUpsertsPresent()
    {
        var owner = await CreateCharacterAsync(_provider, "Disproportionate Sweep Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceIds = await GrantAndAckManyAsync(_provider, itemId, owner, 40);

        var batchId = Guid.NewGuid();
        var result = await DispatchAsync(_provider, new ApplySnapshotCommand(
            serverId,
            batchId,
            SnapshotScopeKind.Character,
            owner,
            null,
            1,
            SnapshotMode.Full,
            instanceIds.Take(3).Select(id => CharacterUpsert(id, itemId, owner, revision: 1)).ToList(),
            []));

        if (result is not ApplySnapshotResult.SuspiciousReconcile suspicious)
        {
            throw new InvalidOperationException($"Expected SuspiciousReconcile, got {result}");
        }

        // Three upserts: enough to clear the near-empty arm, nowhere near enough to account for 40 rows.
        Assert.Equal(3, suspicious.Upserts);
        Assert.Equal(37, suspicious.WouldHaveSwept);
        Assert.Equal(40, suspicious.ScopeRowCount);

        foreach (var instanceId in instanceIds)
        {
            Assert.NotNull(await LoadLiveAsync(_provider, instanceId));
        }

        Assert.NotNull(await LoadSuspiciousReconcileAsync(_provider, SuspiciousReconcile.BuildKey(serverId, batchId)));
    }

    /// <summary>
    /// The <c>Container</c>-scope half of the guard and its staff record, which nothing covered before
    /// review round 1. The scope enumeration is a different code path entirely (a bounded downward walk
    /// rather than a denormalised root read), and the record has to name the container rather than a
    /// character, so neither is implied by the <c>Character</c>-scope coverage above.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeContainerScopeWithAnEmptyPayloadAgainstALargeCrate_IsRefusedAndRecorded()
    {
        var owner = await CreateCharacterAsync(_provider, "Container Guard Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var contentIds = await GrantAndAckManyAsync(_provider, itemId, owner, 30);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, contentIds.Select(id => ContainerUpsert(id, itemId, crateId, revision: 1)).ToList()));
        Assert.Equal(30, filled.AppliedCount);

        var batchId = Guid.NewGuid();
        var result = await DispatchAsync(_provider, new ApplySnapshotCommand(
            serverId, batchId, SnapshotScopeKind.Container, null, crateId, 1, SnapshotMode.Full, [], []));

        if (result is not ApplySnapshotResult.SuspiciousReconcile suspicious)
        {
            throw new InvalidOperationException($"Expected SuspiciousReconcile, got {result}");
        }

        Assert.Equal(30, suspicious.WouldHaveSwept);
        Assert.Equal(30, suspicious.ScopeRowCount);

        foreach (var contentId in contentIds)
        {
            Assert.NotNull(await LoadLiveAsync(_provider, contentId));
        }

        var record = await LoadSuspiciousReconcileAsync(_provider, SuspiciousReconcile.BuildKey(serverId, batchId));
        Assert.NotNull(record);
        Assert.Equal(SnapshotScopeKind.Container, record.ScopeKind);
        Assert.Equal(crateId, record.ScopeContainerInstanceId);
        Assert.Null(record.ScopeCharacterId);
        Assert.Equal(30, record.WouldHaveSwept);
    }

    /// <summary>
    /// Review round 2's HIGH: <b>one stray JSON field must not switch the downward protection rule off
    /// for a container it names.</b> <c>ApplySnapshotCommand</c> carries both companion id fields, and
    /// the endpoint only ever checked that the <i>required</i> one for the declared kind was present —
    /// so a Character-scoped batch could carry a <c>scope.containerInstanceId</c> naming any crate at
    /// all. That id landed in the sweep's "mentioned containers" set ungated, and mentioning a container
    /// is precisely what unlocks its contents for deletion.
    ///
    /// The result was that one extra field turned "nothing swept" into "that crate and everything in it
    /// deleted" — under the guard's thresholds, so silently and with no staff record. This dispatches
    /// the command directly, past the endpoint's own parse, because the handler-side gate is the
    /// correctness fix and has to hold on its own.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeCharacterScopeCarryingAStrayContainerId_DoesNotUnlockThatContainersContents()
    {
        var owner = await CreateCharacterAsync(_provider, "Stray Container Id Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var contentIds = await GrantAndAckManyAsync(_provider, itemId, owner, 3);
        var looseId = await GrantAndAckAsync(_provider, itemId, owner);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, contentIds.Select(id => ContainerUpsert(id, itemId, crateId, revision: 1)).ToList()));
        Assert.Equal(3, filled.AppliedCount);

        // A Character-scoped Full that never mentions the crate — but carries its id in the container
        // companion field the Character kind does not use.
        var applied = await ApplyAsync(_provider, new ApplySnapshotCommand(
            serverId,
            Guid.NewGuid(),
            SnapshotScopeKind.Character,
            owner,
            crateId,
            1,
            SnapshotMode.Full,
            [CharacterUpsert(looseId, itemId, owner, revision: 2)],
            []));

        Assert.Equal(0, applied.Swept);
        Assert.NotNull(await LoadLiveAsync(_provider, crateId));

        foreach (var contentId in contentIds)
        {
            Assert.NotNull(await LoadLiveAsync(_provider, contentId));
        }
    }

    /// <summary>
    /// Review round 2, item 3: a rejected entry must not confer authority over <i>other</i> rows. The
    /// sharp case is a staff-tombstoned crate — the upsert naming it is correctly rejected
    /// <c>RemovedByStaff</c> and the crate itself correctly survives, yet its id still landed in the
    /// "mentioned containers" set and unlocked every one of its children. The tombstone honoured for the
    /// container and ignored for its contents.
    ///
    /// The distinction the fix draws is exact: a rejected id stays in the <i>named</i> set, so the row it
    /// names is still never swept (that rule is unchanged and deliberate — the mod reported the row as
    /// present, and the batch merely declined to write what it said), but it is removed from the
    /// <i>mentioned</i> set. Same principle as an explicitly deleted row not rescuing its parent: a row
    /// the batch did not write does not get a vote about its children.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeSweep_DoesNotLetARejectedContainerUpsertUnlockItsContents()
    {
        var owner = await CreateCharacterAsync(_provider, "Rejected Crate Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var contentIds = await GrantAndAckManyAsync(_provider, itemId, owner, 3);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, contentIds.Select(id => ContainerUpsert(id, itemId, crateId, revision: 1)).ToList()));
        Assert.Equal(3, filled.AppliedCount);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Patch<ItemInstance>(crateId.Value).Set(x => x.RemovedByStaff, true);
            await session.SaveChangesAsync();
        }

        // The payload names the crate, so it would ordinarily be enumerating its contents — but the
        // upsert naming it is rejected, so the batch never actually wrote anything about it.
        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId, owner, sequence: 1, upserts: [CharacterUpsert(crateId, itemId, owner, revision: 9)]));

        Assert.Equal(SnapshotRejectionReason.RemovedByStaff, Assert.Single(applied.Rejected).Reason);
        Assert.Equal(0, applied.Swept);

        var tombstone = await LoadLiveAsync(_provider, crateId);
        Assert.NotNull(tombstone);
        Assert.True(tombstone.RemovedByStaff);

        foreach (var contentId in contentIds)
        {
            Assert.NotNull(await LoadLiveAsync(_provider, contentId));
        }
    }

    /// <summary>
    /// Review round 2, item 2: the guard must measure what is <b>at stake</b>, not what survives the
    /// sweep's own protection rules. Those rules exist to save rows from deletion, so gating on the
    /// surviving sweep count let every row they saved also make the guard quieter about the ones they
    /// didn't — the guard falling silent in exactly the scenario it exists for.
    ///
    /// Here the mod reports <i>nothing at all</i> about a 46-row character. Forty of those rows sit in a
    /// crate the payload never mentions, so rule 4 protects them and rule 5 rescues the crate; only the
    /// five loose rows are sweepable. Gated on the sweep, five is under the threshold and those five
    /// would go silently. Gated on the scope, forty-six is not, and the claim gets the refusal it
    /// deserves.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeReportingNothingAboutALargeScope_IsRefusedEvenWhenProtectionShrinksTheSweep()
    {
        var owner = await CreateCharacterAsync(_provider, "Protected Scope Guard Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var crateId = await GrantAndAckAsync(_provider, itemId, owner);
        var nestedIds = await GrantAndAckManyAsync(_provider, itemId, owner, 40);
        var looseIds = await GrantAndAckManyAsync(_provider, itemId, owner, 5);

        var filled = await ApplyAsync(_provider, CharacterScopedCommand(
            serverId, owner, nestedIds.Select(id => ContainerUpsert(id, itemId, crateId, revision: 1)).ToList()));
        Assert.Equal(40, filled.AppliedCount);

        var batchId = Guid.NewGuid();
        var result = await DispatchAsync(_provider, new ApplySnapshotCommand(
            serverId, batchId, SnapshotScopeKind.Character, owner, null, 1, SnapshotMode.Full, [], []));

        if (result is not ApplySnapshotResult.SuspiciousReconcile suspicious)
        {
            throw new InvalidOperationException($"Expected SuspiciousReconcile, got {result}");
        }

        // The protection rules did their job — only the five loose rows were ever sweepable, well under
        // the threshold — and the guard fired anyway, on the size of the claim rather than its residue.
        Assert.Equal(5, suspicious.WouldHaveSwept);
        Assert.Equal(46, suspicious.ScopeRowCount);
        Assert.Equal(25, suspicious.ScopeRowsThreshold);

        foreach (var looseId in looseIds)
        {
            Assert.NotNull(await LoadLiveAsync(_provider, looseId));
        }

        var record = await LoadSuspiciousReconcileAsync(_provider, SuspiciousReconcile.BuildKey(serverId, batchId));
        Assert.NotNull(record);
        Assert.Equal(5, record.WouldHaveSwept);
        Assert.Equal(46, record.ScopeRowCount);
    }

    /// <summary>
    /// Review round 3's MEDIUM, half one. <c>LoadScopeRowsAsync</c> deliberately does not filter
    /// <c>PendingSpawn</c> (the anchor walk needs those rows as potential parents) and
    /// <c>ComputeSweep</c>'s rule 1 refuses to sweep them — so counting them toward the guard's gate
    /// counted rows that were never at stake, and the guard over-fired on entirely <i>correct</i>
    /// batches.
    ///
    /// A character with 30 undelivered grants and 2 carried items sends a perfectly accurate <c>Full</c>
    /// naming both carried rows: nothing to sweep at all, and yet it was refused 422 and its two
    /// legitimate upserts discarded. Worse, that is not self-correcting the way a threshold trip
    /// normally is — the condition is a property of stored state rather than of the batch, so every
    /// retry is refused identically and writes another staff record under a fresh <c>batchId</c>.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeForACharacterHoldingManyUndeliveredGrants_IsNotRefused()
    {
        var owner = await CreateCharacterAsync(_provider, "Undelivered Grants Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var carriedIds = await GrantAndAckManyAsync(_provider, itemId, owner, 2);

        // 30 pending rows: minted, never acked, never seen by the mod. Their absence from a snapshot
        // means nothing, which is exactly why they are never swept — and therefore why they must not
        // count toward how much this batch is putting at stake.
        var pendingIds = new List<ItemInstanceId>();
        for (var i = 0; i < 30; i++)
        {
            pendingIds.Add(await GrantOneAsync(_provider, itemId, owner));
        }

        var applied = await ApplyAsync(_provider, FullScopedCommand(
            serverId,
            owner,
            sequence: 1,
            upserts: carriedIds.Select(id => CharacterUpsert(id, itemId, owner, revision: 1)).ToList()));

        Assert.Equal(2, applied.AppliedCount);
        Assert.Equal(0, applied.Swept);

        foreach (var pendingId in pendingIds)
        {
            var stillPending = await LoadLiveAsync(_provider, pendingId);
            Assert.NotNull(stillPending);
            Assert.True(stillPending.PendingSpawn);
        }
    }

    /// <summary>
    /// Review round 3's MEDIUM, half two, and the sharper of the pair: a staff tombstone is a live row
    /// kept <i>indefinitely</i>, so counting tombstones toward the gate broke "the player logged out
    /// naked" — the one case <c>WorldSettings</c>' own comment says the guard must never touch —
    /// permanently, for any character who had ever accumulated 26 of them.
    ///
    /// Same reasoning as the pending half: rule 2 refuses to sweep a tombstone, so a tombstone was never
    /// at stake, so it has no business inflating the measure of what was.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeForACharacterHoldingManyStaffTombstones_StillReconcilesToEmpty()
    {
        var owner = await CreateCharacterAsync(_provider, "Many Tombstones Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);

        var tombstonedIds = await GrantAndAckManyAsync(_provider, itemId, owner, 26);
        var carriedIds = await GrantAndAckManyAsync(_provider, itemId, owner, 3);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            foreach (var tombstonedId in tombstonedIds)
            {
                session.Patch<ItemInstance>(tombstonedId.Value).Set(x => x.RemovedByStaff, true);
            }

            await session.SaveChangesAsync();
        }

        // Logged out naked: three carried rows, an honest empty payload.
        var batchId = Guid.NewGuid();
        var applied = await ApplyAsync(_provider, new ApplySnapshotCommand(
            serverId, batchId, SnapshotScopeKind.Character, owner, null, 1, SnapshotMode.Full, [], []));

        Assert.Equal(3, applied.Swept);

        foreach (var carriedId in carriedIds)
        {
            Assert.Null(await LoadLiveAsync(_provider, carriedId));
        }

        // The tombstones are untouched and still sticky, and no staff record was written.
        foreach (var tombstonedId in tombstonedIds)
        {
            var tombstone = await LoadLiveAsync(_provider, tombstonedId);
            Assert.NotNull(tombstone);
            Assert.True(tombstone.RemovedByStaff);
        }

        Assert.Null(await LoadSuspiciousReconcileAsync(_provider, SuspiciousReconcile.BuildKey(serverId, batchId)));
    }

    /// <summary>
    /// Scope is <c>Character</c> or <c>Container</c> only in this phase: a server-wide reconcile is a
    /// separate, explicitly-authorised staff operation with a dry run, and it lands with
    /// world-structure state where it is actually needed. That matters far more now than it did in
    /// tasks 1-3, because <c>Full</c> <b>deletes</b> — an unbounded <c>Full</c> is a deployment-wide
    /// wipe rather than a widened query.
    ///
    /// <see cref="SnapshotScopeKind"/> declares no server-wide member at all, and the endpoint's own
    /// <c>Enum.IsDefined</c> parse refuses anything else, so this test has to cast past both to reach
    /// the handler's own guard — which is the point of that guard existing: it is what makes a future
    /// third member fail closed instead of being silently interpreted as "everything".
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithAServerWideScope_IsRejectedAsUnsupportedFullScope()
    {
        var serverId = await ServerIdAsync(_provider);

        var result = await DispatchAsync(_provider, new ApplySnapshotCommand(
            serverId, Guid.NewGuid(), (SnapshotScopeKind)2, null, null, 1, SnapshotMode.Full, [], []));

        if (result is not ApplySnapshotResult.UnsupportedFullScope unsupported)
        {
            throw new InvalidOperationException($"Expected UnsupportedFullScope, got {result}");
        }

        Assert.Equal((SnapshotScopeKind)2, unsupported.ScopeKind);
    }

    /// <summary>
    /// The subtler shape of the same mistake: a supported scope kind whose companion id is missing. A
    /// <c>Full</c> batch that names <c>Character</c> but no <c>characterId</c> has no bounded set of
    /// rows to reconcile, which is exactly what "server-wide" means however it got that way. Tasks 1-3
    /// left this to <c>ScopeCursor.BuildKey</c>'s bare <c>InvalidOperationException</c>, which was the
    /// right call while a <c>Full</c> batch could only advance a counter and is the wrong one now that
    /// reaching that point would first have had to decide which rows to delete.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_FullModeWithNoScopeId_IsRejectedAsUnsupportedFullScope()
    {
        var serverId = await ServerIdAsync(_provider);

        var result = await DispatchAsync(_provider, new ApplySnapshotCommand(
            serverId, Guid.NewGuid(), SnapshotScopeKind.Character, null, null, 1, SnapshotMode.Full, [], []));

        Assert.True(result is ApplySnapshotResult.UnsupportedFullScope, $"Expected UnsupportedFullScope, got {result}");
    }

    /// <summary>
    /// The first lock the test above has to cast past, asserted directly: this enum is what makes a
    /// server-wide scope unrepresentable on the wire in the first place. Append-only by convention, so
    /// a third member is a deliberate act — and this test is what makes it a visible one.
    /// </summary>
    [Fact]
    public void SnapshotScopeKind_DeclaresNoServerWideMember()
    {
        Assert.Equal(
            [SnapshotScopeKind.Character, SnapshotScopeKind.Container],
            Enum.GetValues<SnapshotScopeKind>());
    }

    /// <summary>
    /// Task 3's replay contract, extended over task 4's new counter: a replayed <c>batchId</c> returns
    /// the original response byte-for-byte, and <c>swept</c> is part of "byte-for-byte". Without the
    /// stored field this would replay as 0 and tell the Bridge a destructive batch deleted nothing.
    /// </summary>
    [Fact]
    public async Task ApplySnapshot_ReplayingAFullBatchThatSwept_ReturnsTheOriginalSweptCount()
    {
        var owner = await CreateCharacterAsync(_provider, "Swept Replay Character");
        var itemId = await CreateCatalogItemAsync(_provider);
        var serverId = await ServerIdAsync(_provider);
        var instanceIds = await GrantAndAckManyAsync(_provider, itemId, owner, 3);

        var batchId = Guid.NewGuid();
        var command = new ApplySnapshotCommand(
            serverId,
            batchId,
            SnapshotScopeKind.Character,
            owner,
            null,
            1,
            SnapshotMode.Full,
            [CharacterUpsert(instanceIds[0], itemId, owner, revision: 1)],
            []);

        var original = await ApplyAsync(_provider, command);
        Assert.False(original.ReplayOfPriorBatch);
        Assert.Equal(2, original.Swept);

        var replay = await ApplyAsync(_provider, command);
        Assert.True(replay.ReplayOfPriorBatch);
        Assert.Equal(original.Swept, replay.Swept);
        Assert.Equal(original.AppliedCount, replay.AppliedCount);
        Assert.Equal(original.CascadeDeleted, replay.CascadeDeleted);
    }
}
