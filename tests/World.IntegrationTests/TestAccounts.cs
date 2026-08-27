using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.DependencyInjection;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Creates accounts the way portal signup does: a Keycloak subject and no Bohemia ID. Copied from
/// Shops.IntegrationTests/TestAccounts.cs — this project needs a real account only to satisfy
/// CreateCharacterCommand's AccountLookupQuery dispatch when a test needs a real character (with a
/// real CurrentServerId) to exercise the ack/spawn-failed server guard against.
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

/// <summary>Stands in for the bearer token's subject. No test here reads it from HttpContext.</summary>
internal sealed class TestCurrentKeycloakUser : ICurrentKeycloakUser
{
    public KeycloakUserId? Current { get; set; }

    public ValueTask<KeycloakUserId?> GetIdAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Current);
}
