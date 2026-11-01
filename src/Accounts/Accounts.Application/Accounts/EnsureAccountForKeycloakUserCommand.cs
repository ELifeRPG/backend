using ELifeRPG.Accounts.Application.Common;

namespace ELifeRPG.Accounts.Application.Accounts;

public union EnsureAccountForKeycloakUserResult(
    EnsureAccountForKeycloakUserResult.Resolved,
    EnsureAccountForKeycloakUserResult.Conflict)
{
    public record Resolved(AccountId AccountId, bool Created);

    /// <summary>
    /// The caller's web identity and game identities cannot be reconciled automatically — they sit
    /// on accounts that both hold game identities, or on two different accounts. Surfaced to the
    /// portal as a 409 rather than silently ignored: this is the one place a player can be told
    /// their accounts need staff attention.
    /// </summary>
    public record Conflict(string Reason);
}

/// <summary>
/// Resolves the account behind the calling Keycloak user, creating it if it does not exist yet.
///
/// Two origins converge here. Portal-first: the Keycloak user comes from ordinary web signup and
/// the account is created the first time they reach the backend, with no game identity. Join-first:
/// the account already exists because the player joined a gameserver, and the token's game-identity
/// claim is what lets us attach this web identity to it rather than making a second account.
///
/// Idempotent: a portal page calling this on every load returns the same account.
/// </summary>
public sealed record EnsureAccountForKeycloakUserCommand(KeycloakUserId KeycloakUserId)
    : IRequest<EnsureAccountForKeycloakUserResult>;

public sealed class EnsureAccountForKeycloakUserHandler(
    IAccountIdentityReconciler reconciler,
    ICurrentKeycloakUser currentKeycloakUser)
    : IRequestHandler<EnsureAccountForKeycloakUserCommand, EnsureAccountForKeycloakUserResult>
{
    public async ValueTask<EnsureAccountForKeycloakUserResult> Handle(
        EnsureAccountForKeycloakUserCommand request, CancellationToken cancellationToken)
    {
        var gameIdentityIds = await currentKeycloakUser.GetGameIdentityIdsAsync(cancellationToken);

        var result = await reconciler.ReconcileAsync(
            request.KeycloakUserId, gameIdentityIds, platform: null, cancellationToken);

        return result switch
        {
            AccountIdentityReconciliationResult.Resolved resolved
                => new EnsureAccountForKeycloakUserResult.Resolved(resolved.AccountId, resolved.Created),
            AccountIdentityReconciliationResult.Conflict conflict
                => new EnsureAccountForKeycloakUserResult.Conflict(conflict.Reason),
        };
    }
}
