using ELifeRPG.Companies.Domain.Events;

namespace ELifeRPG.Companies.Application.Common;

public interface ICompanyRepository
{
    ValueTask<Company?> FindByIdAsync(CompanyId companyId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the company for a subsequent Append + SaveChangesAsync, using Marten's optimistic
    /// concurrency (FetchForWriting) so a second writer against the same company is caught at
    /// SaveChangesAsync time instead of silently lost. Use this — not FindByIdAsync — whenever the
    /// caller is about to mutate the company and Append an event.
    /// </summary>
    ValueTask<Company?> FetchForUpdateAsync(CompanyId companyId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Company>> FindAllAsync(CancellationToken cancellationToken);

    void StartStream(Company company, CompanyCreated domainEvent);

    /// <summary>Appends an event to an already-started stream — same pattern as IBankAccountRepository.Append. See ARCHITECTURE.md §9e.</summary>
    void Append<TEvent>(CompanyId companyId, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
