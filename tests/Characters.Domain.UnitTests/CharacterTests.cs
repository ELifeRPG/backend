using ELifeRPG.Characters.Domain;
using ELifeRPG.Characters.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Characters.Domain.UnitTests;

public class CharacterTests
{
    [Fact]
    public void Create_SetsPropertiesFromEvent()
    {
        var characterId = new CharacterId(Guid.NewGuid());
        var accountId = new AccountId(Guid.NewGuid());
        var serverId = new GameServerId(Guid.NewGuid());
        var domainEvent = new CharacterCreated(characterId, accountId, "Alice", serverId);

        var character = Character.Create(domainEvent);

        Assert.Equal(characterId, character.Id);
        Assert.Equal(accountId, character.AccountId);
        Assert.Equal("Alice", character.Name);
        Assert.Equal(serverId, character.CurrentServerId);
    }

    [Fact]
    public void Apply_ReplayingCreated_ResultsInSamePropertiesAsCreate()
    {
        var characterId = new CharacterId(Guid.NewGuid());
        var accountId = new AccountId(Guid.NewGuid());
        var serverId = new GameServerId(Guid.NewGuid());
        var domainEvent = new CharacterCreated(characterId, accountId, "Bob", serverId);

        var character = new Character();
        character.Apply(domainEvent);

        Assert.Equal(characterId, character.Id);
        Assert.Equal(accountId, character.AccountId);
        Assert.Equal("Bob", character.Name);
        Assert.Equal(serverId, character.CurrentServerId);
    }

    [Fact]
    public void StartSession_SetsActiveAndClearsEndedAt()
    {
        var character = Character.Create(new CharacterCreated(new CharacterId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "Alice", new GameServerId(Guid.NewGuid())));

        var domainEvent = character.StartSession();

        Assert.True(character.SessionActive);
        Assert.Equal(domainEvent.StartedAt, character.SessionStartedAt);
        Assert.Null(character.SessionEndedAt);
    }

    [Fact]
    public void EndSession_ClearsActiveAndSetsEndedAt()
    {
        var character = Character.Create(new CharacterCreated(new CharacterId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "Alice", new GameServerId(Guid.NewGuid())));
        character.StartSession();

        var domainEvent = character.EndSession();

        Assert.False(character.SessionActive);
        Assert.Equal(domainEvent.EndedAt, character.SessionEndedAt);
    }

    [Fact]
    public void StartSession_CalledAgainWhileAlreadyActive_SupersedesInsteadOfThrowing()
    {
        // No gameserver-crash/restart cleanup exists yet (see StartSession's doc comment) — a stale
        // "active" flag left over from an ungraceful restart must not permanently block reselecting.
        var character = Character.Create(new CharacterCreated(new CharacterId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "Alice", new GameServerId(Guid.NewGuid())));
        character.StartSession();

        var exception = Record.Exception(() => character.StartSession());

        Assert.Null(exception);
        Assert.True(character.SessionActive);
    }
}
