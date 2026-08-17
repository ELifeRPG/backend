using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Companies.Infrastructure.Common;

/// <summary>
/// Holds one session for this repository instance's lifetime — same reasoning as
/// MartenCharacterRepository/MartenBankAccountRepository. CreateCompanyHandler's StartStream +
/// Append (company creation + founder membership) both go through this same session, committing
/// atomically in one SaveChangesAsync.
/// </summary>
public sealed class MartenCompanyRepository : ICompanyRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;

    public MartenCompanyRepository(ICompaniesStore store, ICurrentGameServer currentGameServer)
    {
        _session = store.LightweightSession(currentGameServer.ClientId);
    }

    /// <summary>
    /// Used only by MartenCompanyRepositoryFactory for cross-module atomic writes — the session is
    /// already bound to a shared transaction the caller owns. Intentionally never disposed by this
    /// class in that path; see Global Constraints in
    /// docs/superpowers/plans/2026-08-15-cross-module-atomic-writes.md.
    /// </summary>
    internal MartenCompanyRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async ValueTask<Company?> FindByIdAsync(CompanyId companyId, CancellationToken cancellationToken)
        => await _session.LoadAsync<Company>(companyId, cancellationToken);

    public async ValueTask<IReadOnlyList<Company>> FindAllAsync(CancellationToken cancellationToken)
        => await _session.Query<Company>().ToListAsync(cancellationToken);

    public void StartStream(Company company, CompanyCreated domainEvent)
        => _session.Events.StartStream<Company>(company.Id.Value, domainEvent);

    public void Append<TEvent>(CompanyId companyId, TEvent domainEvent) where TEvent : notnull
        => _session.Events.Append(companyId.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => await _session.SaveChangesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
