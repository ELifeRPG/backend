using ELifeRPG.Companies.Application.Common;

namespace ELifeRPG.Companies.Application.Companies;

public union CompanyDetailsResult(CompanyDetailsResult.Found, CompanyDetailsResult.NotFound)
{
    public record Found(Company Company);

    public record NotFound;
}

public sealed record CompanyDetailsQuery(CompanyId CompanyId) : IRequest<CompanyDetailsResult>;

public sealed class CompanyDetailsHandler(ICompanyRepository companyRepository) : IRequestHandler<CompanyDetailsQuery, CompanyDetailsResult>
{
    public async ValueTask<CompanyDetailsResult> Handle(CompanyDetailsQuery request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        return company is null
            ? new CompanyDetailsResult.NotFound()
            : new CompanyDetailsResult.Found(company);
    }
}
