using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union AcceptApplicationResult(
    AcceptApplicationResult.Accepted,
    AcceptApplicationResult.CompanyNotFound,
    AcceptApplicationResult.NotAuthorized,
    AcceptApplicationResult.ApplicationNotFound,
    AcceptApplicationResult.InvalidState,
    AcceptApplicationResult.AlreadyMember)
{
    public record Accepted;

    public record CompanyNotFound;

    public record NotAuthorized;

    public record ApplicationNotFound;

    public record InvalidState;

    public record AlreadyMember;
}

public sealed record AcceptApplicationCommand(CompanyId CompanyId, CompanyApplicationId ApplicationId, CharacterId ActingCharacterId)
    : IRequest<AcceptApplicationResult>;

public sealed class AcceptApplicationHandler(ICompanyRepository companyRepository)
    : IRequestHandler<AcceptApplicationCommand, AcceptApplicationResult>
{
    public async ValueTask<AcceptApplicationResult> Handle(AcceptApplicationCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new AcceptApplicationResult.CompanyNotFound();
        }

        if (!CompanyMemberAuthorization.CanManageMembers(company, request.ActingCharacterId))
        {
            return new AcceptApplicationResult.NotAuthorized();
        }

        try
        {
            var (acceptedEvent, memberAddedEvent) = company.AcceptApplication(request.ApplicationId);
            companyRepository.Append(request.CompanyId, acceptedEvent);
            companyRepository.Append(request.CompanyId, memberAddedEvent);
        }
        catch (ApplicationNotFoundException)
        {
            return new AcceptApplicationResult.ApplicationNotFound();
        }
        catch (InvalidApplicationStateException)
        {
            return new AcceptApplicationResult.InvalidState();
        }
        catch (AlreadyMemberException)
        {
            return new AcceptApplicationResult.AlreadyMember();
        }

        await companyRepository.SaveChangesAsync(cancellationToken);

        return new AcceptApplicationResult.Accepted();
    }
}
