using ELifeRPG.Shared.Kernel;

namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised by the grant path (<c>IItemInstanceRepository.GrantAsync</c>) when the item being granted
/// has no catalog entry to resolve a <c>PrefabClassName</c> from — an uncatalogued or mistyped
/// <c>ItemId</c>. Like the other domain guard exceptions in this namespace, this is meant to be
/// caught by an Application handler and mapped onto a result union case rather than propagate as a
/// 500 (unlike a plain <see cref="InvalidOperationException"/>, which this repo's convention reserves
/// for genuine bugs). <c>GrantAsync</c> is a cross-module contract — Task 6 (Shops) and Task 7
/// (Gathering) call it directly through <c>ITransactionParticipant{IItemInstanceRepository}</c> rather than through
/// <c>GrantItemsHandler</c>, so they must catch this themselves; they do not inherit that handler's
/// mapping.
/// </summary>
public sealed class ItemNotInCatalogException(ItemId itemId)
    : InvalidOperationException($"Item '{itemId.Value}' has no catalog entry to grant from.")
{
    public ItemId ItemId { get; } = itemId;
}
