using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Domain.Exceptions;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>
/// The World-internal entry point onto the grant path (see <see cref="IItemInstanceRepository.GrantAsync"/>)
/// for a caller that isn't already inside a cross-module transaction — e.g. a staff
/// <c>ItemOrigin.AdminGrant</c>, or an integration test exercising the grant mechanism directly. Task
/// 6 (Shops) and task 7 (Gathering) do not go through this command: they already hold a
/// cross-module transaction and call <c>transaction.Enlist{IItemInstanceRepository}().GrantAsync</c>
/// directly, since dispatching this command would open a second, unrelated database session.
/// </summary>
public union GrantItemsResult(GrantItemsResult.Granted, GrantItemsResult.QuantityExceedsCap, GrantItemsResult.ItemNotInCatalog)
{
    public record Granted(IReadOnlyList<GrantedInstance> Instances);

    /// <summary>The caller is expected to check this before opening any transaction — see WorldSettings.MaxInstancesPerGrant.</summary>
    public record QuantityExceedsCap(int Requested, int MaxInstancesPerGrant);

    /// <summary>
    /// Maps <see cref="ItemNotInCatalogException"/> — thrown by <c>GrantAsync</c> when
    /// <see cref="ItemId"/> has no catalog entry to resolve a <c>PrefabClassName</c> from. A caller
    /// reaching <c>GrantAsync</c> directly through <c>ITransactionParticipant{IItemInstanceRepository}</c> (task 6, task
    /// 7) bypasses this handler and must catch that exception itself.
    /// </summary>
    public record ItemNotInCatalog(ItemId ItemId);
}

public sealed record GrantItemsCommand(
    ItemId ItemId,
    int Quantity,
    CharacterId OwnerCharacterId,
    ItemOrigin Origin,
    OriginRef? OriginRef) : IRequest<GrantItemsResult>;

public sealed class GrantItemsHandler(
    IItemInstanceRepository repository,
    IWorldSettingsRepository settingsRepository)
    : IRequestHandler<GrantItemsCommand, GrantItemsResult>
{
    public async ValueTask<GrantItemsResult> Handle(GrantItemsCommand request, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(cancellationToken);
        if (request.Quantity > settings.MaxInstancesPerGrant)
        {
            return new GrantItemsResult.QuantityExceedsCap(request.Quantity, settings.MaxInstancesPerGrant);
        }

        IReadOnlyList<GrantedInstance> granted;
        try
        {
            granted = await repository.GrantAsync(
                request.ItemId,
                request.Quantity,
                request.OwnerCharacterId,
                request.Origin,
                request.OriginRef,
                cancellationToken);
        }
        catch (ItemNotInCatalogException)
        {
            return new GrantItemsResult.ItemNotInCatalog(request.ItemId);
        }

        await repository.SaveChangesAsync(cancellationToken);

        return new GrantItemsResult.Granted(granted);
    }
}
