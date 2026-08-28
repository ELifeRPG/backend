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
///
/// Append only, and the bits are frozen. These values are persisted on <c>CompanyPosition.Permissions</c>
/// inside the inline-projected <c>Company</c> document, and every cross-module authorization check
/// (<c>BankAccountAuthorization</c>, <c>ShopAuthorization</c>, <c>CompanyMemberAuthorization</c>) is a
/// <c>HasFlag</c> against a stored value — so reassigning a bit silently regrants or revokes it on
/// every existing position. Only ever add a new member at the next free bit (64).
///
/// The values were written out as literals rather than the shift chain they used to be
/// (<c>ManageCompany = None &lt;&lt; 1</c>, …): same numbers, but a reviewer can now read a member's
/// stored bit without doing the arithmetic. <c>None = 1</c> is deliberate-by-inheritance, not a typo —
/// it is a real bit, so <c>HasFlag(None)</c> is a genuine test rather than the vacuously-true one it
/// would be at 0. The cost is that <c>default</c> is <c>0</c>, which names no member; that is the
/// legacy app's shape and is now frozen by the stored data, so it is left alone.
/// </summary>
[Flags]
public enum CompanyPermissions
{
    None = 1,
    ManageCompany = 2,
    ManageMembers = 4,
    ManageWages = 8,
    ManageFinances = 16,
    ManageShops = 32,
}
