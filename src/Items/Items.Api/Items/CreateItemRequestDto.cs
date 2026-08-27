namespace ELifeRPG.Items.Api.Items;

public sealed record CreateItemRequestDto
{
    public required string DisplayName { get; init; }

    public required string PrefabClassName { get; init; }

    /// <summary>
    /// "Despawns" (default) or "Persistent". A string on the wire, parsed at the endpoint: no
    /// JsonStringEnumConverter is configured in this solution, so an enum-typed property here would
    /// only bind from its ordinal. Same convention as ShopEndpoints' ownerType.
    /// </summary>
    public string? Persistence { get; init; }

    public CreateItemCommand ToCommand(ItemPersistence persistence) => new(DisplayName, PrefabClassName, persistence);
}
