using ELifeRPG.Companies.Application.Companies;

namespace ELifeRPG.Banking.Api.Common;

/// <summary>Resolves the company name that BankAccountDto shows for company accounts.</summary>
internal static class CompanyNames
{
    public static async ValueTask<string?> FindAsync(BankAccount bankAccount, IMediator mediator, CancellationToken cancellationToken)
        => bankAccount.OwnerCompanyId is { } companyId
            ? await FindAsync(companyId, mediator, cancellationToken)
            : null;

    public static async ValueTask<string?> FindAsync(CompanyId companyId, IMediator mediator, CancellationToken cancellationToken)
        => await mediator.Send(new CompanyLookupQuery(companyId), cancellationToken) is CompanyLookupResult.Found found
            ? found.Name
            : null;
}
