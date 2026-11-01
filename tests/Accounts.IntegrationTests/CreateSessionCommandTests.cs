using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// Also requires the keycloak-bohemia-gameaccount provider in the realm: an unlinked join mints a link PIN
/// through it.
///
/// Joins the "HiveSettings" collection because the linked case asserts
/// <see cref="SessionStatus.Active"/> unconditionally — it would flake if it interleaved with
/// <see cref="CreateSessionCommandWhitelistGateTests"/> or <see cref="HiveSettingsTests"/> toggling
/// the shared <c>WhitelistEnabled</c> singleton mid-run.
/// </summary>
[Collection("HiveSettings")]
public sealed class CreateSessionCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(withInfrastructure: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    /// <summary>
    /// The central behaviour change: joining with an unknown Game Identity ID now <b>creates</b> the
    /// account, so the player plays at once, and hands them a URL to link a web account later.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownGameIdentityId_CreatesAPlayableAccountWithALinkUrl()
    {
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());

        var result = await Send(new CreateSessionCommand(gameIdentityId));

        Assert.Equal(SessionStatus.Active, result.Status);
        Assert.NotNull(result.AccountId);
        // Playable, but with no web identity yet — which is the signal the mod shows the link on.
        Assert.False(result.WebAccountLinked);
        Assert.Null(result.KeycloakUserId);
        Assert.False(string.IsNullOrWhiteSpace(result.LinkPin));
        Assert.False(string.IsNullOrWhiteSpace(result.LinkUrl));
        Assert.Contains(result.LinkPin!, result.LinkUrl!);
    }

    /// <summary>
    /// A fresh PIN per join as before, but now on the <i>same</i> account: the second join finds the
    /// link created by the first rather than creating a second account.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownGameIdentityId_MintsAFreshPinEachJoinOnTheSameAccount()
    {
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());

        var first = await Send(new CreateSessionCommand(gameIdentityId));
        var second = await Send(new CreateSessionCommand(gameIdentityId));

        Assert.NotEqual(first.LinkPin, second.LinkPin);
        Assert.Equal(first.AccountId, second.AccountId);
    }

    /// <summary>
    /// The hazard-1 regression test. Two servers in the hive, or a fast reconnect, both read "no
    /// link" and both try to create — the link's unique index must collapse that to one account
    /// rather than leaving the player split across two, which no later query could untangle.
    /// </summary>
    [Fact]
    public async Task Handle_ConcurrentJoinsForOneIdentity_ResolveToASingleAccount()
    {
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => Send(new CreateSessionCommand(gameIdentityId)))));

        // Every racer gets a usable account — the loser resolves onto the winner's rather than
        // being told to reconnect — and crucially they all get the *same* one.
        Assert.All(results, r => Assert.NotNull(r.AccountId));
        Assert.Single(results.Select(r => r.AccountId).Distinct());
    }

    [Fact]
    public async Task Handle_LinkedAccount_ReturnsActiveWithTheKeycloakSubjectToImpersonate()
    {
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());
        TestAccount account;
        using (var scope = _provider.CreateScope())
        {
            account = await TestAccounts.CreateAsync(scope.ServiceProvider, gameIdentityId);
        }

        var result = await Send(new CreateSessionCommand(gameIdentityId));

        Assert.Equal(SessionStatus.Active, result.Status);
        Assert.Equal(account.Id, result.AccountId);
        Assert.True(result.WebAccountLinked);
        Assert.Equal(account.KeycloakUserId, result.KeycloakUserId);
        // Nothing to link, so no prompt — and no Keycloak round trip was needed to decide that.
        Assert.Null(result.LinkUrl);
    }

    [Fact]
    public async Task Handle_CalledTwiceForALinkedAccount_ReturnsTheSameAccountWithoutDuplicating()
    {
        var gameIdentityId = new GameIdentityId(Guid.NewGuid());
        using (var scope = _provider.CreateScope())
        {
            await TestAccounts.CreateAsync(scope.ServiceProvider, gameIdentityId);
        }

        var first = await Send(new CreateSessionCommand(gameIdentityId));
        var second = await Send(new CreateSessionCommand(gameIdentityId));

        Assert.Equal(first.AccountId, second.AccountId);
    }

    private async Task<CreateSessionResponse> Send(CreateSessionCommand command)
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
