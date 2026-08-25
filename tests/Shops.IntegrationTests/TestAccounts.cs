using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Shops.IntegrationTests;

/// <summary>
/// Creates accounts the way portal signup does: a Keycloak subject and no Bohemia ID.
///
/// Tests used to mint accounts through <c>CreateSessionCommand</c>, which no longer creates
/// anything — an unlinked join now returns a PIN for the player to redeem, not an account. Since
/// nothing here needs a real Keycloak user either, these accounts are built straight from the
/// aggregate, which also removes the Keycloak cleanup those tests used to carry.
///
/// Pass <paramref name="bohemiaId"/> for a test that needs an already-linked account (admin search
/// by Bohemia ID, a gameserver session for a known player).
/// </summary>
internal sealed record TestAccount(AccountId Id, KeycloakUserId KeycloakUserId);

internal static class TestAccounts
{
    public static async Task<TestAccount> CreateAsync(
        IServiceProvider services,
        GameId? bohemiaId = null,
        KeycloakUserId? keycloakUserId = null)
    {
        var repository = services.GetRequiredService<IAccountRepository>();

        var accountId = new AccountId(Guid.NewGuid());
        var subject = keycloakUserId ?? new KeycloakUserId(Guid.NewGuid());
        var created = new AccountCreated(accountId, bohemiaId, subject);

        var account = Account.Create(created);
        repository.StartStream(account, created);
        await repository.SaveChangesAsync(CancellationToken.None);

        return new TestAccount(accountId, subject);
    }
}

/// <summary>
/// Stands in for the bearer token's subject. Production reads it from HttpContext, which no test
/// here has — see <c>HttpContextCurrentKeycloakUser</c>.
/// </summary>
internal sealed class TestCurrentKeycloakUser : ICurrentKeycloakUser
{
    public KeycloakUserId? Current { get; set; }

    public ValueTask<KeycloakUserId?> GetIdAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Current);
}
