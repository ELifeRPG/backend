using ELifeRPG.World.Application.Inventory;

namespace ELifeRPG.World.Api.Inventory;

/// <summary>Wire shape for one declared child — see <see cref="AckChildRequest"/>.</summary>
public sealed record AckChildRequestDto
{
    public required Guid ItemId { get; init; }

    public required string Slot { get; init; }
}

/// <summary>Wire shape for one ack entry — see <see cref="InstanceAckRequest"/>.</summary>
public sealed record InstanceAckRequestDto
{
    public required Guid InstanceId { get; init; }

    public IReadOnlyList<AckChildRequestDto> Children { get; init; } = [];
}

/// <summary><c>POST /api/inventory/acks</c>'s request body — batched, see the phase 1 task brief.</summary>
public sealed record AcknowledgeSpawnsRequestDto
{
    public required IReadOnlyList<InstanceAckRequestDto> Acks { get; init; }
}

/// <summary>
/// One resolved child on the response. <see cref="Outcome"/> is one of
/// Minted | ItemNotInCatalog | SlotItemMismatch. <see cref="InstanceId"/> is set only for
/// <c>Minted</c>; <see cref="ExistingItemId"/> only for <c>SlotItemMismatch</c> (review round 1, B-2) —
/// the slot was already minted for a different itemId than this ack declared, so the existing child's
/// id is deliberately withheld rather than silently handed back under the caller's wrong itemId.
/// </summary>
public sealed record AckedChildDto
{
    public required Guid ItemId { get; init; }

    public required string Slot { get; init; }

    public required string Outcome { get; init; }

    public Guid? InstanceId { get; init; }

    public Guid? ExistingItemId { get; init; }

    public static AckedChildDto Create(AckedChild source) => new()
    {
        ItemId = source.ItemId.Value,
        Slot = source.Slot,
        Outcome = source.Outcome switch
        {
            AckChildOutcome.Minted => "Minted",
            AckChildOutcome.ItemNotInCatalog => "ItemNotInCatalog",
            AckChildOutcome.SlotItemMismatch => "SlotItemMismatch",
        },
        InstanceId = source.Outcome is AckChildOutcome.Minted minted ? minted.InstanceId.Value : null,
        ExistingItemId = source.Outcome is AckChildOutcome.SlotItemMismatch mismatch ? mismatch.ExistingItemId.Value : null,
    };
}

/// <summary>One acked instance's result — <see cref="Outcome"/> is one of Cleared | AlreadyCleared | NotFound | WrongServer | RemovedByStaff.</summary>
public sealed record InstanceAckResponseDto
{
    public required Guid InstanceId { get; init; }

    public required string Outcome { get; init; }

    public IReadOnlyList<AckedChildDto> Children { get; init; } = [];

    public static InstanceAckResponseDto Create(InstanceAckOutcome source) => new()
    {
        InstanceId = source.InstanceId.Value,
        Outcome = source.Outcome switch
        {
            AckOutcome.Cleared => "Cleared",
            AckOutcome.AlreadyCleared => "AlreadyCleared",
            AckOutcome.NotFound => "NotFound",
            AckOutcome.WrongServer => "WrongServer",
            AckOutcome.RemovedByStaff => "RemovedByStaff",
        },
        Children = source.Outcome switch
        {
            AckOutcome.Cleared cleared => cleared.Children.Select(AckedChildDto.Create).ToList(),
            AckOutcome.AlreadyCleared alreadyCleared => alreadyCleared.Children.Select(AckedChildDto.Create).ToList(),
            _ => [],
        },
    };
}
