using ELifeRPG.Companies.Application.Common;

namespace ELifeRPG.Companies.Application.Companies;

/// <summary>
/// The only surface other modules should use to reference a Company — see ARCHITECTURE.md §9e.
/// Unlike CompanyDetailsQuery (this module's own read model, which returns the full Company
/// aggregate), this exposes only the minimal fields another module needs — matching
/// AccountLookupQuery/CharacterLookupQuery's shape, not CompanyDetailsQuery's.
/// </summary>
public union CompanyLookupResult(CompanyLookupResult.Found, CompanyLookupResult.NotFound)
{
    public record Found(CompanyId CompanyId, string Name);

    public record NotFound;
}

public sealed record CompanyLookupQuery(CompanyId CompanyId) : IRequest<CompanyLookupResult>;

public sealed class CompanyLookupHandler(ICompanyRepository companyRepository) : IRequestHandler<CompanyLookupQuery, CompanyLookupResult>
{
    public async ValueTask<CompanyLookupResult> Handle(CompanyLookupQuery request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        return company is null
            ? new CompanyLookupResult.NotFound()
            : new CompanyLookupResult.Found(company.Id, company.Name);
    }
}
