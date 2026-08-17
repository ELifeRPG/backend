namespace ELifeRPG.Companies.Domain;

/// <summary>
/// Ported from the legacy domain's flags shape. `ManageMembers` is enforced within Companies itself by
/// CompanyMemberAuthorization, gating the company-applications list/confirm/accept/deny actions — see
/// Companies.Application/Common/CompanyMemberAuthorization.cs. `AddMember`/company-management actions
/// (creating a company, adding a member directly) still have no enforcement, matching legacy, which
/// modeled this but never wired an authorization check there either. ManageFinances is enforced
/// cross-module: Banking.Application's BankAccountAuthorization checks it before allowing a
/// withdraw/transfer on a Corporate BankAccount — see ARCHITECTURE.md §9e. ManageShops is enforced the
/// same cross-module way, by Shops.Application's ShopAuthorization, before allowing listing management
/// on a Corporate Shop.
/// </summary>
[Flags]
public enum CompanyPermissions
{
    None = 1,
    ManageCompany = None << 1,
    ManageMembers = ManageCompany << 1,
    ManageWages = ManageMembers << 1,
    ManageFinances = ManageWages << 1,
    ManageShops = ManageFinances << 1,
}
