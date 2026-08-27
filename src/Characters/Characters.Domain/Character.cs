using ELifeRPG.Characters.Domain.Events;

namespace ELifeRPG.Characters.Domain;

public class Character
{
    public CharacterId Id { get; private set; }

    public AccountId AccountId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Which server (map) this character is currently on. Set at creation and changed only by
    /// travel — a rare, auditable transition, unlike position, which is volatile and lives on
    /// CharacterPresence instead.
    ///
    /// This field was added to <c>CharacterCreated</c> on 2026-08-22. Events written before that
    /// date have no corresponding JSON property. The steady-state read path (Marten
    /// <c>ProjectionLifecycle.Inline</c> + <c>LoadAsync&lt;Character&gt;</c>) never replays those raw
    /// events — it reads the already-materialised snapshot document — so this is silently harmless
    /// today. But System.Text.Json binds a missing constructor argument to its default rather than
    /// throwing, so anything that genuinely replays a pre-migration stream (a projection rebuild,
    /// <c>AggregateStreamAsync</c>, async-daemon catch-up, restore-from-events) will silently produce
    /// <c>default(GameServerId)</c> (<c>Guid.Empty</c>) for it — no exception, no warning. Treat
    /// <c>CurrentServerId</c> on any character replayed from pre-2026-08-22 events as unset, not
    /// trustworthy.
    /// </summary>
    public GameServerId CurrentServerId { get; private set; }

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
        CurrentServerId = domainEvent.CurrentServerId;
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
