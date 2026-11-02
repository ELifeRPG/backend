using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Sessions;

public enum SessionStatus
{
    Active,
    Blocked,
    NotWhitelisted,

    /// <summary>
    /// The player's identity could not be resolved right now and <b>nothing was created</b> — in
    /// practice, Keycloak was unreachable on a first-ever join, so we could not check for an
    /// existing binding and must not invent a second account. The mod should retry.
    ///
    /// Narrowed from its original meaning. A join by an unknown game identity now creates an
    /// account and returns a playable status, so this is no longer the normal first-join answer.
    /// </summary>
    Unlinked,
}

/// <summary>
/// <c>AccountId</c> and <c>KeycloakUserId</c> are null only for <see cref="SessionStatus.Unlinked"/>,
/// where no account exists. <c>KeycloakUserId</c> is additionally null for any account that has not
/// linked a web identity yet; it is retained for compatibility and currently unused by the Bridge.
///
/// <c>WebAccountLinked</c>, not <c>Status</c>, is the "should the mod show the link" signal:
/// <c>Status</c> stays purely the play gate. <c>LinkPin</c> and <c>LinkUrl</c> are set together
/// whenever an unlinked account could be offered a link, and are both null when Keycloak declined
/// or was unreachable — in which case the player simply rejoins.
/// </summary>
public sealed record CreateSessionResponse(
    AccountId? AccountId,
    KeycloakUserId? KeycloakUserId,
    SessionStatus Status,
    bool WebAccountLinked,
    string? LinkPin = null,
    string? LinkUrl = null);

/// <param name="Platform">
/// Which platform the player joined from, when the gameserver reports it. Descriptive only — a Game
/// Identity ID is already globally unique — so null is a normal value and nothing keys on it.
/// </param>
public sealed record CreateSessionCommand(GameIdentityId GameIdentityId, GamePlatform? Platform = null)
    : IRequest<CreateSessionResponse>;

public sealed class CreateSessionHandler(
    IAccountRepository accountRepository,
    IGameAccountLinkRepository linkRepository,
    IAccountIdentityReconciler reconciler,
    IBohemiaGameAccountLinker linker,
    ILinkUrlBuilder linkUrlBuilder,
    IHiveSettingsRepository hiveSettingsRepository,
    IWhitelistApplicationRepository whitelistRepository,
    TimeProvider timeProvider)
    : IRequestHandler<CreateSessionCommand, CreateSessionResponse>
{
    public async ValueTask<CreateSessionResponse> Handle(CreateSessionCommand request, CancellationToken cancellationToken)
    {
        var account = await FindByGameIdentityAsync(request.GameIdentityId, cancellationToken);

        // Fast path: an identity we know, on an account that already has a web identity. Zero
        // Keycloak calls, which is what keeps the steady-state join cheap — every other branch below
        // costs at least one HTTP round trip to Keycloak.
        if (account is { HasWebAccount: true })
        {
            return await RespondAsync(account, null, null, cancellationToken);
        }

        try
        {
            if (await TryClaimFromKeycloakAsync(request, cancellationToken) is { } claimed)
            {
                return await RespondAsync(claimed, null, null, cancellationToken);
            }

            // Creation is the last resort, and deliberately after the Keycloak lookup above.
            // Reversed, a portal-first player who has linked but whose binding we have not observed
            // yet would get a brand-new second account and silently lose their existing one along
            // with its approved whitelist application.
            account ??= await CreateJoinFirstAccountAsync(request, cancellationToken);

            var linkPin = await linker.MintLinkPinAsync(request.GameIdentityId, cancellationToken);
            if (linkPin is null)
            {
                // The provider refuses to mint for an already-bound identity, so our "unclaimed"
                // view is simply stale. Re-check once rather than offering a PIN that could never be
                // redeemed.
                return await RespondAsync(
                    await TryClaimFromKeycloakAsync(request, cancellationToken) ?? account,
                    null,
                    null,
                    cancellationToken);
            }

            return await RespondAsync(account, linkPin, linkUrlBuilder.BuildLinkUrl(linkPin), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keycloak being down must stop linking, not playing. Four HTTP calls hang off an
            // unclaimed join (token, status, token, pin), and none of them should cost a player
            // their session. An account we already have — including one just created above — is
            // playable; only the creation path itself truly needs Keycloak reachable, because we
            // must not invent a second account when we cannot check for an existing binding.
            //
            // A lost creation race also lands here: our insert violated the link's unique index
            // because a concurrent join for the same identity committed first. Re-resolving turns
            // that into an ordinary join on the winner's account instead of making the player
            // reconnect. Reads do not flush Marten's failed unit of work, so this is safe to do on
            // the same session.
            account ??= await FindByGameIdentityAsync(request.GameIdentityId, cancellationToken);

            return account is null
                ? new CreateSessionResponse(null, null, SessionStatus.Unlinked, WebAccountLinked: false)
                : await RespondAsync(account, null, null, cancellationToken);
        }
    }

    private async ValueTask<Account?> FindByGameIdentityAsync(GameIdentityId gameIdentityId, CancellationToken cancellationToken)
    {
        var link = await linkRepository.FindByGameIdentityIdAsync(gameIdentityId, cancellationToken);
        return link is null ? null : await accountRepository.FindByIdAsync(link.AccountId, cancellationToken);
    }

    /// <summary>
    /// The player may have linked in Keycloak since their last join — the binding lives on their
    /// Keycloak user, and this is where we notice it. Null when Keycloak asserts no binding, and
    /// also when the reconciler refuses: a conflict needs a human, and the player keeps playing on
    /// whichever account holds their link while still being offered the link URL.
    /// </summary>
    private async ValueTask<Account?> TryClaimFromKeycloakAsync(CreateSessionCommand request, CancellationToken cancellationToken)
    {
        if (await linker.FindKeycloakUserIdAsync(request.GameIdentityId, cancellationToken) is not { } boundUser)
        {
            return null;
        }

        var reconciled = await reconciler.ReconcileAsync(
            boundUser, [request.GameIdentityId], request.Platform, cancellationToken);

        return reconciled is AccountIdentityReconciliationResult.Resolved resolved
            ? await accountRepository.FindByIdAsync(resolved.AccountId, cancellationToken)
            : null;
    }

    /// <summary>
    /// The account and its first link commit in one transaction. If a concurrent join for the same
    /// identity got there first, the link's unique index rejects this insert and the new account
    /// rolls back with it — which is what stops two servers in the hive creating two accounts for
    /// one player.
    /// </summary>
    private async ValueTask<Account> CreateJoinFirstAccountAsync(CreateSessionCommand request, CancellationToken cancellationToken)
    {
        var accountId = new AccountId(Guid.NewGuid());
        var accountCreated = new AccountCreated(accountId, KeycloakUserId: null);
        var account = Account.Create(accountCreated);

        accountRepository.StartStream(account, accountCreated);
        accountRepository.Append(accountId, new GameAccountLinked(accountId, request.GameIdentityId, request.Platform));
        linkRepository.Insert(
            GameAccountLink.Create(request.GameIdentityId, accountId, request.Platform, timeProvider.GetUtcNow()));

        await accountRepository.SaveChangesAsync(cancellationToken);

        return account;
    }

    private async ValueTask<CreateSessionResponse> RespondAsync(
        Account account, string? linkPin, string? linkUrl, CancellationToken cancellationToken)
        => new(
            account.Id,
            account.KeycloakUserId,
            await ResolveStatusAsync(account, cancellationToken),
            account.HasWebAccount,
            linkPin,
            linkUrl);

    /// <summary>
    /// Unchanged by join-first creation, and that is the point: a brand-new join-first account
    /// cannot have a whitelist application, because submission is keyed on the Keycloak subject and
    /// it has none. So with the hive gate on it reports <c>not_whitelisted</c> automatically, with
    /// no new code — whitelisting cannot be bypassed by joining before signing up.
    /// </summary>
    private async ValueTask<SessionStatus> ResolveStatusAsync(Account account, CancellationToken cancellationToken)
    {
        if (account.Status == AccountStatus.Locked)
        {
            return SessionStatus.Blocked;
        }

        var hiveSettings = await hiveSettingsRepository.GetAsync(cancellationToken);
        if (!hiveSettings.WhitelistEnabled)
        {
            return SessionStatus.Active;
        }

        var approved = await whitelistRepository.FindApprovedAsync(account.Id, cancellationToken);
        return approved is null ? SessionStatus.NotWhitelisted : SessionStatus.Active;
    }
}
