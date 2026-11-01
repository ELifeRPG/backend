using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Common;

public interface IAccountRepository
{
    ValueTask<Account?> FindByIdAsync(AccountId accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the account behind a Keycloak subject. This is the portal's entry point: a signed-in
    /// player is known by their Keycloak user id and nothing else, so every self-service path
    /// (whitelist application, link status) starts here.
    /// </summary>
    ValueTask<Account?> FindByKeycloakUserIdAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken);

    /// <summary>Every account that has not been absorbed into another.</summary>
    ValueTask<IReadOnlyList<Account>> ListAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Account>> ListByIdsAsync(IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken);

    void StartStream(Account account, AccountCreated domainEvent);

    void Append<TEvent>(AccountId accountId, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
