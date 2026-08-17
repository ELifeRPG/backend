using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using Marten.Events.Aggregation;

namespace ELifeRPG.Accounts.Infrastructure.Common;

public sealed partial class WhitelistApplicationProjection : SingleStreamProjection<WhitelistApplication, WhitelistApplicationId>
{
    public static WhitelistApplication Create(WhitelistApplicationSubmitted domainEvent) => WhitelistApplication.Create(domainEvent);

    public void Apply(WhitelistApplication application, WhitelistApplicationReviewStarted domainEvent) => application.Apply(domainEvent);

    public void Apply(WhitelistApplication application, WhitelistApplicationApproved domainEvent) => application.Apply(domainEvent);

    public void Apply(WhitelistApplication application, WhitelistApplicationRejected domainEvent) => application.Apply(domainEvent);
}
