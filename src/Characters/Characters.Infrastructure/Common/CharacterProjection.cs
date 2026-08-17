using ELifeRPG.Characters.Domain;
using ELifeRPG.Characters.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten.Events.Aggregation;

namespace ELifeRPG.Characters.Infrastructure.Common;

public sealed partial class CharacterProjection : SingleStreamProjection<Character, CharacterId>
{
    public static Character Create(CharacterCreated domainEvent) => Character.Create(domainEvent);

    public void Apply(Character character, CharacterSessionStarted domainEvent) => character.Apply(domainEvent);

    public void Apply(Character character, CharacterSessionEnded domainEvent) => character.Apply(domainEvent);
}
