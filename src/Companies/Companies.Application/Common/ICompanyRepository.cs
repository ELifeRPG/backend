using ELifeRPG.Companies.Domain.Events;

namespace ELifeRPG.Companies.Application.Common;

public interface ICompanyRepository
{
    ValueTask<Company?> FindByIdAsync(CompanyId companyId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Company>> FindAllAsync(CancellationToken cancellationToken);

    void StartStream(Company company, CompanyCreated domainEvent);

    /// <summary>Appends an event to an already-started stream — same pattern as IBankAccountRepository.Append. See ARCHITECTURE.md §9e.</summary>
    void Append<TEvent>(CompanyId companyId, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
