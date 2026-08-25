namespace ELifeRPG.Characters.Api.Characters;

public sealed record CharacterDto
{
    public required Guid CharacterId { get; init; }

    public required string Name { get; init; }

    public required Guid CurrentServerId { get; init; }

    public required bool SessionActive { get; init; }

    public DateTimeOffset? SessionStartedAt { get; init; }

    public DateTimeOffset? SessionEndedAt { get; init; }

    public static CharacterDto Create(Character source) => new()
    {
        CharacterId = source.Id.Value,
        Name = source.Name,
        CurrentServerId = source.CurrentServerId.Value,
        SessionActive = source.SessionActive,
        SessionStartedAt = source.SessionStartedAt,
        SessionEndedAt = source.SessionEndedAt,
    };

    public static CharacterDto Create(CreateCharacterResult.Created source, string name, GameServerId currentServerId) => new()
    {
        CharacterId = source.CharacterId.Value,
        Name = name,
        CurrentServerId = currentServerId.Value,
        SessionActive = false,
    };
}
