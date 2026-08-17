using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union AddMemberResult(AddMemberResult.Added, AddMemberResult.CompanyNotFound, AddMemberResult.CharacterNotFound, AddMemberResult.AlreadyMember)
{
    public record Added;

    public record CompanyNotFound;

    public record CharacterNotFound;

    public record AlreadyMember;
}

public sealed record AddMemberCommand(CompanyId CompanyId, CharacterId CharacterId) : IRequest<AddMemberResult>;

public sealed class AddMemberHandler(ICompanyRepository companyRepository, IMediator mediator) : IRequestHandler<AddMemberCommand, AddMemberResult>
{
    public async ValueTask<AddMemberResult> Handle(AddMemberCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new AddMemberResult.CompanyNotFound();
        }

        var characterLookup = await mediator.Send(new CharacterLookupQuery(request.CharacterId), cancellationToken);
        if (characterLookup is CharacterLookupResult.NotFound)
        {
            return new AddMemberResult.CharacterNotFound();
        }

        MemberAdded domainEvent;
        try
        {
            domainEvent = company.AddMember(request.CharacterId);
        }
        catch (AlreadyMemberException)
        {
            return new AddMemberResult.AlreadyMember();
        }

        companyRepository.Append(request.CompanyId, domainEvent);
        await companyRepository.SaveChangesAsync(cancellationToken);

        return new AddMemberResult.Added();
    }
}
