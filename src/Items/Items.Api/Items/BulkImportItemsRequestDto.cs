namespace ELifeRPG.Items.Api.Items;

public sealed record BulkImportItemDto
{
    public required string PrefabClassName { get; init; }

    /// <summary>Optional — defaults to the prefab class name, so a raw prefab dump imports as-is.</summary>
    public string? DisplayName { get; init; }

    /// <summary>"Despawns" (default) or "Persistent" — see CreateItemRequestDto.Persistence.</summary>
    public string? Persistence { get; init; }

    public BulkImportItem ToCommandItem(ItemPersistence persistence) => new(PrefabClassName, DisplayName, persistence);
}

public sealed record BulkImportItemsRequestDto
{
    public required IReadOnlyList<BulkImportItemDto> Items { get; init; }

    public BulkImportItemsCommand ToCommand(IReadOnlyList<ItemPersistence> persistence)
        => new(Items.Select((x, i) => x.ToCommandItem(persistence[i])).ToList());
}

public sealed record BulkImportItemResultDto
{
    public required string PrefabClassName { get; init; }

    public required Guid ItemId { get; init; }

    /// <summary>False when the prefab was already in the catalog and was left untouched.</summary>
    public required bool Created { get; init; }

    public static BulkImportItemResultDto Create(BulkImportItemResult source) => new()
    {
        PrefabClassName = source.PrefabClassName,
        ItemId = source.ItemId.Value,
        Created = source.Created,
    };
}

public sealed record BulkImportItemsResponseDto
{
    public required int Created { get; init; }

    public required int AlreadyPresent { get; init; }

    public required IReadOnlyList<BulkImportItemResultDto> Results { get; init; }

    public static BulkImportItemsResponseDto Create(IReadOnlyList<BulkImportItemResult> source) => new()
    {
        Created = source.Count(x => x.Created),
        AlreadyPresent = source.Count(x => !x.Created),
        Results = source.Select(BulkImportItemResultDto.Create).ToList(),
    };
}
