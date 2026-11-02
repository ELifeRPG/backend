using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using Marten.Events.Aggregation;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed partial class WhitelistApplicationProjection : SingleStreamProjection<WhitelistApplication, WhitelistApplicationId>
{
    public static WhitelistApplication Create(WhitelistApplicationSubmitted domainEvent) => WhitelistApplication.Create(domainEvent);

    // Without this the absorb's re-pointing would not reach the document, so FindApprovedAsync
    // would still answer for the losing account and an already-whitelisted player would read as
    // not_whitelisted on their next join.
    public void Apply(WhitelistApplication application, WhitelistApplicationAccountReassigned domainEvent) => application.Apply(domainEvent);

    public void Apply(WhitelistApplication application, WhitelistApplicationReviewStarted domainEvent) => application.Apply(domainEvent);

    public void Apply(WhitelistApplication application, WhitelistApplicationApproved domainEvent) => application.Apply(domainEvent);

    public void Apply(WhitelistApplication application, WhitelistApplicationRejected domainEvent) => application.Apply(domainEvent);
}
