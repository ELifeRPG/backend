using ELifeRPG.Characters.Application.Common;

namespace ELifeRPG.Characters.Application.Characters;

public sealed record CharactersQuery(AccountId AccountId) : IRequest<IReadOnlyList<Character>>;

public sealed class CharactersQueryHandler(ICharacterRepository characterRepository) : IRequestHandler<CharactersQuery, IReadOnlyList<Character>>
{
    public async ValueTask<IReadOnlyList<Character>> Handle(CharactersQuery request, CancellationToken cancellationToken)
        => await characterRepository.FindByAccountIdAsync(request.AccountId, cancellationToken);
}
