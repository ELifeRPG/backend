using ELifeRPG.Accounts.Application.Accounts;
using ELifeRPG.Characters.Application.Common;
using ELifeRPG.Characters.Domain.Events;

namespace ELifeRPG.Characters.Application.Characters;

public union CreateCharacterResult(CreateCharacterResult.Created, CreateCharacterResult.AccountNotFound, CreateCharacterResult.AccountLocked)
{
    public record Created(CharacterId CharacterId);

    public record AccountNotFound;

    public record AccountLocked;
}

public sealed record CreateCharacterCommand(AccountId AccountId, string Name) : IRequest<CreateCharacterResult>;

public sealed class CreateCharacterHandler(ICharacterRepository characterRepository, IMediator mediator)
    : IRequestHandler<CreateCharacterCommand, CreateCharacterResult>
{
    public async ValueTask<CreateCharacterResult> Handle(CreateCharacterCommand request, CancellationToken cancellationToken)
    {
        var accountLookup = await mediator.Send(new AccountLookupQuery(request.AccountId), cancellationToken);

        if (accountLookup is AccountLookupResult.NotFound)
        {
            return new CreateCharacterResult.AccountNotFound();
        }

        if (accountLookup is AccountLookupResult.Found { Status: ELifeRPG.Accounts.Domain.AccountStatus.Locked })
        {
            return new CreateCharacterResult.AccountLocked();
        }

        var characterId = new CharacterId(Guid.NewGuid());
        var domainEvent = new CharacterCreated(characterId, request.AccountId, request.Name);
        var character = Character.Create(domainEvent);

        characterRepository.StartStream(character, domainEvent);
        await characterRepository.SaveChangesAsync(cancellationToken);

        return new CreateCharacterResult.Created(characterId);
    }
}
