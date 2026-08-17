namespace ELifeRPG.Characters.Domain.Events;

public sealed record CharacterSessionEnded(CharacterId Id, DateTimeOffset EndedAt);
