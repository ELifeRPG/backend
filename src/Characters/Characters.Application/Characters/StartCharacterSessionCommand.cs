using ELifeRPG.Characters.Application.Common;

namespace ELifeRPG.Characters.Application.Characters;

public union StartCharacterSessionResult(StartCharacterSessionResult.Started, StartCharacterSessionResult.CharacterNotFound)
{
    public record Started;

    public record CharacterNotFound;
}

public sealed record StartCharacterSessionCommand(CharacterId CharacterId) : IRequest<StartCharacterSessionResult>;

public sealed class StartCharacterSessionHandler(ICharacterRepository characterRepository)
    : IRequestHandler<StartCharacterSessionCommand, StartCharacterSessionResult>
{
    public async ValueTask<StartCharacterSessionResult> Handle(StartCharacterSessionCommand request, CancellationToken cancellationToken)
    {
        var character = await characterRepository.FindByIdAsync(request.CharacterId, cancellationToken);
        if (character is null)
        {
            return new StartCharacterSessionResult.CharacterNotFound();
        }

        var domainEvent = character.StartSession();

        characterRepository.Append(request.CharacterId, domainEvent);
        await characterRepository.SaveChangesAsync(cancellationToken);

        return new StartCharacterSessionResult.Started();
    }
}
