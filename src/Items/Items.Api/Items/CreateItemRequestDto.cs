namespace ELifeRPG.Items.Api.Items;

public sealed record CreateItemRequestDto
{
    public required string DisplayName { get; init; }

    public required string PrefabClassName { get; init; }

    public CreateItemCommand ToCommand() => new(DisplayName, PrefabClassName);
}
