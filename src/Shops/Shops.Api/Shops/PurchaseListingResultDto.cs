using ELifeRPG.World.Application.Common;

namespace ELifeRPG.Shops.Api.Shops;

/// <summary>
/// One freshly minted instance handed over by a purchase. Deliberately a separate type from
/// World.Api's own GrantedInstanceDto (on the gather response), which is field-for-field identical,
/// rather than a shared contract — per ARCHITECTURE.md §9e, DTOs live beside their endpoint and own
/// their mapping; there is no shared DTO project, and a shared type here would couple two modules' API
/// surfaces together. The duplication is the point: the two stay identical on purpose, so the mod's
/// adopt-and-ack path is written once and works unmodified against either response body.
/// </summary>
public sealed record GrantedInstanceDto
{
    public required Guid InstanceId { get; init; }

    public required Guid ItemId { get; init; }

    public required string PrefabClassName { get; init; }

    public static GrantedInstanceDto Create(GrantedInstance source) => new()
    {
        InstanceId = source.InstanceId.Value,
        ItemId = source.ItemId.Value,
        PrefabClassName = source.PrefabClassName,
    };
}

public sealed record PurchaseListingResultDto
{
    public required decimal TotalPaid { get; init; }

    public required int NewStock { get; init; }

    public required IReadOnlyList<GrantedInstanceDto> GrantedInstances { get; init; }

    public static PurchaseListingResultDto Create(PurchaseListingResult.Purchased source) => new()
    {
        TotalPaid = source.TotalPaid,
        NewStock = source.NewStock,
        GrantedInstances = source.GrantedInstances.Select(GrantedInstanceDto.Create).ToList(),
    };
}
