using ELifeRPG.World.Domain;
using ELifeRPG.World.Domain.Items;

namespace ELifeRPG.World.Api.Inventory;

/// <summary>
/// Composes both halves of the phase 1 plan's Controller ruling into the one document the Bridge
/// reads at boot: the operationally tunable <see cref="WorldSettings"/> values, alongside the
/// structural domain constants (<see cref="ItemInstance.MaxContainerDepth"/>,
/// <see cref="ItemAttributes.MaxKeys"/>, <see cref="ItemAttributes.MaxValueLength"/>) that never
/// change at runtime. The Bridge hardcodes nothing from either half.
/// </summary>
public sealed record WorldLimitsDto(
    int MaxInstancesPerGrant,
    int MaxContainerDepth,
    int MaxAttributeKeys,
    int MaxAttributeValueLength,
    int GroundItemTtlSeconds,
    int MaxPendingPageSize,
    int MaxDeliveryAttempts,
    int MaxAcksPerBatch,
    int MaxChildrenPerAck)
{
    public static WorldLimitsDto Create(WorldSettings settings) => new(
        settings.MaxInstancesPerGrant,
        ItemInstance.MaxContainerDepth,
        ItemAttributes.MaxKeys,
        ItemAttributes.MaxValueLength,
        settings.GroundItemTtlSeconds,
        settings.MaxPendingPageSize,
        settings.MaxDeliveryAttempts,
        settings.MaxAcksPerBatch,
        settings.MaxChildrenPerAck);
}
