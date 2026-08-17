using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Domain;

public sealed record CompanyShareGrant(CharacterId CharacterId, int Quantity);

public class Company
{
    public CompanyId Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public List<CompanyPosition> Positions { get; private set; } = [];

    public List<CompanyMembership> Memberships { get; private set; } = [];

    public List<CompanyApplication> Applications { get; private set; } = [];

    public List<CompanyShareGrant> Shares { get; private set; } = [];

    public static Company Create(CompanyCreated domainEvent)
    {
        var company = new Company();
        company.Apply(domainEvent);
        return company;
    }

    /// <summary>
    /// Adds a member. If no position is given, defaults to the lowest-ranked position (highest
    /// Ordering) — matches the legacy app's Company.AddMembership default.
    /// </summary>
    public MemberAdded AddMember(CharacterId characterId, CompanyPositionId? positionId = null)
    {
        if (Memberships.Any(x => x.CharacterId == characterId))
        {
            throw new AlreadyMemberException("Character is already a member of this company.");
        }

        var resolvedPositionId = positionId ?? Positions.OrderByDescending(x => x.Ordering).First().Id;
        if (Positions.All(x => x.Id != resolvedPositionId))
        {
            throw new InvalidOperationException("Unknown company position.");
        }

        var domainEvent = new MemberAdded(Id, characterId, resolvedPositionId);
        Apply(domainEvent);
        return domainEvent;
    }

    public ApplicationSubmitted SubmitApplication(CharacterId characterId, string message)
    {
        if (Memberships.Any(x => x.CharacterId == characterId))
        {
            throw new AlreadyMemberException("Character is already a member of this company.");
        }

        if (Applications.Any(x => x.CharacterId == characterId && x.Status is CompanyApplicationStatus.Pending or CompanyApplicationStatus.InProgress))
        {
            throw new DuplicateApplicationException("Character already has an open application to this company.");
        }

        var domainEvent = new ApplicationSubmitted(Id, new CompanyApplicationId(Guid.NewGuid()), characterId, message);
        Apply(domainEvent);
        return domainEvent;
    }

    public ApplicationConfirmed ConfirmApplication(CompanyApplicationId applicationId)
    {
        var application = Applications.SingleOrDefault(x => x.Id == applicationId)
            ?? throw new ApplicationNotFoundException("Unknown application.");

        if (application.Status != CompanyApplicationStatus.Pending)
        {
            throw new InvalidApplicationStateException("Application must be Pending to be confirmed.");
        }

        var domainEvent = new ApplicationConfirmed(Id, applicationId);
        Apply(domainEvent);
        return domainEvent;
    }

    public (ApplicationAccepted AcceptedEvent, MemberAdded MemberAddedEvent) AcceptApplication(CompanyApplicationId applicationId)
    {
        var application = Applications.SingleOrDefault(x => x.Id == applicationId)
            ?? throw new ApplicationNotFoundException("Unknown application.");

        if (application.Status is CompanyApplicationStatus.Accepted or CompanyApplicationStatus.Denied)
        {
            throw new InvalidApplicationStateException("Application has already been decided.");
        }

        var memberAddedEvent = AddMember(application.CharacterId);
        var acceptedEvent = new ApplicationAccepted(Id, applicationId);
        Apply(acceptedEvent);

        return (acceptedEvent, memberAddedEvent);
    }

    public ApplicationDenied DenyApplication(CompanyApplicationId applicationId)
    {
        var application = Applications.SingleOrDefault(x => x.Id == applicationId)
            ?? throw new ApplicationNotFoundException("Unknown application.");

        if (application.Status is CompanyApplicationStatus.Accepted or CompanyApplicationStatus.Denied)
        {
            throw new InvalidApplicationStateException("Application has already been decided.");
        }

        var domainEvent = new ApplicationDenied(Id, applicationId);
        Apply(domainEvent);
        return domainEvent;
    }

    public CompanySharesIssued IssueShares(CharacterId buyer, int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");
        }

        var domainEvent = new CompanySharesIssued(Id, buyer, quantity);
        Apply(domainEvent);
        return domainEvent;
    }

    public void Apply(CompanyCreated domainEvent)
    {
        Id = domainEvent.Id;
        Name = domainEvent.Name;
        Positions =
        [
            new CompanyPosition(
                domainEvent.OwnerPositionId,
                "Owner",
                1,
                CompanyPermissions.ManageCompany | CompanyPermissions.ManageMembers | CompanyPermissions.ManageWages | CompanyPermissions.ManageFinances | CompanyPermissions.ManageShops),
            new CompanyPosition(domainEvent.DefaultPositionId, "Rookie", 10, CompanyPermissions.None),
        ];
        Memberships = [];
    }

    public void Apply(MemberAdded domainEvent) => Memberships.Add(new CompanyMembership(domainEvent.CharacterId, domainEvent.PositionId));

    public void Apply(ApplicationSubmitted domainEvent) =>
        Applications.Add(new CompanyApplication(domainEvent.ApplicationId, domainEvent.CharacterId, domainEvent.Message, CompanyApplicationStatus.Pending));

    public void Apply(ApplicationConfirmed domainEvent)
    {
        var index = Applications.FindIndex(x => x.Id == domainEvent.ApplicationId);
        Applications[index] = Applications[index] with { Status = CompanyApplicationStatus.InProgress };
    }

    public void Apply(ApplicationAccepted domainEvent)
    {
        var index = Applications.FindIndex(x => x.Id == domainEvent.ApplicationId);
        Applications[index] = Applications[index] with { Status = CompanyApplicationStatus.Accepted };
    }

    public void Apply(ApplicationDenied domainEvent)
    {
        var index = Applications.FindIndex(x => x.Id == domainEvent.ApplicationId);
        Applications[index] = Applications[index] with { Status = CompanyApplicationStatus.Denied };
    }

    public void Apply(CompanySharesIssued domainEvent)
    {
        var index = Shares.FindIndex(x => x.CharacterId == domainEvent.Buyer);
        if (index >= 0)
        {
            Shares[index] = Shares[index] with { Quantity = Shares[index].Quantity + domainEvent.Quantity };
        }
        else
        {
            Shares.Add(new CompanyShareGrant(domainEvent.Buyer, domainEvent.Quantity));
        }
    }
}
