using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// Prior to the hive-wide whitelist migration, <see cref="Events.WhitelistApplicationSubmitted"/>
/// carried a <c>ServerClientId</c> scoping the application to one gameserver. That field is gone from
/// the event record, so it is silently ignored (not an error) when replaying pre-migration event
/// streams that still contain it. The practical effect: an application that was <c>Approved</c> for
/// one specific server before this migration reads as approved everywhere in the hive the first time
/// it is replayed after upgrade — indistinguishable from an approval that was always hive-wide. That
/// is the intended behavior going forward ("approved once, play anywhere"), but it is a retroactive
/// widening of access for approvals that predate it. An operator upgrading a deployment with real
/// approval history should decide whether to re-review those approvals rather than silently inherit
/// them as hive-wide.
/// </summary>
public class WhitelistApplication
{
    public WhitelistApplicationId Id { get; private set; }

    public AccountId AccountId { get; private set; }

    public string ApplicationText { get; private set; } = string.Empty;

    public WhitelistApplicationStatus Status { get; private set; } = WhitelistApplicationStatus.Open;

    public static WhitelistApplication Create(WhitelistApplicationSubmitted domainEvent)
    {
        var application = new WhitelistApplication();
        application.Apply(domainEvent);
        return application;
    }

    public WhitelistApplicationReviewStarted? StartReview()
    {
        if (Status == WhitelistApplicationStatus.InReview)
        {
            return null;
        }

        if (Status != WhitelistApplicationStatus.Open)
        {
            throw new WhitelistApplicationStatusException("Application must be Open to start review.");
        }

        var domainEvent = new WhitelistApplicationReviewStarted(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public WhitelistApplicationApproved? Approve()
    {
        if (Status == WhitelistApplicationStatus.Approved)
        {
            return null;
        }

        if (Status != WhitelistApplicationStatus.InReview)
        {
            throw new WhitelistApplicationStatusException("Application must be InReview to be approved.");
        }

        var domainEvent = new WhitelistApplicationApproved(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public WhitelistApplicationRejected? Reject()
    {
        if (Status == WhitelistApplicationStatus.Rejected)
        {
            return null;
        }

        if (Status != WhitelistApplicationStatus.InReview)
        {
            throw new WhitelistApplicationStatusException("Application must be InReview to be rejected.");
        }

        var domainEvent = new WhitelistApplicationRejected(Id);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(WhitelistApplicationSubmitted domainEvent)
    {
        Id = domainEvent.Id;
        AccountId = domainEvent.AccountId;
        ApplicationText = domainEvent.ApplicationText;
    }

    public void Apply(WhitelistApplicationReviewStarted domainEvent) => Status = WhitelistApplicationStatus.InReview;

    public void Apply(WhitelistApplicationApproved domainEvent) => Status = WhitelistApplicationStatus.Approved;

    public void Apply(WhitelistApplicationRejected domainEvent) => Status = WhitelistApplicationStatus.Rejected;
}
