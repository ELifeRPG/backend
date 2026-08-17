namespace ELifeRPG.Characters.Domain.Events;

public sealed record CharacterSessionStarted(CharacterId Id, DateTimeOffset StartedAt);
