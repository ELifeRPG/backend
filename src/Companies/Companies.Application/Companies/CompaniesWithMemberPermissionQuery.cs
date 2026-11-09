using ELifeRPG.Companies.Application.Common;

namespace ELifeRPG.Companies.Application.Companies;

/// <summary>
/// The reverse of CompanyMemberPermissionsQuery: every company in which the character's current
/// position includes the given permission. Exposes only the id and name, like CompanyLookupQuery —
/// see ARCHITECTURE.md §9e.
/// </summary>
public sealed record CompaniesWithMemberPermissionQuery(CharacterId CharacterId, CompanyPermissions Permission)
    : IRequest<IReadOnlyList<CompaniesWithMemberPermissionQuery.Match>>
{
    public sealed record Match(CompanyId CompanyId, string Name);
}

public sealed class CompaniesWithMemberPermissionHandler(ICompanyRepository companyRepository)
    : IRequestHandler<CompaniesWithMemberPermissionQuery, IReadOnlyList<CompaniesWithMemberPermissionQuery.Match>>
{
    public async ValueTask<IReadOnlyList<CompaniesWithMemberPermissionQuery.Match>> Handle(
        CompaniesWithMemberPermissionQuery request,
        CancellationToken cancellationToken)
    {
        var companies = await companyRepository.FindByMemberAsync(request.CharacterId, cancellationToken);

        return companies
            .Where(company =>
            {
                var membership = company.Memberships.Single(x => x.CharacterId == request.CharacterId);
                var position = company.Positions.Single(x => x.Id == membership.PositionId);
                return position.Permissions.HasFlag(request.Permission);
            })
            .Select(company => new CompaniesWithMemberPermissionQuery.Match(company.Id, company.Name))
            .ToList();
    }
}
