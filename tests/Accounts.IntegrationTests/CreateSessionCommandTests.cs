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
/// Joins the "HiveSettings" collection (see <see cref="HiveSettingsCollection"/>) because
/// <see cref="Handle_NewBohemiaId_CreatesAccountWithExpectedKeycloakUsername"/> asserts
/// <see cref="SessionStatus.Active"/> unconditionally — it would flake if it interleaved with
/// <see cref="CreateSessionCommandWhitelistGateTests"/> or <see cref="HiveSettingsTests"/>
/// toggling the shared <c>WhitelistEnabled</c> singleton mid-run.
/// </summary>
[Collection("HiveSettings")]
public sealed class CreateSessionCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly KeycloakTestClient _keycloak = new();
    private readonly List<string> _createdUsernames = [];

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(withInfrastructure: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var username in _createdUsernames)
        {
            await _keycloak.DeleteUserAsync(username);
        }

        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Handle_NewBohemiaId_CreatesAccountWithExpectedKeycloakUsername()
    {
        var bohemiaId = new GameId(Guid.NewGuid());

        var result = await Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(result.KeycloakUsername);
        Assert.Equal(KeycloakUsername.For(bohemiaId), result.KeycloakUsername);
        Assert.Equal(SessionStatus.Active, result.Status);
    }

    [Fact]
    public async Task Handle_CalledTwiceForSameBohemiaId_ReturnsSameAccountIdWithoutDuplicating()
    {
        var bohemiaId = new GameId(Guid.NewGuid());

        var first = await Send(new CreateSessionCommand(bohemiaId));
        var second = await Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(first.KeycloakUsername);
        Assert.Equal(first.AccountId, second.AccountId);
    }

    private async Task<CreateSessionResponse> Send(CreateSessionCommand command)
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
