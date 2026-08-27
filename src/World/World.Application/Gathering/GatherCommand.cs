using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Application.Skills;
using ELifeRPG.Characters.Domain.Events;
using ELifeRPG.Characters.Domain.Skills;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain.Exceptions;

namespace ELifeRPG.World.Application.Gathering;

/// <summary>
/// The gathering path's own <c>PurchaseListingResult</c>: a gather action grants an item and skill XP
/// atomically, so the two can never diverge (loot without XP, or XP without loot, is unreconcilable
/// after the fact — see the phase 1 task brief). Every case here that can be evaluated without I/O, or
/// via a read-only cross-module contract, is deliberately checked before <c>transactionFactory.BeginAsync</c>
/// — same ruling as <c>PurchaseListingResult</c>'s: never take a lock, and never grant either leg, for
/// a gather that cannot be fulfilled.
/// </summary>
public union GatherResult(
    GatherResult.Gathered,
    GatherResult.CharacterNotFound,
    GatherResult.WrongServer,
    GatherResult.UnknownAction,
    GatherResult.InvalidQuantity,
    GatherResult.GrantTooLarge,
    GatherResult.ItemNotInCatalog)
{
    public record Gathered(IReadOnlyList<SkillXpGrant> Gains, IReadOnlyList<GrantedInstance> GrantedInstances);

    public record CharacterNotFound;

    /// <summary>
    /// The character exists but is not currently on the calling gameserver — the design's correctness
    /// core, mechanism 4: an inventory write for a character whose <c>Character.CurrentServerId</c> is a
    /// different gameserver is rejected. Gathering is unambiguously an inventory write (it mints item
    /// instances), and it was the one gameserver-scoped write path on this branch without the guard, so
    /// server A could mint items and skill XP for a character playing on server B using only its own
    /// scope. Guards on <c>CurrentServerId</c>, never <c>SessionActive</c> — same as
    /// <c>AcknowledgeSpawnsHandler</c> and <c>SpawnFailedHandler</c>. (Whole-branch review, I1.)
    ///
    /// A shop purchase legitimately has no such guard: a portal buy has no gameserver at all, and the
    /// delivery server is resolved later, at ack time. That asymmetry is real; this one was an omission.
    /// </summary>
    public record WrongServer;

    /// <summary>
    /// <c>GatherCommand.Action</c> doesn't parse to a known <c>SkillAction</c>, or parses to one with
    /// no reward configured in <c>SkillActionCatalog.Rewards</c>. Checked first and entirely in
    /// memory — no I/O is needed to reject a malformed action string, so this is rejected before any
    /// cross-module dispatch, exactly like <c>RecordSkillActionResult.UnknownAction</c>.
    /// </summary>
    public record UnknownAction;

    /// <summary>
    /// <c>GatherCommand.Quantity</c> is zero or negative. Checked in memory, alongside the action
    /// parse, before any dispatch. The purchase path gets this invariant free from
    /// <c>ShopListing.Purchase</c>'s own guard; gathering has no equivalent domain gate, so without
    /// this check the two orchestrators diverged: <c>Quantity = 0</c> appended a permanent 0-XP
    /// <c>SkillXpGranted</c> event while minting nothing, and <c>Quantity = -1</c> reached
    /// <c>CharacterSkills.GrantXp</c> and threw <c>ArgumentOutOfRangeException</c> — a 500 for what is
    /// plainly a bad request. (Whole-branch review, I7.)
    /// </summary>
    public record InvalidQuantity(int Requested);

    /// <summary>
    /// The gathered quantity would mint more item instances than <c>WorldSettings.MaxInstancesPerGrant</c>
    /// allows. Evaluated at the precheck, before <c>transactionFactory.BeginAsync</c> — no skill XP is
    /// ever granted for a gather that cannot be fulfilled. Same ruling as
    /// <c>PurchaseListingResult.GrantTooLarge</c>.
    /// </summary>
    public record GrantTooLarge(int Requested, int MaxInstancesPerGrant);

    /// <summary>
    /// <c>GatherCommand.ItemId</c> has no catalog entry to resolve a <c>PrefabClassName</c> from.
    /// Normally returned from the precheck (a batched <c>ItemCatalogEntriesQuery</c> dispatch, before
    /// <c>transactionFactory.BeginAsync</c>) — same reasoning as <c>PurchaseListingResult.ItemNotInCatalog</c>.
    /// Unlike a shop listing, a gather request names its item fresh on every call rather than
    /// referencing a value stored earlier, so there is no equivalent "valid when created, stale by the
    /// time it's used" window here — the precheck is expected to be the only path that ever produces
    /// this case in practice. Can still, in principle, be produced from a caught
    /// <see cref="ItemNotInCatalogException"/> if the in-transaction grant's defense-in-depth check
    /// ever fires (see <c>IItemInstanceRepository.GrantAsync</c>'s prefab-taking overload) — in that
    /// case the skill XP append was already queued but never saved, and the transaction is never
    /// committed, so disposing the uncommitted <see cref="ICrossModuleTransaction"/> still rolls back
    /// and no XP is recorded either.
    /// </summary>
    public record ItemNotInCatalog(ItemId ItemId);
}

/// <summary>
/// A gather action: mint <paramref name="Quantity"/> discrete item instances (no stacking — see
/// <see cref="IItemInstanceRepository.GrantAsync(ItemId,string?,int,CharacterId,ItemOrigin,OriginRef?,CancellationToken)"/>)
/// and grant the same <paramref name="Quantity"/>-scaled skill XP <paramref name="Action"/> rewards,
/// in one commit.
/// </summary>
public sealed record GatherCommand(
    GameServerId GameServerId,
    CharacterId CharacterId,
    string Action,
    ItemId ItemId,
    int Quantity) : IRequest<GatherResult>;

/// <summary>
/// Lives in World.Application, not Characters.Application: World.Application already references
/// Characters.Application (for <c>CharacterLookupQuery</c>/<c>CharactersOnServerQuery</c>), so putting
/// this orchestrator in Characters and referencing World back would be a circular project reference.
/// The dependency direction is fixed — World depends on Characters, never the reverse — so the module
/// that already has both sides' contracts in scope is the one that orchestrates. Structurally this
/// mirrors Shops.Application's <c>PurchaseListingHandler</c> exactly: one <see cref="ICrossModuleTransaction"/>,
/// a fully resolved precheck before opening it, then a strictly in-memory-insert path through both
/// participating modules' cross-module repository factories before one commit.
/// </summary>
public sealed class GatherHandler(
    ICrossModuleTransactionFactory transactionFactory,
    ICharacterSkillsRepositoryFactory characterSkillsRepositoryFactory,
    IItemInstanceRepositoryFactory itemInstanceRepositoryFactory,
    IMediator mediator)
    : IRequestHandler<GatherCommand, GatherResult>
{
    public async ValueTask<GatherResult> Handle(GatherCommand request, CancellationToken cancellationToken)
    {
        // Pure, in-memory precheck — no I/O needed to reject a malformed action string, so it comes
        // first, before any cross-module dispatch.
        if (!Enum.TryParse<SkillAction>(request.Action, out var action) || !SkillActionCatalog.Rewards.TryGetValue(action, out var rewards))
        {
            return new GatherResult.UnknownAction();
        }

        // Also pure and in-memory, so it sits with the action parse rather than after any dispatch. See
        // GatherResult.InvalidQuantity: the purchase path gets this from ShopListing.Purchase's own
        // guard, gathering has no domain gate of its own, and both a zero and a negative quantity are
        // reachable straight off the wire.
        if (request.Quantity <= 0)
        {
            return new GatherResult.InvalidQuantity(request.Quantity);
        }

        // Character-existence precheck — dispatches Characters.Application's public CharacterLookupQuery
        // via IMediator (never a direct ICharacterRepository injection — see ARCHITECTURE.md §9e), same
        // sanctioned Application->Application borrow PurchaseListingHandler uses for WorldSettingsQuery.
        var characterLookup = await mediator.Send(new CharacterLookupQuery(request.CharacterId), cancellationToken);
        if (characterLookup is not CharacterLookupResult.Found)
        {
            return new GatherResult.CharacterNotFound();
        }

        // Server guard — the design's mechanism 4, matching how the ack and spawn-failed paths do it:
        // the same batched CharactersOnServerQuery contract, compared against Character.CurrentServerId
        // (never SessionActive, which Character.cs documents as unreliable after an ungraceful crash).
        // A gather is an inventory write, so a gameserver may only drive one for a character actually
        // on it. Checked before transactionFactory.BeginAsync, like every other rejection here.
        var onThisServer = await mediator.Send(
            new CharactersOnServerQuery(request.GameServerId, [request.CharacterId]), cancellationToken);
        if (!onThisServer.Contains(request.CharacterId))
        {
            return new GatherResult.WrongServer();
        }

        // Catalog-resolution precheck — dispatches Items.Application's public, batched
        // ItemCatalogEntriesQuery via IMediator. Resolving here, before any transaction opens, means the
        // in-transaction grant below can receive an already-resolved prefab and do a pure insert with no
        // external dispatch — same reasoning as PurchaseListingHandler's identical precheck.
        var catalogEntries = await mediator.Send(new ItemCatalogEntriesQuery([request.ItemId]), cancellationToken);
        if (!catalogEntries.TryGetValue(request.ItemId, out var catalogEntry))
        {
            return new GatherResult.ItemNotInCatalog(request.ItemId);
        }

        var prefabClassName = catalogEntry.PrefabClassName;

        // Grant-size precheck — dispatches World.Application's own public WorldSettingsQuery via
        // IMediator (never a direct IWorldSettingsRepository injection). Must happen before
        // transactionFactory.BeginAsync: never grant skill XP for a gather that cannot be fulfilled.
        var worldSettings = await mediator.Send(new WorldSettingsQuery(), cancellationToken);
        if (request.Quantity > worldSettings.MaxInstancesPerGrant)
        {
            return new GatherResult.GrantTooLarge(request.Quantity, worldSettings.MaxInstancesPerGrant);
        }

        await using var transaction = await transactionFactory.BeginAsync(cancellationToken);

        // Repositories obtained from a cross-module transaction handle are intentionally never
        // disposed here — only `transaction` owns the underlying connection/transaction.
        var characterSkillsRepository = characterSkillsRepositoryFactory.CreateFor(transaction.Handle);

        // Same "find-or-initialize" shape as RecordSkillActionHandler — a character's first gather (or
        // first skill action of any kind) has no CharacterSkills stream yet.
        var characterSkills = await characterSkillsRepository.FindByCharacterIdAsync(request.CharacterId, cancellationToken);
        if (characterSkills is null)
        {
            var initialized = new CharacterSkillsInitialized(new CharacterSkillsId(Guid.NewGuid()), request.CharacterId);
            characterSkills = CharacterSkills.Create(initialized);
            characterSkillsRepository.StartStream(characterSkills, initialized);
        }

        var gains = new List<SkillXpGrant>();
        foreach (var reward in rewards)
        {
            var levelBefore = SkillLeveling.LevelForTotalXp(characterSkills.TotalXpBySkill.GetValueOrDefault(reward.Skill));
            var domainEvent = characterSkills.GrantXp(reward.Skill, reward.XpReward * request.Quantity, XpSource.Action, action);
            var levelAfter = SkillLeveling.LevelForTotalXp(domainEvent.NewTotalXp);

            characterSkillsRepository.Append(characterSkills.Id, domainEvent);
            gains.Add(new SkillXpGrant(reward.Skill, domainEvent.Amount, domainEvent.NewTotalXp, levelAfter, levelAfter > levelBefore));
        }

        // Repositories obtained from a cross-module transaction handle are intentionally never
        // disposed here — only `transaction` owns the underlying connection/transaction.
        var itemInstanceRepository = itemInstanceRepositoryFactory.CreateFor(transaction.Handle);

        IReadOnlyList<GrantedInstance> grantedInstances;
        try
        {
            // The prefab-taking overload: it does pure in-memory inserts with no external dispatch,
            // since prefabClassName was already resolved at the precheck above, before any transaction
            // opened. Gathering mints discrete entities like any other grant — no stacking, no target
            // instance, and so no row lock either (same as PurchaseListingHandler's grant). The catch
            // below is defense in depth, not the normal path — see GatherResult.ItemNotInCatalog's doc
            // comment on why this path has no realistic way to reach it in practice.
            grantedInstances = await itemInstanceRepository.GrantAsync(
                request.ItemId,
                prefabClassName,
                request.Quantity,
                request.CharacterId,
                ItemOrigin.Gathered,
                new OriginRef("Gathering", request.Action),
                cancellationToken);
        }
        catch (ItemNotInCatalogException)
        {
            return new GatherResult.ItemNotInCatalog(request.ItemId);
        }

        await characterSkillsRepository.SaveChangesAsync(cancellationToken);
        await itemInstanceRepository.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new GatherResult.Gathered(gains, grantedInstances);
    }
}
