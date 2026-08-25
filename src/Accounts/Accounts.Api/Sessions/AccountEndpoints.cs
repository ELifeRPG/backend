using ELifeRPG.Accounts.Api.Common;
using ELifeRPG.Accounts.Api.Sessions;
using ELifeRPG.Accounts.Application.Accounts;
using ELifeRPG.Accounts.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class AccountModule
{
    public const string SessionCreateScope = "gameserver:session:create";
    public const string AccountsManageScope = "accounts:manage";
    public const string SelfManageScope = "account:self:manage";
    private const string SelfManagePolicy = "Accounts.SelfManage";
    private const string SessionCreatePolicy = "Accounts.SessionCreate";
    private const string AccountsManagePolicy = "Accounts.Manage";

    public const string SessionRevokeScope = "gameserver:session:revoke";
    private const string SessionRevokePolicy = "Accounts.SessionRevoke";

    public const string RoleManageRole = "admin";
    private const string RoleManagePolicy = "Accounts.RoleManage";

    public static IServiceCollection AddAccountModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAccountInfrastructure(configuration);

        // The HttpContext-reading half of ICurrentKeycloakUser, mirroring how the Characters and
        // Shops modules resolve the calling gameserver.
        services.AddScoped<ICurrentKeycloakUser, HttpContextCurrentKeycloakUser>();

        services.AddAuthorizationBuilder()
            .AddPolicy(SessionCreatePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(SessionCreateScope)))
            .AddPolicy(AccountsManagePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(AccountsManageScope)))
            .AddPolicy(SessionRevokePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(SessionRevokeScope)))
            .AddPolicy(SelfManagePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(SelfManageScope)))
            .AddPolicy(RoleManagePolicy, policy => policy.RequireAssertion(context =>
                RealmRoleAuthorization.HasRole(context.User, RoleManageRole)));

        return services;
    }

    public static WebApplication MapAccountModule(this WebApplication app)
    {
        var group = app.MapGroup("api/accounts").WithTags("Accounts");

        group.MapPost("session-bootstrap", async (
                [FromBody] CreateSessionRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(), cancellationToken);
                return Results.Ok(SessionDto.Create(result));
            })
            .RequireAuthorization(SessionCreatePolicy)
            .Produces<SessionDto>()
            .WithName("BootstrapSession")
            .WithDescription("Bootstraps (or looks up) a session for a player's Bohemia ID, provisioning an account if needed. Always returns 200 — a blocked or not-whitelisted account is reported via the Status field, not an error.");

        group.MapPost("me", async (
                ICurrentKeycloakUser currentUser,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var keycloakUserId = await currentUser.GetIdAsync(cancellationToken);
                if (keycloakUserId is not { } subject)
                {
                    return Results.Problem(
                        title: "Token carries no usable subject",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var result = await mediator.Send(new EnsureAccountForKeycloakUserCommand(subject), cancellationToken);
                return Results.Ok(new CurrentAccountDto { AccountId = result.AccountId.Value, Created = result.Created });
            })
            .RequireAuthorization(SelfManagePolicy)
            .Produces<CurrentAccountDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("EnsureCurrentAccount")
            .WithDescription("Creates (or returns) the account behind the calling Keycloak user. This is the portal-first entry point: the account exists from web signup onward, with no Bohemia ID until the player links their game identity. Idempotent.");

        group.MapPost("{accountId:guid}/lock", async (
                Guid accountId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new LockAccountCommand(new AccountId(accountId)), cancellationToken);

                return result switch
                {
                    LockAccountResult.Locked => Results.NoContent(),
                    LockAccountResult.AccountNotFound => Results.Problem(
                        title: "Account not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(AccountsManagePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("LockAccount")
            .WithDescription("Locks (blocks) an account: marks it Locked and disables its Keycloak user (blocks normal login; does not by itself block the Bridge's token-exchange grant — player-connected's status check is what actually prevents a new player token, see ARCHITECTURE.md §4.3). Idempotent — locking an already-locked account still returns 204.");

        group.MapPost("{accountId:guid}/unlock", async (
                Guid accountId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new UnlockAccountCommand(new AccountId(accountId)), cancellationToken);

                return result switch
                {
                    UnlockAccountResult.Unlocked => Results.NoContent(),
                    UnlockAccountResult.AccountNotFound => Results.Problem(
                        title: "Account not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(AccountsManagePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UnlockAccount")
            .WithDescription("Unlocks an account: re-enables its Keycloak user and marks it Active. Idempotent — unlocking an already-active account still returns 204.");

        group.MapPost("tokens/revoke", async (
                [FromBody] RevokeTokenRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                await mediator.Send(request.ToCommand(), cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(SessionRevokePolicy)
            .Produces(StatusCodes.Status204NoContent)
            .WithName("RevokeToken")
            .WithDescription("Revokes a specific player-impersonation token by jti, e.g. on player disconnect.");

        group.MapGet("", async (
                [FromQuery] string? search,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new AccountsQuery(search ?? string.Empty), cancellationToken);
                return result switch
                {
                    AccountsResult.Found found => Results.Ok(new AccountsResponseDto
                    {
                        Accounts = found.Accounts.Select(AccountDto.Create).ToList(),
                    }),
                };
            })
            .RequireAuthorization(AccountsManagePolicy)
            .Produces<AccountsResponseDto>()
            .WithName("ListAccounts")
            .WithDescription("Lists accounts, optionally filtered by a Bohemia ID substring match, for the admin accounts view.");

        group.MapGet("{accountId:guid}/roles", async (
                Guid accountId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new AccountRolesQuery(new AccountId(accountId)), cancellationToken);
                return result switch
                {
                    AccountRolesResult.Found found => Results.Ok(AccountRolesDto.Create(found)),
                    AccountRolesResult.AccountNotFound => Results.Problem(
                        title: "Account not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(RoleManagePolicy)
            .Produces<AccountRolesDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetAccountRoles")
            .WithDescription("Gets an account's currently assigned realm roles plus every role available to assign, minus Keycloak's own built-in roles.");

        group.MapPut("{accountId:guid}/roles/{roleName}", async (
                Guid accountId,
                string roleName,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new AssignAccountRoleCommand(new AccountId(accountId), roleName), cancellationToken);
                return result switch
                {
                    AssignAccountRoleResult.Assigned => Results.NoContent(),
                    AssignAccountRoleResult.AccountNotFound => Results.Problem(
                        title: "Account not found", statusCode: StatusCodes.Status404NotFound),
                    AssignAccountRoleResult.RoleNotFound => Results.Problem(
                        title: "Role not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(RoleManagePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("AssignAccountRole")
            .WithDescription("Grants a Keycloak realm role to an account. Idempotent — assigning an already-held role still returns 204.");

        group.MapDelete("{accountId:guid}/roles/{roleName}", async (
                Guid accountId,
                string roleName,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new RevokeAccountRoleCommand(new AccountId(accountId), roleName), cancellationToken);
                return result switch
                {
                    RevokeAccountRoleResult.Revoked => Results.NoContent(),
                    RevokeAccountRoleResult.AccountNotFound => Results.Problem(
                        title: "Account not found", statusCode: StatusCodes.Status404NotFound),
                    RevokeAccountRoleResult.RoleNotFound => Results.Problem(
                        title: "Role not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(RoleManagePolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RevokeAccountRole")
            .WithDescription("Revokes a Keycloak realm role from an account. Idempotent — revoking a role it doesn't hold still returns 204.");

        return app;
    }
}
