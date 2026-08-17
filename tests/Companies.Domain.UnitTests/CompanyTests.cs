using ELifeRPG.Companies.Domain;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Companies.Domain.Exceptions;
using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Companies.Domain.UnitTests;

public class CompanyTests
{
    private static Company CreateCompany(out CompanyPositionId ownerPositionId, out CompanyPositionId defaultPositionId)
    {
        var companyId = new CompanyId(Guid.NewGuid());
        ownerPositionId = new CompanyPositionId(Guid.NewGuid());
        defaultPositionId = new CompanyPositionId(Guid.NewGuid());
        var domainEvent = new CompanyCreated(companyId, "Acme Corp", ownerPositionId, defaultPositionId);
        return Company.Create(domainEvent);
    }

    [Fact]
    public void Create_SeedsOwnerAndRookiePositions()
    {
        var company = CreateCompany(out var ownerPositionId, out var defaultPositionId);

        Assert.Equal(2, company.Positions.Count);
        var owner = company.Positions.Single(x => x.Id == ownerPositionId);
        Assert.Equal("Owner", owner.Name);
        Assert.Equal(
            CompanyPermissions.ManageCompany | CompanyPermissions.ManageMembers | CompanyPermissions.ManageWages | CompanyPermissions.ManageFinances | CompanyPermissions.ManageShops,
            owner.Permissions);
        var rookie = company.Positions.Single(x => x.Id == defaultPositionId);
        Assert.Equal("Rookie", rookie.Name);
        Assert.Equal(CompanyPermissions.None, rookie.Permissions);
        Assert.Empty(company.Memberships);
    }

    [Fact]
    public void AddMember_WithoutPosition_DefaultsToRookieNotOwner()
    {
        var company = CreateCompany(out _, out var defaultPositionId);
        var characterId = new CharacterId(Guid.NewGuid());

        var domainEvent = company.AddMember(characterId);

        Assert.Equal(defaultPositionId, domainEvent.PositionId);
        Assert.Contains(company.Memberships, m => m.CharacterId == characterId && m.PositionId == defaultPositionId);
    }

    [Fact]
    public void AddMember_WithExplicitOwnerPosition_AssignsOwnerPosition()
    {
        var company = CreateCompany(out var ownerPositionId, out _);
        var characterId = new CharacterId(Guid.NewGuid());

        var domainEvent = company.AddMember(characterId, ownerPositionId);

        Assert.Equal(ownerPositionId, domainEvent.PositionId);
    }

    [Fact]
    public void IssueShares_FirstPurchase_AddsShareGrant()
    {
        var company = CreateCompany(out _, out _);
        var buyer = new CharacterId(Guid.NewGuid());

        var domainEvent = company.IssueShares(buyer, 10);

        Assert.Equal(10, domainEvent.Quantity);
        Assert.Contains(company.Shares, s => s.CharacterId == buyer && s.Quantity == 10);
    }

    [Fact]
    public void IssueShares_SecondPurchaseBySameBuyer_AccumulatesQuantity()
    {
        var company = CreateCompany(out _, out _);
        var buyer = new CharacterId(Guid.NewGuid());
        company.IssueShares(buyer, 10);

        company.IssueShares(buyer, 5);

        var grant = Assert.Single(company.Shares, s => s.CharacterId == buyer);
        Assert.Equal(15, grant.Quantity);
    }

    [Fact]
    public void IssueShares_WithNonPositiveQuantity_Throws()
    {
        var company = CreateCompany(out _, out _);
        var buyer = new CharacterId(Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() => company.IssueShares(buyer, 0));
    }

    [Fact]
    public void AddMember_SameCharacterTwice_Throws()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());
        company.AddMember(characterId);

        Assert.Throws<AlreadyMemberException>(() => company.AddMember(characterId));
    }

    [Fact]
    public void AddMember_WithUnknownPosition_Throws()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());
        var unknownPositionId = new CompanyPositionId(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => company.AddMember(characterId, unknownPositionId));
    }

    [Fact]
    public void Apply_ReplayingCreatedThenMemberAdded_ResultsInSameMembership()
    {
        var companyId = new CompanyId(Guid.NewGuid());
        var ownerPositionId = new CompanyPositionId(Guid.NewGuid());
        var defaultPositionId = new CompanyPositionId(Guid.NewGuid());
        var characterId = new CharacterId(Guid.NewGuid());

        var company = Company.Create(new CompanyCreated(companyId, "Acme Corp", ownerPositionId, defaultPositionId));
        company.Apply(new MemberAdded(companyId, characterId, defaultPositionId));

        Assert.Single(company.Memberships);
        Assert.Equal(characterId, company.Memberships[0].CharacterId);
    }

    [Fact]
    public void SubmitApplication_ForNewApplicant_AddsPendingApplication()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());

        var domainEvent = company.SubmitApplication(characterId, "Please let me in.");

        Assert.Equal(characterId, domainEvent.CharacterId);
        Assert.Equal("Please let me in.", domainEvent.Message);
        var application = Assert.Single(company.Applications);
        Assert.Equal(domainEvent.ApplicationId, application.Id);
        Assert.Equal(CompanyApplicationStatus.Pending, application.Status);
    }

    [Fact]
    public void SubmitApplication_ForExistingMember_ThrowsAlreadyMember()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());
        company.AddMember(characterId);

        Assert.Throws<AlreadyMemberException>(() => company.SubmitApplication(characterId, "Let me in again."));
    }

    [Fact]
    public void SubmitApplication_WithExistingOpenApplication_ThrowsDuplicateApplication()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());
        company.SubmitApplication(characterId, "First try.");

        Assert.Throws<DuplicateApplicationException>(() => company.SubmitApplication(characterId, "Second try."));
    }

    [Fact]
    public void Apply_ReplayingApplicationSubmitted_ResultsInSamePendingApplication()
    {
        var companyId = new CompanyId(Guid.NewGuid());
        var ownerPositionId = new CompanyPositionId(Guid.NewGuid());
        var defaultPositionId = new CompanyPositionId(Guid.NewGuid());
        var characterId = new CharacterId(Guid.NewGuid());
        var applicationId = new CompanyApplicationId(Guid.NewGuid());

        var company = Company.Create(new CompanyCreated(companyId, "Acme Corp", ownerPositionId, defaultPositionId));
        company.Apply(new ApplicationSubmitted(companyId, applicationId, characterId, "Hire me."));

        Assert.Single(company.Applications);
        Assert.Equal(characterId, company.Applications[0].CharacterId);
        Assert.Equal(CompanyApplicationStatus.Pending, company.Applications[0].Status);
    }

    [Fact]
    public void ConfirmApplication_ForPendingApplication_SetsInProgress()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");

        var domainEvent = company.ConfirmApplication(submitted.ApplicationId);

        Assert.Equal(submitted.ApplicationId, domainEvent.ApplicationId);
        Assert.Equal(CompanyApplicationStatus.InProgress, company.Applications.Single().Status);
    }

    [Fact]
    public void ConfirmApplication_ForUnknownApplication_ThrowsApplicationNotFound()
    {
        var company = CreateCompany(out _, out _);

        Assert.Throws<ApplicationNotFoundException>(() => company.ConfirmApplication(new CompanyApplicationId(Guid.NewGuid())));
    }

    [Fact]
    public void ConfirmApplication_ForAlreadyConfirmedApplication_ThrowsInvalidApplicationState()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.ConfirmApplication(submitted.ApplicationId);

        Assert.Throws<InvalidApplicationStateException>(() => company.ConfirmApplication(submitted.ApplicationId));
    }

    [Fact]
    public void AcceptApplication_ForPendingApplication_GrantsDefaultPosition()
    {
        var company = CreateCompany(out _, out var defaultPositionId);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");

        var (acceptedEvent, memberAddedEvent) = company.AcceptApplication(submitted.ApplicationId);

        Assert.Equal(submitted.ApplicationId, acceptedEvent.ApplicationId);
        Assert.Equal(defaultPositionId, memberAddedEvent.PositionId);
        Assert.Equal(CompanyApplicationStatus.Accepted, company.Applications.Single().Status);
        Assert.Contains(company.Memberships, m => m.CharacterId == submitted.CharacterId && m.PositionId == defaultPositionId);
    }

    [Fact]
    public void AcceptApplication_ForInProgressApplication_GrantsDefaultPosition()
    {
        var company = CreateCompany(out _, out var defaultPositionId);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.ConfirmApplication(submitted.ApplicationId);

        var (_, memberAddedEvent) = company.AcceptApplication(submitted.ApplicationId);

        Assert.Equal(defaultPositionId, memberAddedEvent.PositionId);
    }

    [Fact]
    public void AcceptApplication_ForUnknownApplication_ThrowsApplicationNotFound()
    {
        var company = CreateCompany(out _, out _);

        Assert.Throws<ApplicationNotFoundException>(() => company.AcceptApplication(new CompanyApplicationId(Guid.NewGuid())));
    }

    [Fact]
    public void AcceptApplication_ForAlreadyAcceptedApplication_ThrowsInvalidApplicationState()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.AcceptApplication(submitted.ApplicationId);

        Assert.Throws<InvalidApplicationStateException>(() => company.AcceptApplication(submitted.ApplicationId));
    }

    [Fact]
    public void AcceptApplication_WhenCharacterBecameMemberSeparately_ThrowsAlreadyMemberWithoutMutatingApplication()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.AddMember(submitted.CharacterId);

        Assert.Throws<AlreadyMemberException>(() => company.AcceptApplication(submitted.ApplicationId));
        Assert.Equal(CompanyApplicationStatus.Pending, company.Applications.Single().Status);
    }

    [Fact]
    public void DenyApplication_ForPendingApplication_SetsDenied()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");

        var domainEvent = company.DenyApplication(submitted.ApplicationId);

        Assert.Equal(submitted.ApplicationId, domainEvent.ApplicationId);
        Assert.Equal(CompanyApplicationStatus.Denied, company.Applications.Single().Status);
    }

    [Fact]
    public void DenyApplication_ForInProgressApplication_SetsDenied()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.ConfirmApplication(submitted.ApplicationId);

        company.DenyApplication(submitted.ApplicationId);

        Assert.Equal(CompanyApplicationStatus.Denied, company.Applications.Single().Status);
    }

    [Fact]
    public void DenyApplication_ForUnknownApplication_ThrowsApplicationNotFound()
    {
        var company = CreateCompany(out _, out _);

        Assert.Throws<ApplicationNotFoundException>(() => company.DenyApplication(new CompanyApplicationId(Guid.NewGuid())));
    }

    [Fact]
    public void DenyApplication_ForAlreadyDeniedApplication_ThrowsInvalidApplicationState()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.DenyApplication(submitted.ApplicationId);

        Assert.Throws<InvalidApplicationStateException>(() => company.DenyApplication(submitted.ApplicationId));
    }

    [Fact]
    public void AcceptApplication_ForAlreadyDeniedApplication_ThrowsInvalidApplicationState()
    {
        var company = CreateCompany(out _, out _);
        var submitted = company.SubmitApplication(new CharacterId(Guid.NewGuid()), "Hire me.");
        company.DenyApplication(submitted.ApplicationId);

        Assert.Throws<InvalidApplicationStateException>(() => company.AcceptApplication(submitted.ApplicationId));
    }

    [Fact]
    public void SubmitApplication_AfterPriorDenial_Succeeds()
    {
        var company = CreateCompany(out _, out _);
        var characterId = new CharacterId(Guid.NewGuid());
        var firstSubmission = company.SubmitApplication(characterId, "First try.");
        company.DenyApplication(firstSubmission.ApplicationId);

        var secondSubmission = company.SubmitApplication(characterId, "Second try.");

        Assert.Equal(2, company.Applications.Count);
        Assert.Equal(CompanyApplicationStatus.Pending, company.Applications.Single(a => a.Id == secondSubmission.ApplicationId).Status);
    }
}
