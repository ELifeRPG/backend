using ELifeRPG.Items.Application.Common;
using ELifeRPG.Items.Domain;
using ELifeRPG.Items.Domain.Events;
using ELifeRPG.Items.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Marten;
using Npgsql;

namespace ELifeRPG.Items.Infrastructure.Common;

/// <summary>
/// Holds one session for this repository instance's lifetime — same reasoning as
/// MartenCompanyRepository. Hive model: the item catalog is shared across every gameserver, so the
/// session is untenanted (the parameterless LightweightSession() overload).
/// </summary>
public sealed class MartenItemRepository : IItemRepository, IAsyncDisposable
{
    private const string UniqueViolation = "23505";

    private readonly IDocumentSession _session;

    public MartenItemRepository(IItemsStore store)
    {
        _session = store.LightweightSession();
    }

    public async ValueTask<Item?> FindByIdAsync(ItemId itemId, CancellationToken cancellationToken)
        => await _session.LoadAsync<Item>(itemId, cancellationToken);

    public async ValueTask<IReadOnlyList<Item>> FindAllAsync(CancellationToken cancellationToken)
        => await _session.Query<Item>().ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<Item>> FindByIdsAsync(IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        // Compare the strongly-typed id itself, never `x.Id.Value`: Marten's LINQ provider rejects
        // the latter outright ("Marten can not (yet) deal with x.Id.Value"). Identity comparisons
        // take the strongly-typed id — ARCHITECTURE.md §9e gotcha 4.
        var ids = itemIds.ToArray();
        return await _session.Query<Item>().Where(x => x.Id.IsOneOf(ids)).ToListAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<Item>> FindByPrefabClassNamesAsync(
        IReadOnlyList<string> prefabClassNames,
        CancellationToken cancellationToken)
    {
        if (prefabClassNames.Count == 0)
        {
            return [];
        }

        var names = prefabClassNames.ToArray();
        return await _session.Query<Item>().Where(x => names.Contains(x.PrefabClassName)).ToListAsync(cancellationToken);
    }

    // The catalog is append-only — nothing is ever deleted or rewritten — so the raw event count is
    // a monotonic stand-in for a version. Cheap (an index-only count over a table with hundreds of
    // rows) and, unlike a hash of the contents, O(1) to compare on the Bridge's side.
    public async ValueTask<long> GetCatalogVersionAsync(CancellationToken cancellationToken)
        => await _session.Events.QueryAllRawEvents().CountAsync(cancellationToken);

    public void StartStream(Item item, ItemCreated domainEvent)
        => _session.Events.StartStream<Item>(item.Id.Value, domainEvent);

    public async ValueTask SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (FindUniqueViolation(exception) is { } violation)
        {
            // The unique index on PrefabClassName is the only thing standing between two concurrent
            // creates and an ambiguous prefab. Translate it here so handlers can map it onto a
            // result union case instead of it escaping as a 500 — see ARCHITECTURE.md §9e.
            throw new DuplicatePrefabClassNameException(ExtractPrefabClassName(violation));
        }
    }

    public async ValueTask DisposeAsync()
        => await _session.DisposeAsync();

    private static PostgresException? FindUniqueViolation(Exception exception) => exception switch
    {
        PostgresException { SqlState: UniqueViolation } postgres => postgres,
        AggregateException aggregate => aggregate.InnerExceptions.Select(FindUniqueViolation).FirstOrDefault(x => x is not null),
        { InnerException: { } inner } => FindUniqueViolation(inner),
        _ => null,
    };

    // Npgsql does not hand back the offending value, only the index that rejected it. The prefab
    // name is reported best-effort so the message stays useful; callers re-read to find the winner.
    private static string ExtractPrefabClassName(PostgresException violation)
        => violation.Detail is { Length: > 0 } detail ? detail : violation.ConstraintName ?? "unknown";
}
