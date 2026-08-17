using System.Runtime.CompilerServices;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Companies.Domain;

[assembly: InternalsVisibleTo("ELifeRPG.Shops.Application")]

namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// Resolves whether a character may commit a transaction (withdraw/transfer-out) on a bank account,
/// shared by WithdrawHandler and TransferHandler. Lives here rather than on the BankAccount aggregate
/// because the Corporate branch needs a cross-module query into Companies.Application, which Domain
/// may never do — see ARCHITECTURE.md §9e.
/// </summary>
internal static class BankAccountAuthorization
{
    public static async ValueTask<bool> IsAuthorizedAsync(
        BankAccount bankAccount,
        CharacterId actingCharacterId,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (bankAccount.Type == BankAccountType.Personal)
        {
            return bankAccount.OwnerCharacterId == actingCharacterId;
        }

        var permissionsLookup = await mediator.Send(
            new CompanyMemberPermissionsQuery(bankAccount.OwnerCompanyId!.Value, actingCharacterId),
            cancellationToken);

        return permissionsLookup is CompanyMemberPermissionsResult.Found found
            && found.Permissions.HasFlag(CompanyPermissions.ManageFinances);
    }
}
