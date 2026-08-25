using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Sessions;

public enum SessionStatus
{
    Active,
    Blocked,
    NotWhitelisted,

    /// <summary>
    /// No account owns this Bohemia ID yet. The player is handed a PIN to type into the portal;
    /// nothing is created here, because the account is created by web signup, not by joining.
    /// </summary>
    Unlinked,
}

/// <summary>
/// <c>KeycloakUserId</c> is null when <c>Status</c> is <see cref="SessionStatus.Unlinked"/> — there
/// is no account and therefore nobody to impersonate. <c>LinkPin</c> is set only in that case, and
/// only when Keycloak actually minted one.
/// </summary>
public sealed record CreateSessionResponse(
    AccountId? AccountId,
    KeycloakUserId? KeycloakUserId,
    SessionStatus Status,
    string? LinkPin = null);

public sealed record CreateSessionCommand(GameId BohemiaId) : IRequest<CreateSessionResponse>;

public sealed class CreateSessionHandler(
    IAccountRepository accountRepository,
    IBohemiaGameAccountLinker linker,
    IHiveSettingsRepository hiveSettingsRepository,
    IWhitelistApplicationRepository whitelistRepository)
    : IRequestHandler<CreateSessionCommand, CreateSessionResponse>
{
    public async ValueTask<CreateSessionResponse> Handle(CreateSessionCommand request, CancellationToken cancellationToken)
    {
        var account = await accountRepository.FindByBohemiaIdAsync(request.BohemiaId, cancellationToken);

        if (account is null)
        {
            // The player may have linked in Keycloak since their last join — the binding lives on
            // their Keycloak user, and this is where we notice it and record it on the account.
            account = await BindFromKeycloakAsync(request.BohemiaId, cancellationToken);
        }

        if (account is null)
        {
            var pin = await linker.MintLinkPinAsync(request.BohemiaId, cancellationToken);
            return new CreateSessionResponse(null, null, SessionStatus.Unlinked, pin);
        }

        var status = await ResolveStatusAsync(account, cancellationToken);

        return new CreateSessionResponse(account.Id, account.KeycloakUserId, status);
    }

    private async ValueTask<Account?> BindFromKeycloakAsync(GameId bohemiaId, CancellationToken cancellationToken)
    {
        var keycloakUserId = await linker.FindKeycloakUserIdAsync(bohemiaId, cancellationToken);
        if (keycloakUserId is not { } boundUser)
        {
            return null;
        }

        var account = await accountRepository.FindByKeycloakUserIdAsync(boundUser, cancellationToken);
        if (account is null)
        {
            // Keycloak says this game identity belongs to a user we have no account for. That means
            // the account-creation step on portal signup did not run (or was rolled back), not that
            // the player should silently get a second identity — so report Unlinked rather than
            // inventing an account here.
            return null;
        }

        account.BindBohemiaId(bohemiaId);
        accountRepository.Append(account.Id, new BohemiaIdBound(account.Id, bohemiaId));
        await accountRepository.SaveChangesAsync(cancellationToken);

        return account;
    }

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
