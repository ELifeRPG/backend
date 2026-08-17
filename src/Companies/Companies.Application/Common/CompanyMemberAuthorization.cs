namespace ELifeRPG.Companies.Application.Common;

/// <summary>
/// Checks whether a character can manage members/applications for a company, using an
/// already-loaded Company aggregate. Unlike CompanyMemberPermissionsQuery (the cross-module surface
/// other modules like Banking use — see ARCHITECTURE.md §9e), this stays in-process:
/// Companies.Application handlers already have the aggregate loaded, so a second mediator
/// round-trip would be redundant.
/// </summary>
internal static class CompanyMemberAuthorization
{
    public static bool CanManageMembers(Company company, CharacterId characterId)
    {
        var membership = company.Memberships.SingleOrDefault(x => x.CharacterId == characterId);
        if (membership is null)
        {
            return false;
        }

        var position = company.Positions.Single(x => x.Id == membership.PositionId);
        return position.Permissions.HasFlag(CompanyPermissions.ManageMembers);
    }
}
