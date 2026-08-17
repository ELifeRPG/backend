namespace ELifeRPG.Characters.Domain.Events;

public sealed record CharacterCreated(CharacterId Id, AccountId AccountId, string Name);
