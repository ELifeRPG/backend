using ELifeRPG.Companies.Application.Common;

namespace ELifeRPG.Companies.Application.Companies;

/// <summary>
/// Second cross-module surface, alongside CompanyLookupQuery — lets another module (Banking) ask
/// "what can this character do in this company" without seeing the full Company aggregate or its
/// membership list. See ARCHITECTURE.md §9e.
/// </summary>
public union CompanyMemberPermissionsResult(CompanyMemberPermissionsResult.Found, CompanyMemberPermissionsResult.NotMember)
{
    public record Found(CompanyPermissions Permissions);

    public record NotMember;
}

public sealed record CompanyMemberPermissionsQuery(CompanyId CompanyId, CharacterId CharacterId) : IRequest<CompanyMemberPermissionsResult>;

public sealed class CompanyMemberPermissionsHandler(ICompanyRepository companyRepository)
    : IRequestHandler<CompanyMemberPermissionsQuery, CompanyMemberPermissionsResult>
{
    public async ValueTask<CompanyMemberPermissionsResult> Handle(CompanyMemberPermissionsQuery request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FindByIdAsync(request.CompanyId, cancellationToken);
        var membership = company?.Memberships.SingleOrDefault(x => x.CharacterId == request.CharacterId);
        if (membership is null)
        {
            return new CompanyMemberPermissionsResult.NotMember();
        }

        var position = company!.Positions.Single(x => x.Id == membership.PositionId);
        return new CompanyMemberPermissionsResult.Found(position.Permissions);
    }
}
