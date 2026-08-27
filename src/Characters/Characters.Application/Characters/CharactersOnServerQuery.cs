using ELifeRPG.Characters.Application.Common;

namespace ELifeRPG.Characters.Application.Characters;

/// <summary>
/// Of these characters, which are currently on this gameserver? The World module's anti-dupe guard:
/// a snapshot that claims to describe a character who is on a different server is rejected, which is
/// what stops a fast server-hop from persisting the same inventory twice.
///
/// Batched deliberately — the guard runs on the snapshot write path, where resolving characters one
/// at a time would put N cross-module round trips in front of every batch.
///
/// Answers from <see cref="Character.CurrentServerId"/>, not <c>SessionActive</c>: the session flag
/// is documented on the aggregate as unreliable after an ungraceful gameserver crash (nothing ends
/// the session in that case), whereas CurrentServerId changes only on travel.
/// </summary>
public sealed record CharactersOnServerQuery(GameServerId GameServerId, IReadOnlyList<CharacterId> CharacterIds)
    : IRequest<IReadOnlySet<CharacterId>>;

public sealed class CharactersOnServerHandler(ICharacterRepository characterRepository)
    : IRequestHandler<CharactersOnServerQuery, IReadOnlySet<CharacterId>>
{
    public async ValueTask<IReadOnlySet<CharacterId>> Handle(CharactersOnServerQuery request, CancellationToken cancellationToken)
    {
        if (request.CharacterIds.Count == 0)
        {
            return new HashSet<CharacterId>();
        }

        var characters = await characterRepository.FindByIdsAsync(request.CharacterIds, cancellationToken);

        // Unknown characters are simply absent, so the caller treats "not on this server" and "no
        // such character" identically — both mean "do not accept this write".
        return characters
            .Where(x => x.CurrentServerId == request.GameServerId)
            .Select(x => x.Id)
            .ToHashSet();
    }
}
