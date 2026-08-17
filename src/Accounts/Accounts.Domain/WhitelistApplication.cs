using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;

namespace ELifeRPG.Accounts.Domain;

public class WhitelistApplication
{
    public WhitelistApplicationId Id { get; private set; }

    public AccountId AccountId { get; private set; }

    public string ServerClientId { get; private set; } = string.Empty;

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
        ServerClientId = domainEvent.ServerClientId;
        ApplicationText = domainEvent.ApplicationText;
    }

    public void Apply(WhitelistApplicationReviewStarted domainEvent) => Status = WhitelistApplicationStatus.InReview;

    public void Apply(WhitelistApplicationApproved domainEvent) => Status = WhitelistApplicationStatus.Approved;

    public void Apply(WhitelistApplicationRejected domainEvent) => Status = WhitelistApplicationStatus.Rejected;
}
