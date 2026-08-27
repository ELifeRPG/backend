namespace ELifeRPG.World.Api.Inventory;

/// <summary><c>POST /api/inventory/instances/{instanceId}/spawn-failed</c>'s request body. <see cref="Reason"/> is one of InventoryFull | PrefabMissing | ContainerMissing | AdoptionUnsupported, verbatim.</summary>
public sealed record SpawnFailedRequestDto
{
    public required string Reason { get; init; }
}

/// <summary>The negative ack's response — <see cref="Outcome"/> is one of StillPending | Undeliverable.</summary>
public sealed record SpawnFailedResponseDto
{
    public required string Outcome { get; init; }
}
