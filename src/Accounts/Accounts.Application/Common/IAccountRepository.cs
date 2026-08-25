using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Common;

public interface IAccountRepository
{
    ValueTask<Account?> FindByIdAsync(AccountId accountId, CancellationToken cancellationToken);

    ValueTask<Account?> FindByBohemiaIdAsync(GameId bohemiaId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the account behind a Keycloak subject. This is the portal's entry point: a signed-in
    /// player is known by their Keycloak user id and nothing else, so every self-service path
    /// (whitelist application, link status) starts here.
    /// </summary>
    ValueTask<Account?> FindByKeycloakUserIdAsync(KeycloakUserId keycloakUserId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Account>> SearchAsync(string search, CancellationToken cancellationToken);

    void StartStream(Account account, AccountCreated domainEvent);

    void Append<TEvent>(AccountId accountId, TEvent domainEvent) where TEvent : notnull;

    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
