using ELifeRPG.World.Domain.Items;

namespace ELifeRPG.World.Api.Inventory;

/// <summary>
/// One instance on the wire, shared by both join-time reads
/// (<c>GET /api/inventory/characters/{characterId}/items</c> and <c>/pending</c>). Carries
/// <see cref="Revision"/> so the mod can seed its last-write-wins counters, and
/// <see cref="DeliveryAttempts"/> so the pending read's caller can see how many times a row has been
/// offered — see the phase 1 task brief.
/// </summary>
public sealed record ItemInstanceDto
{
    public required Guid InstanceId { get; init; }

    public required Guid ItemId { get; init; }

    public required long Revision { get; init; }

    public string? DisplayNameOverride { get; init; }

    public required ItemParentDto Parent { get; init; }

    public float? Durability { get; init; }

    public int? Ammo { get; init; }

    public required IReadOnlyDictionary<string, string> Attributes { get; init; }

    public required string Origin { get; init; }

    public string? OriginRefModule { get; init; }

    public string? OriginRefId { get; init; }

    public required bool PendingSpawn { get; init; }

    /// <summary>
    /// Backend-owned; never sent by the mod and never the same counter as <see cref="Revision"/>. See
    /// <c>ItemInstance.RecordDeliveryAttempt</c>.
    /// </summary>
    public required int DeliveryAttempts { get; init; }

    /// <summary>
    /// The reason given on the most recent negative ack (<c>POST /api/inventory/instances/{id}/spawn-failed</c>)
    /// against this row, if any. Added in review round 1 (B-3) so the undeliverable queue shows *why* a
    /// delivery failed, not just that it did — see <c>ItemInstance.RecordSpawnFailure</c>.
    /// </summary>
    public string? LastSpawnFailureReason { get; init; }

    public DateTimeOffset? LastSpawnFailureAt { get; init; }

    /// <summary>How many times a negative ack has been reported against this row — distinct from <see cref="DeliveryAttempts"/>, which counts how many times it was served.</summary>
    public required int SpawnFailureCount { get; init; }

    public required DateTimeOffset RegisteredAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public static ItemInstanceDto Create(ItemInstance source) => new()
    {
        InstanceId = source.Id.Value,
        ItemId = source.ItemId.Value,
        Revision = source.Revision,
        DisplayNameOverride = source.DisplayNameOverride,
        Parent = ItemParentDto.Create(source.Parent),
        Durability = source.Durability,
        Ammo = source.Ammo,
        Attributes = source.Attributes.Values,
        Origin = source.Origin.ToString(),
        OriginRefModule = source.OriginRef?.Module,
        OriginRefId = source.OriginRef?.Id,
        PendingSpawn = source.PendingSpawn,
        DeliveryAttempts = source.DeliveryAttempts,
        LastSpawnFailureReason = source.LastSpawnFailureReason?.ToString(),
        LastSpawnFailureAt = source.LastSpawnFailureAt,
        SpawnFailureCount = source.SpawnFailureCount,
        RegisteredAt = source.RegisteredAt,
        UpdatedAt = source.UpdatedAt,
    };
}

/// <summary>The read-side union over an instance's parent-shaped fields — see <see cref="ItemParent"/>.</summary>
public sealed record ItemParentDto
{
    public required string Kind { get; init; }

    public Guid? CharacterId { get; init; }

    public Guid? ContainerInstanceId { get; init; }

    public string? Slot { get; init; }

    public WorldTransformDto? Transform { get; init; }

    public static ItemParentDto Create(ItemParent source) => new()
    {
        Kind = source.Kind.ToString(),
        CharacterId = source.CharacterId?.Value,
        ContainerInstanceId = source.ContainerInstanceId?.Value,
        Slot = source.Slot,
        Transform = source.Transform is null ? null : WorldTransformDto.Create(source.Transform),
    };
}

/// <summary>Only meaningful when <see cref="ItemParentDto.Kind"/> is <c>World</c>.</summary>
public sealed record WorldTransformDto
{
    public required WorldVector3Dto Position { get; init; }

    public required WorldVector3Dto Rotation { get; init; }

    public static WorldTransformDto Create(WorldTransform source) => new()
    {
        Position = WorldVector3Dto.Create(source.Position),
        Rotation = WorldVector3Dto.Create(source.Rotation),
    };
}

public sealed record WorldVector3Dto
{
    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Z { get; init; }

    public static WorldVector3Dto Create(WorldVector3 source) => new()
    {
        X = source.X,
        Y = source.Y,
        Z = source.Z,
    };
}
