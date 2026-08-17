namespace ELifeRPG.Items.Api.Items;

public sealed record ItemDto
{
    public required Guid ItemId { get; init; }

    public required string DisplayName { get; init; }

    public required string PrefabClassName { get; init; }

    public static ItemDto Create(Item source) => new()
    {
        ItemId = source.Id.Value,
        DisplayName = source.DisplayName,
        PrefabClassName = source.PrefabClassName,
    };
}
