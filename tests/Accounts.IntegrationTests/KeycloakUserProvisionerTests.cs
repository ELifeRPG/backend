using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Infrastructure.Common;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.Options;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class KeycloakUserProvisionerTests : IAsyncLifetime
{
    private readonly KeycloakTestClient _keycloak = new();
    private KeycloakUserProvisioner _provisioner = null!;
    private string _username = null!;
    private KeycloakUserId _keycloakUserId;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new KeycloakOptions
        {
            BaseUrl = "http://keycloak:8080/",
            Realm = "eliferpg",
            ProvisioningClientId = "account-service",
            ProvisioningClientSecret = "account-service-secret",
        });
        _provisioner = new KeycloakUserProvisioner(new HttpClient { BaseAddress = new Uri(options.Value.BaseUrl) }, options);

        var bohemiaId = new GameId(Guid.NewGuid());
        _username = KeycloakUsername.For(bohemiaId);
        _keycloakUserId = await _provisioner.EnsureUserAsync(bohemiaId, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _keycloak.DeleteUserAsync(_username);

    [Fact]
    public async Task DisableUserAsync_DisablesTheKeycloakUser()
    {
        await _provisioner.DisableUserAsync(_keycloakUserId, CancellationToken.None);

        Assert.False(await _keycloak.GetUserEnabledAsync(_username));
    }

    [Fact]
    public async Task EnableUserAsync_AfterDisabling_ReEnablesTheKeycloakUser()
    {
        await _provisioner.DisableUserAsync(_keycloakUserId, CancellationToken.None);

        await _provisioner.EnableUserAsync(_keycloakUserId, CancellationToken.None);

        Assert.True(await _keycloak.GetUserEnabledAsync(_username));
    }

    [Fact]
    public async Task ListRealmRolesAsync_ExcludesKeycloakBuiltinRoles()
    {
        var roles = await _provisioner.ListRealmRolesAsync(CancellationToken.None);

        Assert.DoesNotContain(roles, r => r.Name == "offline_access");
        Assert.DoesNotContain(roles, r => r.Name == "uma_authorization");
        Assert.DoesNotContain(roles, r => r.Name.StartsWith("default-roles-"));
        Assert.Contains(roles, r => r.Name == "whitelist-reviewer");
    }

    [Fact]
    public async Task AssignRealmRoleAsync_ThenListUserRealmRolesAsync_ReflectsTheAssignment()
    {
        var assigned = await _provisioner.AssignRealmRoleAsync(_keycloakUserId, "whitelist-reviewer", CancellationToken.None);
        Assert.True(assigned);

        var roles = await _provisioner.ListUserRealmRolesAsync(_keycloakUserId, CancellationToken.None);

        Assert.Contains("whitelist-reviewer", roles);
        Assert.DoesNotContain("default-roles-eliferpg", roles);
    }

    [Fact]
    public async Task RemoveRealmRoleAsync_AfterAssigning_RemovesIt()
    {
        await _provisioner.AssignRealmRoleAsync(_keycloakUserId, "whitelist-reviewer", CancellationToken.None);

        var removed = await _provisioner.RemoveRealmRoleAsync(_keycloakUserId, "whitelist-reviewer", CancellationToken.None);
        Assert.True(removed);

        var roles = await _provisioner.ListUserRealmRolesAsync(_keycloakUserId, CancellationToken.None);
        Assert.DoesNotContain("whitelist-reviewer", roles);
    }

    [Fact]
    public async Task AssignRealmRoleAsync_UnknownRoleName_ReturnsFalse()
    {
        var assigned = await _provisioner.AssignRealmRoleAsync(_keycloakUserId, "no-such-role", CancellationToken.None);

        Assert.False(assigned);
    }
}
