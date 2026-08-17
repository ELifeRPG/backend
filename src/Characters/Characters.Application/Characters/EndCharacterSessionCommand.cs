using ELifeRPG.Characters.Application.Common;

namespace ELifeRPG.Characters.Application.Characters;

public union EndCharacterSessionResult(EndCharacterSessionResult.Ended, EndCharacterSessionResult.CharacterNotFound)
{
    public record Ended;

    public record CharacterNotFound;
}

public sealed record EndCharacterSessionCommand(CharacterId CharacterId) : IRequest<EndCharacterSessionResult>;

public sealed class EndCharacterSessionHandler(ICharacterRepository characterRepository)
    : IRequestHandler<EndCharacterSessionCommand, EndCharacterSessionResult>
{
    public async ValueTask<EndCharacterSessionResult> Handle(EndCharacterSessionCommand request, CancellationToken cancellationToken)
    {
        var character = await characterRepository.FindByIdAsync(request.CharacterId, cancellationToken);
        if (character is null)
        {
            return new EndCharacterSessionResult.CharacterNotFound();
        }

        var domainEvent = character.EndSession();

        characterRepository.Append(request.CharacterId, domainEvent);
        await characterRepository.SaveChangesAsync(cancellationToken);

        return new EndCharacterSessionResult.Ended();
    }
}
