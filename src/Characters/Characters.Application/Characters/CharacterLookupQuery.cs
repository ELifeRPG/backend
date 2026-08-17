using ELifeRPG.Characters.Application.Common;

namespace ELifeRPG.Characters.Application.Characters;

/// <summary>
/// The only surface other modules should use to reference a Character — see ARCHITECTURE.md §9e.
/// Other modules should not reference Characters.Domain or Characters.Infrastructure directly;
/// dispatch this request/response pair via IMediator instead.
/// </summary>
public union CharacterLookupResult(CharacterLookupResult.Found, CharacterLookupResult.NotFound)
{
    public record Found(CharacterId CharacterId, AccountId AccountId);

    public record NotFound;
}

public sealed record CharacterLookupQuery(CharacterId CharacterId) : IRequest<CharacterLookupResult>;

public sealed class CharacterLookupHandler(ICharacterRepository characterRepository) : IRequestHandler<CharacterLookupQuery, CharacterLookupResult>
{
    public async ValueTask<CharacterLookupResult> Handle(CharacterLookupQuery request, CancellationToken cancellationToken)
    {
        var character = await characterRepository.FindByIdAsync(request.CharacterId, cancellationToken);

        return character is null
            ? new CharacterLookupResult.NotFound()
            : new CharacterLookupResult.Found(character.Id, character.AccountId);
    }
}
