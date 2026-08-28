using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Domain.Events;
using ELifeRPG.Shops.Domain.Exceptions;
using Marten;
using Npgsql;

namespace ELifeRPG.Shops.Infrastructure.Common;

public sealed class MartenShopListingRepository : IShopListingRepository, IAsyncDisposable
{
    private readonly IDocumentSession _session;
    private readonly NpgsqlTransaction? _crossModuleTransaction;

    public MartenShopListingRepository(IShopsStore store)
    {
        _session = store.LightweightSession();
    }

    /// <summary>
    /// Used only by MartenShopListingParticipant for cross-module atomic writes — the session
    /// is already bound to a shared transaction the caller owns; `crossModuleTransaction` is the
    /// same raw transaction, needed by ReserveStockAsync to take a Postgres row lock (Marten's own
    /// version-checked append machinery does not work on a ForTransaction-bound session). Intentionally
    /// never disposed by this class in that path; see the Global Constraints section of this plan.
    /// </summary>
    internal MartenShopListingRepository(IDocumentSession session, NpgsqlTransaction crossModuleTransaction)
    {
        _session = session;
        _crossModuleTransaction = crossModuleTransaction;
    }

    public async ValueTask<ShopListing?> FindByIdAsync(ShopListingId listingId, CancellationToken cancellationToken)
        => await _session.LoadAsync<ShopListing>(listingId, cancellationToken);

    public async ValueTask<IReadOnlyList<ShopListing>> FindByShopIdAsync(ShopId shopId, CancellationToken cancellationToken)
        => await _session.Query<ShopListing>()
            .Where(x => x.ShopId.Value == shopId.Value)
            .ToListAsync(cancellationToken);

    public void StartStream(ShopListing listing, ListingCreated domainEvent)
        => _session.Events.StartStream<ShopListing>(listing.Id.Value, domainEvent);

    public void Append<TEvent>(ShopListingId listingId, TEvent domainEvent) where TEvent : notnull
        => _session.Events.Append(listingId.Value, domainEvent);

    public async ValueTask<ShopListing> ReserveStockAsync(ShopListingId listingId, int quantity, CancellationToken cancellationToken)
    {
        if (_crossModuleTransaction is not null)
        {
            // Postgres-native row lock — held until the shared transaction commits or rolls back —
            // stands in for Marten's optimistic concurrency, which doesn't work here (verified via
            // the Task 1/1b spikes referenced above; syntax mirrors
            // tests/Shops.IntegrationTests/CrossModuleRowLockSpikeTests.cs exactly). Exact doc-table
            // name confirmed via Task 1c: shops.mt_doc_shoplisting. The table's primary key is now
            // `id` alone (tenancy removed), so a single-column predicate is the index lookup — see
            // ARCHITECTURE.md §9e gotcha 9.
            var connection = _crossModuleTransaction.Connection;
            await using var lockCommand = connection!.CreateCommand();
            lockCommand.Transaction = _crossModuleTransaction;
            lockCommand.CommandText = "SELECT id FROM shops.mt_doc_shoplisting WHERE id = @id FOR UPDATE";
            lockCommand.Parameters.AddWithValue("@id", listingId.Value);
            var lockedId = await lockCommand.ExecuteScalarAsync(cancellationToken);
            if (lockedId is null)
            {
                throw new InvalidOperationException($"Listing {listingId} not found.");
            }

            var listing = await _session.LoadAsync<ShopListing>(listingId, cancellationToken)
                ?? throw new InvalidOperationException($"Listing {listingId} locked but not found — should be impossible.");
            var domainEvent = listing.Purchase(quantity);
            _session.Events.Append(listingId.Value, domainEvent); // plain, unversioned — safe, row is locked
            return listing;
        }

        var stream = await _session.Events.FetchForWriting<ShopListing>(listingId.Value, cancellationToken);
        var streamDomainEvent = stream.Aggregate.Purchase(quantity);
        stream.AppendOne(streamDomainEvent);
        return stream.Aggregate;
    }

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.SaveChangesAsync(cancellationToken);
        }
        catch (JasperFx.ConcurrencyException)
        {
            // Only reachable via the non-cross-module (FetchForWriting) path — the cross-module path's
            // row lock (see ReserveStockAsync) already serializes access before this point.
            throw new ListingPurchaseConflictException("Another purchase already committed against this listing.");
        }
    }

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();
}
