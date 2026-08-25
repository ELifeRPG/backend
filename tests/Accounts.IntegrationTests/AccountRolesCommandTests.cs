using ELifeRPG.Accounts.Application.Accounts;
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
/// </summary>
public sealed class AccountRolesCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<string> _createdUsernames = [];

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(withInfrastructure: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var keycloak = new KeycloakTestClient();
        foreach (var username in _createdUsernames)
        {
            await keycloak.DeleteUserAsync(username);
        }
        await _provider.DisposeAsync();
    }

    // Role assignment happens against Keycloak, so unlike most tests this one needs a real user
    // there. Production no longer creates them — portal signup does — so the test client stands in.
    private async Task<AccountId> CreateAccountAsync()
    {
        var username = $"test-roles-{Guid.NewGuid():N}";
        var keycloakUserId = new KeycloakUserId(await new KeycloakTestClient().CreateUserAsync(username));
        _createdUsernames.Add(username);

        using var scope = _provider.CreateScope();
        var account = await TestAccounts.CreateAsync(scope.ServiceProvider, keycloakUserId: keycloakUserId);
        return account.Id;
    }

    [Fact]
    public async Task Assign_ThenQuery_ReflectsTheAssignedRole()
    {
        var accountId = await CreateAccountAsync();

        var assignResult = await Send<AssignAccountRoleCommand, AssignAccountRoleResult>(new AssignAccountRoleCommand(accountId, "whitelist-reviewer"));
        Assert.True(assignResult is AssignAccountRoleResult.Assigned, $"Expected Assigned, got {assignResult}");

        var rolesResult = await Send<AccountRolesQuery, AccountRolesResult>(new AccountRolesQuery(accountId));
        if (rolesResult is not AccountRolesResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {rolesResult}.");
        }
        Assert.Contains("whitelist-reviewer", found.AssignedRoles);
        Assert.Contains(found.AvailableRoles, r => r.Name == "whitelist-reviewer");
    }

    [Fact]
    public async Task Revoke_AfterAssigning_RemovesIt()
    {
        var accountId = await CreateAccountAsync();
        await Send<AssignAccountRoleCommand, AssignAccountRoleResult>(new AssignAccountRoleCommand(accountId, "whitelist-reviewer"));

        var revokeResult = await Send<RevokeAccountRoleCommand, RevokeAccountRoleResult>(new RevokeAccountRoleCommand(accountId, "whitelist-reviewer"));
        Assert.True(revokeResult is RevokeAccountRoleResult.Revoked, $"Expected Revoked, got {revokeResult}");

        var rolesResult = await Send<AccountRolesQuery, AccountRolesResult>(new AccountRolesQuery(accountId));
        if (rolesResult is not AccountRolesResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {rolesResult}.");
        }
        Assert.DoesNotContain("whitelist-reviewer", found.AssignedRoles);
    }

    [Fact]
    public async Task Query_UnknownAccount_ReturnsAccountNotFound()
    {
        var result = await Send<AccountRolesQuery, AccountRolesResult>(new AccountRolesQuery(new AccountId(Guid.NewGuid())));

        Assert.True(result is AccountRolesResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Assign_UnknownAccount_ReturnsAccountNotFound()
    {
        var result = await Send<AssignAccountRoleCommand, AssignAccountRoleResult>(new AssignAccountRoleCommand(new AccountId(Guid.NewGuid()), "whitelist-reviewer"));

        Assert.True(result is AssignAccountRoleResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Assign_UnknownRole_ReturnsRoleNotFound()
    {
        var accountId = await CreateAccountAsync();

        var result = await Send<AssignAccountRoleCommand, AssignAccountRoleResult>(new AssignAccountRoleCommand(accountId, "no-such-role"));

        Assert.True(result is AssignAccountRoleResult.RoleNotFound, $"Expected RoleNotFound, got {result}");
    }

    [Fact]
    public async Task Revoke_UnknownAccount_ReturnsAccountNotFound()
    {
        var result = await Send<RevokeAccountRoleCommand, RevokeAccountRoleResult>(new RevokeAccountRoleCommand(new AccountId(Guid.NewGuid()), "whitelist-reviewer"));

        Assert.True(result is RevokeAccountRoleResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Assign_AlreadyAssignedRole_StaysAssignedAndDoesNotThrow()
    {
        var accountId = await CreateAccountAsync();

        var firstAssignResult = await Send<AssignAccountRoleCommand, AssignAccountRoleResult>(new AssignAccountRoleCommand(accountId, "whitelist-reviewer"));
        Assert.True(firstAssignResult is AssignAccountRoleResult.Assigned, $"Expected Assigned, got {firstAssignResult}");

        var secondAssignResult = await Send<AssignAccountRoleCommand, AssignAccountRoleResult>(new AssignAccountRoleCommand(accountId, "whitelist-reviewer"));
        Assert.True(secondAssignResult is AssignAccountRoleResult.Assigned, $"Expected Assigned, got {secondAssignResult}");

        var rolesResult = await Send<AccountRolesQuery, AccountRolesResult>(new AccountRolesQuery(accountId));
        if (rolesResult is not AccountRolesResult.Found found)
        {
            throw new InvalidOperationException($"Expected Found, got {rolesResult}.");
        }
        Assert.Contains("whitelist-reviewer", found.AssignedRoles);
    }

    [Fact]
    public async Task Revoke_UnknownRole_ReturnsRoleNotFound()
    {
        var accountId = await CreateAccountAsync();

        var result = await Send<RevokeAccountRoleCommand, RevokeAccountRoleResult>(new RevokeAccountRoleCommand(accountId, "no-such-role"));

        Assert.True(result is RevokeAccountRoleResult.RoleNotFound, $"Expected RoleNotFound, got {result}");
    }

    [Fact]
    public async Task Assign_KeycloakBuiltinRole_ReturnsRoleNotFound()
    {
        var accountId = await CreateAccountAsync();

        var result = await Send<AssignAccountRoleCommand, AssignAccountRoleResult>(new AssignAccountRoleCommand(accountId, "offline_access"));

        Assert.True(result is AssignAccountRoleResult.RoleNotFound, $"Expected RoleNotFound, got {result}");
    }

    private async Task<TResponse> Send<TCommand, TResponse>(TCommand command) where TCommand : IRequest<TResponse>
    {
        using var scope = _provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command);
    }
}
