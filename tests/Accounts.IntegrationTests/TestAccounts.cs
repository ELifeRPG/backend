using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Creates accounts the way portal signup does: a Keycloak subject and no Game Identity ID.
///
/// Tests used to mint accounts through <c>CreateSessionCommand</c>, which no longer creates
/// anything — an unlinked join now returns a PIN for the player to redeem, not an account. Since
/// nothing here needs a real Keycloak user either, these accounts are built straight from the
/// aggregate, which also removes the Keycloak cleanup those tests used to carry.
///
/// Pass <paramref name="gameIdentityId"/> for a test that needs an already-linked account (admin search
/// by Game Identity ID, a gameserver session for a known player).
/// </summary>
internal sealed record TestAccount(AccountId Id, KeycloakUserId KeycloakUserId);

internal static class TestAccounts
{
    public static async Task<TestAccount> CreateAsync(
        IServiceProvider services,
        GameIdentityId? gameIdentityId = null,
        KeycloakUserId? keycloakUserId = null)
    {
        var repository = services.GetRequiredService<IAccountRepository>();

        var accountId = new AccountId(Guid.NewGuid());
        var subject = keycloakUserId ?? new KeycloakUserId(Guid.NewGuid());
        var created = new AccountCreated(accountId, subject);

        var account = Account.Create(created);
        repository.StartStream(account, created);

        // Game identities live in their own documents now, so an "already linked" test account is an
        // account plus a link, committed together exactly as production does it.
        if (gameIdentityId is { } linkedIdentity)
        {
            services.GetRequiredService<IGameAccountLinkRepository>()
                .Insert(GameAccountLink.Create(linkedIdentity, accountId, platform: null, DateTimeOffset.UtcNow));
            repository.Append(accountId, new GameAccountLinked(accountId, linkedIdentity, Platform: null));
        }

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

    /// <summary>
    /// The game identities this subject's token claims. Settable so a test can stand in for the
    /// realm's multi-valued claim without a real token.
    /// </summary>
    public List<GameIdentityId> GameIdentityIds { get; } = [];

    public ValueTask<KeycloakUserId?> GetIdAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Current);

    public ValueTask<IReadOnlyList<GameIdentityId>> GetGameIdentityIdsAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<GameIdentityId>>(GameIdentityIds);
}
