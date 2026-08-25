using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union DenyApplicationResult(
    DenyApplicationResult.Denied,
    DenyApplicationResult.CompanyNotFound,
    DenyApplicationResult.NotAuthorized,
    DenyApplicationResult.ApplicationNotFound,
    DenyApplicationResult.InvalidState,
    DenyApplicationResult.ConcurrentModification)
{
    public record Denied;

    public record CompanyNotFound;

    public record NotAuthorized;

    public record ApplicationNotFound;

    public record InvalidState;

    public record ConcurrentModification;
}

public sealed record DenyApplicationCommand(CompanyId CompanyId, CompanyApplicationId ApplicationId, CharacterId ActingCharacterId)
    : IRequest<DenyApplicationResult>;

public sealed class DenyApplicationHandler(ICompanyRepository companyRepository)
    : IRequestHandler<DenyApplicationCommand, DenyApplicationResult>
{
    public async ValueTask<DenyApplicationResult> Handle(DenyApplicationCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FetchForUpdateAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new DenyApplicationResult.CompanyNotFound();
        }

        if (!CompanyMemberAuthorization.CanManageMembers(company, request.ActingCharacterId))
        {
            return new DenyApplicationResult.NotAuthorized();
        }

        try
        {
            var domainEvent = company.DenyApplication(request.ApplicationId);
            companyRepository.Append(request.CompanyId, domainEvent);
        }
        catch (ApplicationNotFoundException)
        {
            return new DenyApplicationResult.ApplicationNotFound();
        }
        catch (InvalidApplicationStateException)
        {
            return new DenyApplicationResult.InvalidState();
        }

        try
        {
            await companyRepository.SaveChangesAsync(cancellationToken);
        }
        catch (CompanyConcurrencyException)
        {
            return new DenyApplicationResult.ConcurrentModification();
        }

        return new DenyApplicationResult.Denied();
    }
}
