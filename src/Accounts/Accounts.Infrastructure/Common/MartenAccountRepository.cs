using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed class MartenAccountRepository(IDocumentSession session) : IAccountRepository
{
    // Marten infers the document id type from the Account.Id property (AccountId, not Guid) since
    // StronglyTypedId's conversion operators make it recognizable as a strongly-typed id. Passing
    // accountId.Value (a raw Guid) here throws DocumentIdTypeMismatchException at runtime — pass the
    // AccountId itself. This is the inverse of the event-stream-id rule (StartStream needs .Value).
    public async ValueTask<Account?> FindByIdAsync(AccountId accountId, CancellationToken cancellationToken)
        => await session.LoadAsync<Account>(accountId, cancellationToken);

    // BohemiaId is now nullable — an account created by portal signup has none until the player
    // links. The null check is not redundant: without it every unlinked account would match a
    // lookup for whichever Bohemia ID happened to be asked for once BohemiaId.Value defaulted.
    public async ValueTask<Account?> FindByBohemiaIdAsync(GameId bohemiaId, CancellationToken cancellationToken)
        => await session.Query<Account>()
            .SingleOrDefaultAsync(x => x.BohemiaId != null && x.BohemiaId.Value.Value == bohemiaId.Value, cancellationToken);

    public async ValueTask<Account?> FindByKeycloakUserIdAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken)
        => await session.Query<Account>()
            .SingleOrDefaultAsync(x => x.KeycloakUserId.Value == keycloakUserId.Value, cancellationToken);

    // In-memory filter, not a Marten LINQ-translated Where: BohemiaId is a Guid, and
    // .ToString().Contains() over the JSONB-stored document isn't a query Marten's LINQ
    // provider translates reliably. Fine at this project's account volume; revisit if this
    // ever needs to scale past loading the full table per search.
    public async ValueTask<IReadOnlyList<Account>> SearchAsync(string search, CancellationToken cancellationToken)
    {
        var accounts = await session.Query<Account>().ToListAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(search))
        {
            return accounts;
        }

        // Unlinked accounts have no Bohemia ID to match on, so a non-empty search never returns them.
        return accounts
            .Where(a => a.BohemiaId is { } id && id.Value.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void StartStream(Account account, AccountCreated domainEvent)
        => session.Events.StartStream<Account>(account.Id.Value, domainEvent);

    public void Append<TEvent>(AccountId accountId, TEvent domainEvent) where TEvent : notnull
        => session.Events.Append(accountId.Value, domainEvent);

    public ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => new(session.SaveChangesAsync(cancellationToken));
}
