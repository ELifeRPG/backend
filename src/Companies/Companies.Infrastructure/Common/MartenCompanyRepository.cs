using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Companies.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Marten;
using Npgsql;

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
    private readonly NpgsqlTransaction? _crossModuleTransaction;
    private readonly Dictionary<Guid, JasperFx.Events.IEventStream<Company>> _pendingStreams = new();

    public MartenCompanyRepository(ICompaniesStore store)
    {
        _session = store.LightweightSession();
    }

    /// <summary>
    /// Used only by MartenCompanyRepositoryFactory for cross-module atomic writes — same pattern as
    /// MartenBankAccountRepository's cross-module constructor (see Task 1 of this plan). Intentionally
    /// never disposed by this class in that path.
    /// </summary>
    internal MartenCompanyRepository(IDocumentSession session, NpgsqlTransaction crossModuleTransaction)
    {
        _session = session;
        _crossModuleTransaction = crossModuleTransaction;
    }

    public async ValueTask<Company?> FindByIdAsync(CompanyId companyId, CancellationToken cancellationToken)
        => await _session.LoadAsync<Company>(companyId, cancellationToken);

    public async ValueTask<Company?> FetchForUpdateAsync(CompanyId companyId, CancellationToken cancellationToken)
    {
        if (_crossModuleTransaction is not null)
        {
            // Row lock stands in for Marten's optimistic concurrency, which doesn't work on a
            // ForTransaction-bound session — same reasoning and syntax as
            // MartenBankAccountRepository.FetchForUpdateAsync. The doc table's primary key is now `id`
            // alone (tenancy removed), so a single-column predicate is the index lookup — see
            // ARCHITECTURE.md §9e gotcha 9.
            var connection = _crossModuleTransaction.Connection;
            await using var lockCommand = connection!.CreateCommand();
            lockCommand.Transaction = _crossModuleTransaction;
            lockCommand.CommandText = "SELECT id FROM companies.mt_doc_company WHERE id = @id FOR UPDATE";
            lockCommand.Parameters.AddWithValue("@id", companyId.Value);
            var lockedId = await lockCommand.ExecuteScalarAsync(cancellationToken);
            if (lockedId is null)
            {
                return null;
            }

            return await _session.LoadAsync<Company>(companyId, cancellationToken);
        }

        var stream = await _session.Events.FetchForWriting<Company>(companyId.Value, cancellationToken);
        if (stream.Aggregate is null)
        {
            return null;
        }

        _pendingStreams[companyId.Value] = stream;

        // Deliberately NOT `stream.Aggregate` itself: CompanyProjection is registered Inline, and
        // Marten's Inline commit re-applies this operation's newly appended event(s) onto that exact
        // instance to build the persisted snapshot. Every domain mutator on Company (AddMember,
        // SubmitApplication, ConfirmApplication, AcceptApplication, DenyApplication, IssueShares)
        // already self-applies the event it returns, so handing the caller `stream.Aggregate` to
        // mutate would double-apply that event: once here, once again by Marten at SaveChangesAsync.
        // Loading a second, independent copy via LoadAsync gives the caller something safe to mutate
        // without touching the instance Marten owns for the commit — same state as of this fetch,
        // since no writes have happened yet. This only stays decoupled from `stream.Aggregate` because
        // UseIdentityMapForAggregates is turned off for this store (see CompanyInfrastructureExtensions)
        // — with Marten's default (on), this LoadAsync would return that exact same instance instead of
        // a fresh one. See MartenBankAccountRepository.FetchForUpdateAsync for the identical pattern
        // (Task 1 of this plan).
        return await _session.LoadAsync<Company>(companyId, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<Company>> FindAllAsync(CancellationToken cancellationToken)
        => await _session.Query<Company>().ToListAsync(cancellationToken);

    public void StartStream(Company company, CompanyCreated domainEvent)
        => _session.Events.StartStream<Company>(company.Id.Value, domainEvent);

    public void Append<TEvent>(CompanyId companyId, TEvent domainEvent) where TEvent : notnull
    {
        if (_pendingStreams.TryGetValue(companyId.Value, out var stream))
        {
            stream.AppendOne(domainEvent);
            return;
        }

        // Reached for: (a) cross-module writes, where the row lock already serializes access, so a
        // plain unversioned append is safe; (b) StartStream-adjacent appends in the same request that
        // never went through FetchForUpdateAsync (none exist today, but this keeps old callers safe).
        _session.Events.Append(companyId.Value, domainEvent);
    }

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.SaveChangesAsync(cancellationToken);
        }
        catch (JasperFx.ConcurrencyException)
        {
            throw new CompanyConcurrencyException("Another operation already committed against this company.");
        }
        finally
        {
            _pendingStreams.Clear();
        }
    }

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
