using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten.Events.Aggregation;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed partial class AccountProjection : SingleStreamProjection<Account, AccountId>
{
    public static Account Create(AccountCreated domainEvent) => Account.Create(domainEvent);

    // This projection is inline, so the document IS what queries read, and it silently ignores
    // events it has no Apply for. A missing handler is therefore not cosmetic staleness: without
    // Apply(KeycloakUserAttached) below, FindByKeycloakUserIdAsync would never find the account and
    // /accounts/me would create a fresh one on every call. Every new account event needs a handler
    // here AND an integration test that appends through the repository, saves, and re-reads through
    // a finder — the unit replay tests call Apply directly and bypass this class entirely.
    //
    // The one deliberate exception is GameAccountLinked: Account holds no game-identity state (it
    // lives in GameAccountLink documents), so there is genuinely nothing to project and the event
    // is write-only audit.
    public void Apply(Account account, KeycloakUserAttached domainEvent) => account.Apply(domainEvent);

    public void Apply(Account account, AccountAbsorbed domainEvent) => account.Apply(domainEvent);

    public void Apply(Account account, AccountLocked domainEvent) => account.Apply(domainEvent);

    public void Apply(Account account, AccountUnlocked domainEvent) => account.Apply(domainEvent);
}
