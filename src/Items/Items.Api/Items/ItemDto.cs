namespace ELifeRPG.Items.Api.Items;

public sealed record ItemDto
{
    public required Guid ItemId { get; init; }

    public required string DisplayName { get; init; }

    public required string PrefabClassName { get; init; }

    /// <summary>Whether instances of this item despawn once dropped. See docs/world.md.</summary>
    public required string Persistence { get; init; }

    public static ItemDto Create(Item source) => new()
    {
        ItemId = source.Id.Value,
        DisplayName = source.DisplayName,
        PrefabClassName = source.PrefabClassName,
        Persistence = source.Persistence.ToString(),
    };
}

/// <summary>The catalog plus the stamp the Bridge compares to decide whether to re-fetch.</summary>
public sealed record ItemCatalogDto
{
    public required long CatalogVersion { get; init; }

    public required IReadOnlyList<ItemDto> Items { get; init; }

    public static ItemCatalogDto Create(ItemCatalog source) => new()
    {
        CatalogVersion = source.CatalogVersion,
        Items = source.Items.Select(ItemDto.Create).ToList(),
    };
}
