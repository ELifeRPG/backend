using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Companies.Domain;

namespace ELifeRPG.Shops.Application.Common;

/// <summary>
/// Resolves whether a character may manage a shop's listings, shared by AddListingHandler/
/// UpdateListingHandler/RemoveListingHandler. Lives here rather than on the Shop aggregate because
/// the Corporate branch needs a cross-module query into Companies.Application, which Domain may
/// never do — see ARCHITECTURE.md §9e. Mirrors Banking.Application.Common.BankAccountAuthorization.
/// </summary>
internal static class ShopAuthorization
{
    public static async ValueTask<bool> CanManageAsync(
        Shop shop, CharacterId actingCharacterId, IMediator mediator, CancellationToken cancellationToken)
    {
        if (shop.OwnerType == ShopOwnerType.Personal)
        {
            return shop.OwnerCharacterId == actingCharacterId;
        }

        var permissionsLookup = await mediator.Send(
            new CompanyMemberPermissionsQuery(shop.OwnerCompanyId!.Value, actingCharacterId), cancellationToken);

        return permissionsLookup is CompanyMemberPermissionsResult.Found found
            && found.Permissions.HasFlag(CompanyPermissions.ManageShops);
    }
}
