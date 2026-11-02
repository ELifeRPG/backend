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
    //
    // Deliberately NOT filtered on AbsorbedInto: the whole point of keeping an absorbed account's
    // stream is that "what happened to my old account id" stays answerable by direct lookup.
    public async ValueTask<Account?> FindByIdAsync(AccountId accountId, CancellationToken cancellationToken)
        => await session.LoadAsync<Account>(accountId, cancellationToken);

    // The null check is not redundant, and getting it wrong is worse than staleness: without it
    // every account with no subject — every join-first account, and every absorbed loser, whose
    // subject Apply(AccountAbsorbed) deliberately clears — would match a lookup for whichever
    // subject happened to be asked for, and /accounts/me would hand back the wrong account.
    public async ValueTask<Account?> FindByKeycloakUserIdAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken)
        => await session.Query<Account>()
            .SingleOrDefaultAsync(
                x => x.KeycloakUserId != null
                    && x.KeycloakUserId.Value.Value == keycloakUserId.Value
                    && x.AbsorbedInto == null,
                cancellationToken);

    public async ValueTask<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken)
        => await session.Query<Account>().Where(x => x.AbsorbedInto == null).ToListAsync(cancellationToken);

    public async ValueTask<IReadOnlyList<Account>> ListByIdsAsync(
        IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return [];
        }

        // In-memory id filter rather than a translated Contains over strongly-typed ids — the same
        // trade MartenGameAccountLinkRepository makes, and the same volume assumption.
        var wanted = accountIds.Select(x => x.Value).ToHashSet();
        var accounts = await session.Query<Account>()
            .Where(x => x.AbsorbedInto == null)
            .ToListAsync(cancellationToken);

        return accounts.Where(x => wanted.Contains(x.Id.Value)).ToList();
    }

    public void StartStream(Account account, AccountCreated domainEvent)
        => session.Events.StartStream<Account>(account.Id.Value, domainEvent);

    public void Append<TEvent>(AccountId accountId, TEvent domainEvent) where TEvent : notnull
        => session.Events.Append(accountId.Value, domainEvent);

    public ValueTask SaveChangesAsync(CancellationToken cancellationToken)
        => new(session.SaveChangesAsync(cancellationToken));
}
