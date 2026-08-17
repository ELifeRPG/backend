using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Accounts.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Accounts.Domain.UnitTests;

public sealed class WhitelistApplicationTests
{
    private static WhitelistApplication CreateOpen() => WhitelistApplication.Create(new WhitelistApplicationSubmitted(
        new WhitelistApplicationId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "gameserver-dev", "let me in please"));

    [Fact]
    public void Create_SetsFieldsFromEvent_AndStatusOpen()
    {
        var accountId = new AccountId(Guid.NewGuid());
        var id = new WhitelistApplicationId(Guid.NewGuid());
        var domainEvent = new WhitelistApplicationSubmitted(id, accountId, "gameserver-dev", "text");

        var application = WhitelistApplication.Create(domainEvent);

        Assert.Equal(id, application.Id);
        Assert.Equal(accountId, application.AccountId);
        Assert.Equal("gameserver-dev", application.ServerClientId);
        Assert.Equal("text", application.ApplicationText);
        Assert.Equal(WhitelistApplicationStatus.Open, application.Status);
    }

    [Fact]
    public void StartReview_FromOpen_TransitionsToInReview()
    {
        var application = CreateOpen();

        var domainEvent = application.StartReview();

        Assert.NotNull(domainEvent);
        Assert.Equal(WhitelistApplicationStatus.InReview, application.Status);
    }

    [Fact]
    public void StartReview_AlreadyInReview_IsIdempotentNoOp()
    {
        var application = CreateOpen();
        application.StartReview();

        var domainEvent = application.StartReview();

        Assert.Null(domainEvent);
        Assert.Equal(WhitelistApplicationStatus.InReview, application.Status);
    }

    [Fact]
    public void StartReview_AlreadyApproved_Throws()
    {
        var application = CreateOpen();
        application.StartReview();
        application.Approve();

        Assert.Throws<WhitelistApplicationStatusException>(() => application.StartReview());
    }

    [Fact]
    public void Approve_FromInReview_TransitionsToApproved()
    {
        var application = CreateOpen();
        application.StartReview();

        var domainEvent = application.Approve();

        Assert.NotNull(domainEvent);
        Assert.Equal(WhitelistApplicationStatus.Approved, application.Status);
    }

    [Fact]
    public void Approve_AlreadyApproved_IsIdempotentNoOp()
    {
        var application = CreateOpen();
        application.StartReview();
        application.Approve();

        var domainEvent = application.Approve();

        Assert.Null(domainEvent);
        Assert.Equal(WhitelistApplicationStatus.Approved, application.Status);
    }

    [Fact]
    public void Approve_FromOpen_Throws()
    {
        var application = CreateOpen();

        Assert.Throws<WhitelistApplicationStatusException>(() => application.Approve());
    }

    [Fact]
    public void Approve_AlreadyRejected_Throws()
    {
        var application = CreateOpen();
        application.StartReview();
        application.Reject();

        Assert.Throws<WhitelistApplicationStatusException>(() => application.Approve());
    }

    [Fact]
    public void Reject_FromInReview_TransitionsToRejected()
    {
        var application = CreateOpen();
        application.StartReview();

        var domainEvent = application.Reject();

        Assert.NotNull(domainEvent);
        Assert.Equal(WhitelistApplicationStatus.Rejected, application.Status);
    }

    [Fact]
    public void Reject_AlreadyRejected_IsIdempotentNoOp()
    {
        var application = CreateOpen();
        application.StartReview();
        application.Reject();

        var domainEvent = application.Reject();

        Assert.Null(domainEvent);
        Assert.Equal(WhitelistApplicationStatus.Rejected, application.Status);
    }
}
