using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Items;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Shared, provider-scoped tally the decorator below writes into. Registered as a singleton so a test
/// can read it after the handler's own DI scope has gone.
/// </summary>
internal sealed class RepositoryCallCounts
{
    public int LoadManyCalls;
    public int FindChildrenOfManyCalls;

    public void Reset()
    {
        LoadManyCalls = 0;
        FindChildrenOfManyCalls = 0;
    }
}

/// <summary>
/// Pass-through decorator that counts the two batched reads the snapshot write path makes, so claims
/// about them can be asserted rather than argued from the source. Two properties need it and neither
/// is otherwise observable from outside the handler:
///
/// <list type="bullet">
/// <item>a batch whose every entry was already rejected must issue <b>no</b> instance read at all —
/// the "malformed input never reaches storage" property, which is only true if the id set handed to
/// <see cref="LoadManyAsync"/> is filtered by the rejections (task 2 review, item 4);</item>
/// <item>the upward and downward chain walks must be bounded by container depth and <b>constant in
/// batch size</b> — the thing that keeps "one load round trip" honest when a second query was added
/// to reach ancestors and descendants (task 2 review, item 3).</item>
/// </list>
///
/// <paramref name="afterLoadMany"/> is the other half of the same seam: a hook that fires after every
/// batched load, so a test can make a concurrent writer commit at a chosen point <i>inside</i> a
/// handler's load-to-save window. Some races are only reachable from there —
/// <c>AcknowledgeSpawnsTests.Ack_WhenTheInstanceIsDeletedAfterTheHandlerLoadedIt_...</c> is one — and
/// the alternative is asserting that a reconciliation exists rather than that it works. The hook is
/// deliberately dumb, firing on every call and leaving once-only logic to the caller.
///
/// Wired through <c>TestServices.BuildProvider</c>'s <c>configureServices</c> hook, the same seam
/// <c>GatherTests</c> uses to swap in its faulty repository, so no production registration changes for
/// the sake of a test.
/// </summary>
internal sealed class InstrumentedItemInstanceRepository(
    IItemInstanceRepository inner,
    RepositoryCallCounts counts,
    Func<ValueTask>? afterLoadMany = null) : IItemInstanceRepository
{
    public ValueTask<ItemInstance?> FindByIdAsync(ItemInstanceId id, CancellationToken cancellationToken)
        => inner.FindByIdAsync(id, cancellationToken);

    public ValueTask<IReadOnlyList<ItemInstance>> FindByRootCharacterAsync(CharacterId rootCharacterId, CancellationToken cancellationToken)
        => inner.FindByRootCharacterAsync(rootCharacterId, cancellationToken);

    public ValueTask<IReadOnlyList<ItemInstance>> FindCarriedByRootCharacterAsync(CharacterId rootCharacterId, DateTimeOffset now, CancellationToken cancellationToken)
        => inner.FindCarriedByRootCharacterAsync(rootCharacterId, now, cancellationToken);

    public ValueTask<IReadOnlyList<ItemInstance>> FindPendingByRootCharacterAsync(
        CharacterId rootCharacterId, int limit, int maxDeliveryAttempts, DateTimeOffset now, CancellationToken cancellationToken)
        => inner.FindPendingByRootCharacterAsync(rootCharacterId, limit, maxDeliveryAttempts, now, cancellationToken);

    public ValueTask<IReadOnlyList<ItemInstance>> LoadManyAsync(IReadOnlyList<ItemInstanceId> ids, CancellationToken cancellationToken)
    {
        // Counted before the inner call's own empty-list short-circuit, deliberately: the property
        // under test is that the handler doesn't *ask*, not that the repository is clever enough to
        // skip a pointless query.
        Interlocked.Increment(ref counts.LoadManyCalls);
        return afterLoadMany is null
            ? inner.LoadManyAsync(ids, cancellationToken)
            : LoadThenNotifyAsync(ids, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<ItemInstance>> LoadThenNotifyAsync(IReadOnlyList<ItemInstanceId> ids, CancellationToken cancellationToken)
    {
        var loaded = await inner.LoadManyAsync(ids, cancellationToken);
        await afterLoadMany!();
        return loaded;
    }

    public ValueTask<IReadOnlyList<ItemInstance>> FindChildrenOfManyAsync(IReadOnlyList<ItemInstanceId> containerInstanceIds, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref counts.FindChildrenOfManyCalls);
        return inner.FindChildrenOfManyAsync(containerInstanceIds, cancellationToken);
    }

    public ValueTask<IReadOnlyList<ItemInstance>> FindChildrenAsync(ItemInstanceId containerInstanceId, CancellationToken cancellationToken)
        => inner.FindChildrenAsync(containerInstanceId, cancellationToken);

    public ValueTask<IReadOnlyList<ItemInstance>> FindUndeliverableAsync(int maxDeliveryAttempts, CancellationToken cancellationToken)
        => inner.FindUndeliverableAsync(maxDeliveryAttempts, cancellationToken);

    public void Store(ItemInstance instance) => inner.Store(instance);

    public void WriteAppliedSnapshot(ItemInstance instance) => inner.WriteAppliedSnapshot(instance);

    public void WriteAcknowledgedSpawn(ItemInstance instance) => inner.WriteAcknowledgedSpawn(instance);

    public void RecordDeliveryAttempt(ItemInstance instance, DateTimeOffset now) => inner.RecordDeliveryAttempt(instance, now);

    public void RecordSpawnFailure(ItemInstance instance, SpawnFailureReason reason, DateTimeOffset now)
        => inner.RecordSpawnFailure(instance, reason, now);

    public void RewriteResolvedRoots(
        ItemInstance instance, CharacterId? rootCharacterId, GameServerId? rootGameServerId, DateTimeOffset? expiresAt, DateTimeOffset now)
        => inner.RewriteResolvedRoots(instance, rootCharacterId, rootGameServerId, expiresAt, now);

    public void Eject(ItemInstance instance) => inner.Eject(instance);

    public void SoftDelete(ItemInstance instance) => inner.SoftDelete(instance);

    public ValueTask SaveChangesAsync(CancellationToken cancellationToken) => inner.SaveChangesAsync(cancellationToken);

    public ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
        ItemId itemId, int quantity, CharacterId ownerCharacterId, ItemOrigin origin, OriginRef? originRef, CancellationToken cancellationToken)
        => inner.GrantAsync(itemId, quantity, ownerCharacterId, origin, originRef, cancellationToken);

    public ValueTask<IReadOnlyList<GrantedInstance>> GrantAsync(
        ItemId itemId, string? prefabClassName, int quantity, CharacterId ownerCharacterId, ItemOrigin origin, OriginRef? originRef, CancellationToken cancellationToken)
        => inner.GrantAsync(itemId, prefabClassName, quantity, ownerCharacterId, origin, originRef, cancellationToken);
}
