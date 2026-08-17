using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union SubmitApplicationResult(
    SubmitApplicationResult.Submitted,
    SubmitApplicationResult.CompanyNotFound,
    SubmitApplicationResult.CharacterNotFound,
    SubmitApplicationResult.AlreadyMember,
    SubmitApplicationResult.DuplicateApplication)
{
    public record Submitted(CompanyApplicationId ApplicationId);

    public record CompanyNotFound;

    public record CharacterNotFound;

    public record AlreadyMember;

    public record DuplicateApplication;
}

public sealed record SubmitApplicationCommand(CompanyId CompanyId, CharacterId CharacterId, string Message) : IRequest<SubmitApplicationResult>;

public sealed class SubmitApplicationHandler(ICompanyRepository companyRepository, IMediator mediator)
    : IRequestHandler<SubmitApplicationCommand, SubmitApplicationResult>
{
    public async ValueTask<SubmitApplicationResult> Handle(SubmitApplicationCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new SubmitApplicationResult.CompanyNotFound();
        }

        var characterLookup = await mediator.Send(new CharacterLookupQuery(request.CharacterId), cancellationToken);
        if (characterLookup is CharacterLookupResult.NotFound)
        {
            return new SubmitApplicationResult.CharacterNotFound();
        }

        ApplicationSubmitted domainEvent;
        try
        {
            domainEvent = company.SubmitApplication(request.CharacterId, request.Message);
        }
        catch (AlreadyMemberException)
        {
            return new SubmitApplicationResult.AlreadyMember();
        }
        catch (DuplicateApplicationException)
        {
            return new SubmitApplicationResult.DuplicateApplication();
        }

        companyRepository.Append(request.CompanyId, domainEvent);
        await companyRepository.SaveChangesAsync(cancellationToken);

        return new SubmitApplicationResult.Submitted(domainEvent.ApplicationId);
    }
}
