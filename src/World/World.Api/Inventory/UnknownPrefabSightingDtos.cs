using ELifeRPG.World.Domain.Inventory;

namespace ELifeRPG.World.Api.Inventory;

/// <summary>Wire shape for one reported sighting — see <see cref="ELifeRPG.World.Application.Inventory.UnknownPrefabSightingRequest"/>. Every field is bounded by <c>WorldModule.TryParseRecordUnknownPrefabSightingsCommand</c> before it ever reaches a command.</summary>
public sealed record UnknownPrefabSightingRequestDto
{
    public required string PrefabClassName { get; init; }

    public required int Count { get; init; }

    public required DateTimeOffset FirstSeenAt { get; init; }

    public string? SampleContext { get; init; }
}

/// <summary><c>POST /api/inventory/unknown-prefabs</c>'s request body — batched, same shape convention as <see cref="AcknowledgeSpawnsRequestDto"/>.</summary>
public sealed record RecordUnknownPrefabSightingsRequestDto
{
    public required IReadOnlyList<UnknownPrefabSightingRequestDto> Sightings { get; init; }
}

/// <summary>One row of <c>GET /api/inventory/unknown-prefabs</c>'s staff promotion queue.</summary>
public sealed record UnknownPrefabSightingDto
{
    public required string PrefabClassName { get; init; }

    public required int Count { get; init; }

    public required DateTimeOffset FirstSeenAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public string? SampleContext { get; init; }

    public static UnknownPrefabSightingDto Create(UnknownPrefabSighting source) => new()
    {
        PrefabClassName = source.PrefabClassName,
        Count = source.Count,
        FirstSeenAt = source.FirstSeenAt,
        LastSeenAt = source.LastSeenAt,
        SampleContext = source.SampleContext,
    };
}
