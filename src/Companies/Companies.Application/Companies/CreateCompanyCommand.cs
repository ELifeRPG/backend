using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union CreateCompanyResult(CreateCompanyResult.Created, CreateCompanyResult.FounderNotFound, CreateCompanyResult.ConcurrentModification)
{
    public record Created(CompanyId CompanyId);

    public record FounderNotFound;

    public record ConcurrentModification;
}

public sealed record CreateCompanyCommand(string Name, CharacterId FounderCharacterId) : IRequest<CreateCompanyResult>;

public sealed class CreateCompanyHandler(ICompanyRepository companyRepository, IMediator mediator)
    : IRequestHandler<CreateCompanyCommand, CreateCompanyResult>
{
    public async ValueTask<CreateCompanyResult> Handle(CreateCompanyCommand request, CancellationToken cancellationToken)
    {
        var characterLookup = await mediator.Send(new CharacterLookupQuery(request.FounderCharacterId), cancellationToken);
        if (characterLookup is CharacterLookupResult.NotFound)
        {
            return new CreateCompanyResult.FounderNotFound();
        }

        var companyId = new CompanyId(Guid.NewGuid());
        var ownerPositionId = new CompanyPositionId(Guid.NewGuid());
        var defaultPositionId = new CompanyPositionId(Guid.NewGuid());
        var createdEvent = new CompanyCreated(companyId, request.Name, ownerPositionId, defaultPositionId);
        var company = Company.Create(createdEvent);

        // The founder automatically becomes the company's first member, in the "Owner" position
        // (full permissions) — not the default "Rookie" a later AddMemberCommand call would pick.
        var memberAddedEvent = company.AddMember(request.FounderCharacterId, ownerPositionId);

        companyRepository.StartStream(company, createdEvent);
        companyRepository.Append(companyId, memberAddedEvent);

        try
        {
            await companyRepository.SaveChangesAsync(cancellationToken);
        }
        catch (CompanyConcurrencyException)
        {
            return new CreateCompanyResult.ConcurrentModification();
        }

        return new CreateCompanyResult.Created(companyId);
    }
}
