using ELifeRPG.Characters.Domain.Events;

namespace ELifeRPG.Characters.Domain;

public class Character
{
    public CharacterId Id { get; private set; }

    public AccountId AccountId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public decimal Cash { get; private set; }

    public bool SessionActive { get; private set; }

    public DateTimeOffset? SessionStartedAt { get; private set; }

    public DateTimeOffset? SessionEndedAt { get; private set; }

    public static Character Create(CharacterCreated domainEvent)
    {
        var character = new Character();
        character.Apply(domainEvent);
        return character;
    }

    // No guard against calling this while a session is already active: a stale "active" flag left
    // over from an ungraceful gameserver crash/restart (nothing yet ends it in that case — see
    // ARCHITECTURE.md/MIGRATION.md notes on character sessions) would otherwise permanently block
    // that character from ever selecting again. Starting again just supersedes it.
    public CharacterSessionStarted StartSession()
    {
        var domainEvent = new CharacterSessionStarted(Id, DateTimeOffset.UtcNow);
        Apply(domainEvent);
        return domainEvent;
    }

    public CharacterSessionEnded EndSession()
    {
        var domainEvent = new CharacterSessionEnded(Id, DateTimeOffset.UtcNow);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(CharacterCreated domainEvent)
    {
        Id = domainEvent.Id;
        AccountId = domainEvent.AccountId;
        Name = domainEvent.Name;
    }

    public void Apply(CharacterSessionStarted domainEvent)
    {
        SessionActive = true;
        SessionStartedAt = domainEvent.StartedAt;
        SessionEndedAt = null;
    }

    public void Apply(CharacterSessionEnded domainEvent)
    {
        SessionActive = false;
        SessionEndedAt = domainEvent.EndedAt;
    }
}
