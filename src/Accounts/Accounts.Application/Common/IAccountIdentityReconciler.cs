using ELifeRPG.Accounts.Domain.Events;

namespace ELifeRPG.Accounts.Application.Common;

public union AccountIdentityReconciliationResult(
    AccountIdentityReconciliationResult.Resolved,
    AccountIdentityReconciliationResult.Conflict)
{
    public record Resolved(AccountId AccountId, bool Created);

    /// <summary>
    /// The two identities cannot be reconciled automatically. Always a staff matter: every case
    /// that reaches here risks destroying data if guessed at.
    /// </summary>
    public record Conflict(string Reason);
}

/// <summary>
/// Binds the game identities Keycloak asserts for a web identity onto exactly one account.
///
/// A plain application service rather than a Mediator request: both call sites live in this module,
/// and nesting a <c>Send</c> inside a handler buys nothing. Follows
/// <c>World.Application/Inventory/IItemCatalogResolver.cs</c> — interface and its one implementation
/// in a single file.
///
/// Both call sites matter, because the Keycloak SPI binds without telling the backend:
/// <c>/accounts/me</c> notices on the next portal page load, <c>session-bootstrap</c> on the next
/// join, and a player who only ever does one of the two is still covered.
/// </summary>
public interface IAccountIdentityReconciler
{
    /// <param name="gameIdentityIds">
    /// May be empty — that is the ordinary portal-first signup, which creates an account with no
    /// links. A <i>set</i> rather than one id because the Keycloak claim is multi-valued: a player
    /// on three platforms presents three.
    /// </param>
    /// <param name="platform">Recorded on links this call creates. Null is normal.</param>
    ValueTask<AccountIdentityReconciliationResult> ReconcileAsync(
        KeycloakUserId keycloakUserId,
        IReadOnlyCollection<GameIdentityId> gameIdentityIds,
        GamePlatform? platform,
        CancellationToken cancellationToken);
}

public sealed class AccountIdentityReconciler(
    IAccountRepository accountRepository,
    IGameAccountLinkRepository linkRepository,
    IWhitelistApplicationRepository whitelistRepository,
    TimeProvider timeProvider)
    : IAccountIdentityReconciler
{
    public async ValueTask<AccountIdentityReconciliationResult> ReconcileAsync(
        KeycloakUserId keycloakUserId,
        IReadOnlyCollection<GameIdentityId> gameIdentityIds,
        GamePlatform? platform,
        CancellationToken cancellationToken)
    {
        var links = new List<GameAccountLink>();
        foreach (var gameIdentityId in gameIdentityIds)
        {
            if (await linkRepository.FindByGameIdentityIdAsync(gameIdentityId, cancellationToken) is { } link)
            {
                links.Add(link);
            }
        }

        // The player's identities already sit on two different accounts. Consolidating them means
        // choosing which one's characters and balances to keep, which this module cannot see.
        var linkedAccountIds = links.Select(x => x.AccountId.Value).Distinct().ToList();
        if (linkedAccountIds.Count > 1)
        {
            return new AccountIdentityReconciliationResult.Conflict(
                "These game identities are already linked to different accounts.");
        }

        var linkedAccount = linkedAccountIds.Count == 1
            ? await accountRepository.FindByIdAsync(new AccountId(linkedAccountIds[0]), cancellationToken)
            : null;

        // Never re-point an identity at a different web account: that would silently hand over every
        // character and balance behind it. Checked up front so neither branch below has to.
        if (linkedAccount?.KeycloakUserId is { } owner && owner.Value != keycloakUserId.Value)
        {
            return new AccountIdentityReconciliationResult.Conflict(
                "A game identity is already linked to a different web account.");
        }

        var subjectAccount = await accountRepository.FindByKeycloakUserIdAsync(keycloakUserId, cancellationToken);

        Account target;
        var created = false;

        if (linkedAccount is null && subjectAccount is null)
        {
            var accountId = new AccountId(Guid.NewGuid());
            var accountCreated = new AccountCreated(accountId, keycloakUserId);
            target = Account.Create(accountCreated);
            accountRepository.StartStream(target, accountCreated);
            created = true;
        }
        else if (linkedAccount is null)
        {
            // Portal-first, and the second-platform path: the account exists, the identity is new.
            target = subjectAccount!;
        }
        else if (subjectAccount is null)
        {
            // Join-first player signing up: the account exists with links but no web identity.
            Attach(linkedAccount, keycloakUserId);
            target = linkedAccount;
        }
        else if (linkedAccount.Id.Value == subjectAccount.Id.Value)
        {
            target = linkedAccount;
        }
        else
        {
            // Two accounts for one player. The survivor is the one holding links, because gameservers
            // have been handed its id; the loser contributes its web identity and whitelist history.
            if (await HasLinksAsync(subjectAccount.Id, cancellationToken))
            {
                return new AccountIdentityReconciliationResult.Conflict(
                    "Both accounts hold game identities; merging them could discard characters.");
            }

            // Pre-checked rather than caught, so a refusal is a result and not an exception —
            // session-bootstrap is documented as always returning 200.
            if (subjectAccount.Status == AccountStatus.Locked)
            {
                return new AccountIdentityReconciliationResult.Conflict(
                    "The account holding this web identity is locked.");
            }

            accountRepository.Append(subjectAccount.Id, subjectAccount.AbsorbInto(linkedAccount.Id));
            Attach(linkedAccount, keycloakUserId);
            await ReassignWhitelistApplicationsAsync(subjectAccount.Id, linkedAccount.Id, cancellationToken);

            target = linkedAccount;
        }

        LinkMissingIdentities(target, gameIdentityIds, links, platform);

        // One SaveChanges for all three repositories: they share the module's scoped
        // IDocumentSession, so the new account, its links, the absorb and the reassignments all
        // commit together or not at all.
        await accountRepository.SaveChangesAsync(cancellationToken);

        return new AccountIdentityReconciliationResult.Resolved(target.Id, created);
    }

    private void Attach(Account account, KeycloakUserId keycloakUserId)
    {
        if (account.AttachKeycloakUser(keycloakUserId) is { } attached)
        {
            accountRepository.Append(account.Id, attached);
        }
    }

    private async ValueTask<bool> HasLinksAsync(AccountId accountId, CancellationToken cancellationToken)
        => (await linkRepository.ListByAccountAsync(accountId, cancellationToken)).Count > 0;

    private async ValueTask ReassignWhitelistApplicationsAsync(
        AccountId from, AccountId to, CancellationToken cancellationToken)
    {
        foreach (var application in await whitelistRepository.ListByAccountAsync(from, cancellationToken))
        {
            if (application.ReassignTo(to) is { } reassigned)
            {
                whitelistRepository.Append(application.Id, reassigned);
            }
        }
    }

    private void LinkMissingIdentities(
        Account target,
        IReadOnlyCollection<GameIdentityId> gameIdentityIds,
        List<GameAccountLink> existingLinks,
        GamePlatform? platform)
    {
        var alreadyLinked = existingLinks.Select(x => x.GameIdentityId.Value).ToHashSet();

        foreach (var gameIdentityId in gameIdentityIds.Where(x => !alreadyLinked.Contains(x.Value)))
        {
            linkRepository.Insert(
                GameAccountLink.Create(gameIdentityId, target.Id, platform, timeProvider.GetUtcNow()));
            accountRepository.Append(target.Id, new GameAccountLinked(target.Id, gameIdentityId, platform));
        }
    }
}
