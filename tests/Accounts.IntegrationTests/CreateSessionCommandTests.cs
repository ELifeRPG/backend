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
/// Also requires the keycloak-spi-reforger provider in the realm: an unlinked join mints a link PIN
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
    /// The central behaviour change: joining with an unknown Bohemia ID no longer provisions
    /// anything. The account is created by portal signup, so all a join can do is hand the player a
    /// PIN to redeem there.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownBohemiaId_CreatesNothingAndReturnsUnlinkedWithAPin()
    {
        var bohemiaId = new GameId(Guid.NewGuid());

        var result = await Send(new CreateSessionCommand(bohemiaId));

        Assert.Equal(SessionStatus.Unlinked, result.Status);
        Assert.Null(result.AccountId);
        Assert.Null(result.KeycloakUserId);
        Assert.False(string.IsNullOrWhiteSpace(result.LinkPin));
    }

    [Fact]
    public async Task Handle_UnknownBohemiaId_MintsAFreshPinEachJoin()
    {
        var bohemiaId = new GameId(Guid.NewGuid());

        var first = await Send(new CreateSessionCommand(bohemiaId));
        var second = await Send(new CreateSessionCommand(bohemiaId));

        Assert.NotEqual(first.LinkPin, second.LinkPin);
    }

    [Fact]
    public async Task Handle_LinkedAccount_ReturnsActiveWithTheKeycloakSubjectToImpersonate()
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        TestAccount account;
        using (var scope = _provider.CreateScope())
        {
            account = await TestAccounts.CreateAsync(scope.ServiceProvider, bohemiaId);
        }

        var result = await Send(new CreateSessionCommand(bohemiaId));

        Assert.Equal(SessionStatus.Active, result.Status);
        Assert.Equal(account.Id, result.AccountId);
        // The Bridge impersonates by user id, so this is what the whole session hangs off.
        Assert.Equal(account.KeycloakUserId, result.KeycloakUserId);
    }

    [Fact]
    public async Task Handle_CalledTwiceForALinkedAccount_ReturnsTheSameAccountWithoutDuplicating()
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        using (var scope = _provider.CreateScope())
        {
            await TestAccounts.CreateAsync(scope.ServiceProvider, bohemiaId);
        }

        var first = await Send(new CreateSessionCommand(bohemiaId));
        var second = await Send(new CreateSessionCommand(bohemiaId));

        Assert.Equal(first.AccountId, second.AccountId);
    }

    private async Task<CreateSessionResponse> Send(CreateSessionCommand command)
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
