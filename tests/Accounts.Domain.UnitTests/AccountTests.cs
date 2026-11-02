using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Accounts.Domain.UnitTests;

public class AccountTests
{
    private static Account CreateAccount(KeycloakUserId? keycloakUserId = null)
        => Account.Create(new AccountCreated(
            new AccountId(Guid.NewGuid()),
            keycloakUserId ?? new KeycloakUserId(Guid.NewGuid())));

    /// <summary>An account created because an unknown game identity joined: no web identity yet.</summary>
    private static Account CreateJoinFirstAccount()
        => Account.Create(new AccountCreated(new AccountId(Guid.NewGuid()), KeycloakUserId: null));

    [Fact]
    public void Create_SetsPropertiesFromEvent()
    {
        var accountId = new AccountId(Guid.NewGuid());
        var keycloakUserId = new KeycloakUserId(Guid.NewGuid());

        var account = Account.Create(new AccountCreated(accountId, keycloakUserId));

        Assert.Equal(accountId, account.Id);
        Assert.Equal(keycloakUserId, account.KeycloakUserId);
        Assert.True(account.HasWebAccount);
        Assert.Null(account.AbsorbedInto);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Create_WithoutAKeycloakUser_IsPlayableButHasNoWebAccount()
    {
        var account = CreateJoinFirstAccount();

        Assert.Null(account.KeycloakUserId);
        Assert.False(account.HasWebAccount);
        // Join-first is a normal starting state, not a degraded one — the player plays at once.
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Lock_WhenActive_SetsStatusToLocked()
    {
        var account = CreateAccount();

        account.Lock();

        Assert.Equal(AccountStatus.Locked, account.Status);
    }

    [Fact]
    public void Lock_WhenActive_ReturnsEventForThisAccount()
    {
        var account = CreateAccount();

        var domainEvent = account.Lock();

        Assert.Equal(account.Id, domainEvent.Id);
    }

    [Fact]
    public void Lock_WhenAlreadyLocked_Throws()
    {
        var account = CreateAccount();
        account.Lock();

        Assert.Throws<AccountStatusException>(() => account.Lock());
    }

    [Fact]
    public void Unlock_WhenLocked_SetsStatusToActive()
    {
        var account = CreateAccount();
        account.Lock();

        account.Unlock();

        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Unlock_WhenAlreadyActive_Throws()
    {
        var account = CreateAccount();

        Assert.Throws<AccountStatusException>(() => account.Unlock());
    }

    [Fact]
    public void Apply_ReplayingCreatedThenLocked_ResultsInLockedAccount()
    {
        var accountId = new AccountId(Guid.NewGuid());

        var account = Account.Create(new AccountCreated(accountId, new KeycloakUserId(Guid.NewGuid())));
        account.Apply(new AccountLocked(accountId));

        Assert.Equal(AccountStatus.Locked, account.Status);
    }

    // --- attaching a web identity to a join-first account -------------------------------

    [Fact]
    public void AttachKeycloakUser_WhenNoneYet_AttachesIt()
    {
        var account = CreateJoinFirstAccount();
        var keycloakUserId = new KeycloakUserId(Guid.NewGuid());

        var domainEvent = account.AttachKeycloakUser(keycloakUserId);

        Assert.NotNull(domainEvent);
        Assert.Equal(account.Id, domainEvent.Id);
        Assert.Equal(keycloakUserId, account.KeycloakUserId);
        Assert.True(account.HasWebAccount);
    }

    /// <summary>
    /// Re-observed on every join and every /accounts/me call, so idempotence is the normal case
    /// rather than an edge case — it returns null instead of throwing or appending a second event.
    /// </summary>
    [Fact]
    public void AttachKeycloakUser_WhenAlreadyThatSubject_IsANoOp()
    {
        var keycloakUserId = new KeycloakUserId(Guid.NewGuid());
        var account = CreateAccount(keycloakUserId);

        Assert.Null(account.AttachKeycloakUser(keycloakUserId));
        Assert.Equal(keycloakUserId, account.KeycloakUserId);
    }

    /// <summary>
    /// Re-pointing an account at a different web identity would hand over every character and
    /// balance behind it, so it is refused rather than treated as a rebind.
    /// </summary>
    [Fact]
    public void AttachKeycloakUser_WhenADifferentSubject_Throws()
    {
        var account = CreateAccount();

        Assert.Throws<AccountStatusException>(
            () => account.AttachKeycloakUser(new KeycloakUserId(Guid.NewGuid())));
    }

    // --- the absorb ---------------------------------------------------------------------

    [Fact]
    public void AbsorbInto_RecordsTheSurvivorAndClearsTheSubject()
    {
        var account = CreateAccount();
        var survivor = new AccountId(Guid.NewGuid());

        var domainEvent = account.AbsorbInto(survivor);

        Assert.Equal(account.Id, domainEvent.Id);
        Assert.Equal(survivor, domainEvent.AbsorbedIntoAccountId);
        Assert.Equal(survivor, account.AbsorbedInto);
        // Cleared so a subject lookup stays single-valued while both rows exist.
        Assert.Null(account.KeycloakUserId);
    }

    [Fact]
    public void AbsorbInto_WhenAlreadyAbsorbed_Throws()
    {
        var account = CreateAccount();
        account.AbsorbInto(new AccountId(Guid.NewGuid()));

        Assert.Throws<AccountStatusException>(() => account.AbsorbInto(new AccountId(Guid.NewGuid())));
    }

    [Fact]
    public void AbsorbInto_Itself_Throws()
    {
        var account = CreateAccount();

        Assert.Throws<AccountStatusException>(() => account.AbsorbInto(account.Id));
    }

    /// <summary>
    /// The ban-laundering guard: absorbing moves a subject onto an Active survivor, and nothing else
    /// would carry the moderation state across. session-bootstrap gates on Status, never on
    /// Keycloak's enabled flag, so this cannot be left to Keycloak to enforce.
    /// </summary>
    [Fact]
    public void AbsorbInto_WhenLocked_Throws()
    {
        var account = CreateAccount();
        account.Lock();

        Assert.Throws<AccountStatusException>(() => account.AbsorbInto(new AccountId(Guid.NewGuid())));
    }

    // --- replay -------------------------------------------------------------------------

    /// <summary>
    /// The projection is inline, so the document IS what queries read. Without Apply for this event
    /// the subject would never land and /accounts/me would create a fresh account on every call.
    /// </summary>
    [Fact]
    public void Apply_ReplayingCreatedJoinFirstThenAttached_ResultsInAWebAccount()
    {
        var accountId = new AccountId(Guid.NewGuid());
        var keycloakUserId = new KeycloakUserId(Guid.NewGuid());

        var account = Account.Create(new AccountCreated(accountId, KeycloakUserId: null));
        account.Apply(new KeycloakUserAttached(accountId, keycloakUserId));

        Assert.Equal(keycloakUserId, account.KeycloakUserId);
        Assert.True(account.HasWebAccount);
    }

    [Fact]
    public void Apply_ReplayingCreatedThenAbsorbed_ClearsTheSubjectAndRecordsTheSurvivor()
    {
        var accountId = new AccountId(Guid.NewGuid());
        var survivor = new AccountId(Guid.NewGuid());

        var account = Account.Create(new AccountCreated(accountId, new KeycloakUserId(Guid.NewGuid())));
        account.Apply(new AccountAbsorbed(accountId, survivor));

        Assert.Equal(survivor, account.AbsorbedInto);
        Assert.Null(account.KeycloakUserId);
    }
}
