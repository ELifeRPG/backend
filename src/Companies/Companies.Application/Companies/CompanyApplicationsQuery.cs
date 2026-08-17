using ELifeRPG.Companies.Application.Common;

namespace ELifeRPG.Companies.Application.Companies;

public union CompanyApplicationsResult(CompanyApplicationsResult.Found, CompanyApplicationsResult.CompanyNotFound, CompanyApplicationsResult.NotAuthorized)
{
    public record Found(IReadOnlyList<CompanyApplication> Applications);

    public record CompanyNotFound;

    public record NotAuthorized;
}

public sealed record CompanyApplicationsQuery(CompanyId CompanyId, CharacterId ActingCharacterId) : IRequest<CompanyApplicationsResult>;

public sealed class CompanyApplicationsHandler(ICompanyRepository companyRepository)
    : IRequestHandler<CompanyApplicationsQuery, CompanyApplicationsResult>
{
    public async ValueTask<CompanyApplicationsResult> Handle(CompanyApplicationsQuery request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new CompanyApplicationsResult.CompanyNotFound();
        }

        if (!CompanyMemberAuthorization.CanManageMembers(company, request.ActingCharacterId))
        {
            return new CompanyApplicationsResult.NotAuthorized();
        }

        return new CompanyApplicationsResult.Found(company.Applications);
    }
}
