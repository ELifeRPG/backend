using ELifeRPG.Banking.Application.Common;
using ELifeRPG.Companies.Application.Companies;

namespace ELifeRPG.Banking.Application.BankAccounts;

/// <summary>
/// The accounts a character may withdraw from: their personal accounts, plus the accounts of every
/// company in which their position includes BankAccountAuthorization.RequiredCompanyPermission —
/// the same rule the bookings use.
/// </summary>
public sealed record BankAccountsManagedByCharacterQuery(CharacterId CharacterId)
    : IRequest<IReadOnlyList<BankAccountsManagedByCharacterQuery.ManagedBankAccount>>
{
    /// <summary>CompanyName is set for company accounts and null for personal ones.</summary>
    public sealed record ManagedBankAccount(BankAccount BankAccount, string? CompanyName);
}

public sealed class BankAccountsManagedByCharacterHandler(IBankAccountRepository bankAccountRepository, IMediator mediator)
    : IRequestHandler<BankAccountsManagedByCharacterQuery, IReadOnlyList<BankAccountsManagedByCharacterQuery.ManagedBankAccount>>
{
    public async ValueTask<IReadOnlyList<BankAccountsManagedByCharacterQuery.ManagedBankAccount>> Handle(
        BankAccountsManagedByCharacterQuery request,
        CancellationToken cancellationToken)
    {
        var personalAccounts = await bankAccountRepository.FindByCharacterIdAsync(request.CharacterId, cancellationToken);

        var companies = await mediator.Send(
            new CompaniesWithMemberPermissionQuery(request.CharacterId, BankAccountAuthorization.RequiredCompanyPermission),
            cancellationToken);
        var companyNames = companies.ToDictionary(x => x.CompanyId, x => x.Name);
        var companyAccounts = await bankAccountRepository.FindByCompanyIdsAsync(companyNames.Keys, cancellationToken);

        return personalAccounts
            .Select(x => new BankAccountsManagedByCharacterQuery.ManagedBankAccount(x, null))
            .Concat(companyAccounts.Select(x =>
                new BankAccountsManagedByCharacterQuery.ManagedBankAccount(x, companyNames[x.OwnerCompanyId!.Value])))
            .ToList();
    }
}
