using ELifeRPG.Companies.Application.Common;

namespace ELifeRPG.Companies.Application.Companies;

public sealed record CompaniesQuery : IRequest<IReadOnlyList<Company>>;

public sealed class CompaniesQueryHandler(ICompanyRepository companyRepository) : IRequestHandler<CompaniesQuery, IReadOnlyList<Company>>
{
    public async ValueTask<IReadOnlyList<Company>> Handle(CompaniesQuery request, CancellationToken cancellationToken)
        => await companyRepository.FindAllAsync(cancellationToken);
}
