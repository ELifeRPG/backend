using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// A player's account: their web identity plus their moderation state. Game identities are
/// <b>not</b> here — they hang off this account as <see cref="GameAccountLink"/> documents, because
/// Arma Reforger mints one Game Identity per game-platform combination and a player may hold
/// several.
/// </summary>
public class Account
{
    public AccountId Id { get; private set; }

    /// <summary>
    /// Null for a join-first account: the player joined a gameserver before having any web
    /// identity, and plays immediately. Filled in when they link. Also cleared by an absorb, so
    /// that a subject lookup stays single-valued.
    /// </summary>
    public KeycloakUserId? KeycloakUserId { get; private set; }

    public AccountStatus Status { get; private set; } = AccountStatus.Active;

    /// <summary>
    /// Set when this account lost an absorb: it is the same player as another account, and that one
    /// survived. Deliberately not an <see cref="AccountStatus"/> member — <c>Status</c> is the
    /// moderation state the gameserver gates on, and conflating "absorbed" with "blocked" would
    /// make that view ambiguous for no benefit.
    /// </summary>
    public AccountId? AbsorbedInto { get; private set; }

    public bool HasWebAccount => KeycloakUserId is not null;

    public static Account Create(AccountCreated domainEvent)
    {
        var account = new Account();
        account.Apply(domainEvent);
        return account;
    }

    /// <summary>
    /// Attaches the web identity Keycloak asserts for this account. Returns null when it is already
    /// that subject — this path is re-observed on every join and every <c>/accounts/me</c> call, so
    /// idempotence is the normal case, not an edge case. Throws for a <i>different</i> subject:
    /// re-pointing an account at another web identity would silently hand over every character and
    /// balance behind it.
    /// </summary>
    public KeycloakUserAttached? AttachKeycloakUser(KeycloakUserId keycloakUserId)
    {
        if (KeycloakUserId is { } existing)
        {
            if (existing.Value == keycloakUserId.Value)
            {
                return null;
            }

            throw new AccountStatusException("Account already has a different Keycloak user.");
        }

        var domainEvent = new KeycloakUserAttached(Id, keycloakUserId);
        Apply(domainEvent);
        return domainEvent;
    }

    /// <summary>
    /// Records that this account lost an absorb into <paramref name="survivor"/>.
    ///
    /// Refuses a locked account structurally. That guard is what stops an absorb laundering a ban:
    /// it moves a subject onto an <c>Active</c> survivor, and nothing else would carry the
    /// moderation state across. It is hard to reach today only because locking also disables the
    /// Keycloak user, so a banned player cannot authenticate to confirm a link — but
    /// <c>session-bootstrap</c> reads <see cref="Status"/>, never Keycloak's <c>enabled</c> flag, so
    /// a console-side unlock would open it.
    /// </summary>
    public AccountAbsorbed AbsorbInto(AccountId survivor)
    {
        if (survivor.Value == Id.Value)
        {
            throw new AccountStatusException("An account cannot be absorbed into itself.");
        }

        if (AbsorbedInto is not null)
        {
            throw new AccountStatusException("Account has already been absorbed.");
        }

        if (Status == AccountStatus.Locked)
        {
            throw new AccountStatusException("A locked account cannot be absorbed — that would discard its moderation state.");
        }

        var domainEvent = new AccountAbsorbed(Id, survivor);
        Apply(domainEvent);
        return domainEvent;
    }

    public AccountLocked Lock()
    {
        if (Status == AccountStatus.Locked)
        {
            throw new AccountStatusException("Account is already locked.");
        }

        var domainEvent = new AccountLocked(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public AccountUnlocked Unlock()
    {
        if (Status == AccountStatus.Active)
        {
            throw new AccountStatusException("Account is already active.");
        }

        var domainEvent = new AccountUnlocked(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(AccountCreated domainEvent)
    {
        Id = domainEvent.Id;
        KeycloakUserId = domainEvent.KeycloakUserId;
    }

    public void Apply(KeycloakUserAttached domainEvent) => KeycloakUserId = domainEvent.KeycloakUserId;

    // Clears the subject as well as recording the survivor: that is what keeps
    // FindByKeycloakUserIdAsync single-valued. Both appends commit in one Marten transaction, so no
    // window exists in which two documents hold the same subject.
    public void Apply(AccountAbsorbed domainEvent)
    {
        AbsorbedInto = domainEvent.AbsorbedIntoAccountId;
        KeycloakUserId = null;
    }

    public void Apply(AccountLocked domainEvent) => Status = AccountStatus.Locked;

    public void Apply(AccountUnlocked domainEvent) => Status = AccountStatus.Active;
}
