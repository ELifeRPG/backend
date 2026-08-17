using ELifeRPG.Companies.Domain;
using ELifeRPG.Companies.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Marten.Events.Aggregation;

namespace ELifeRPG.Companies.Infrastructure.Common;

public sealed partial class CompanyProjection : SingleStreamProjection<Company, CompanyId>
{
    public static Company Create(CompanyCreated domainEvent) => Company.Create(domainEvent);

    public void Apply(Company company, MemberAdded domainEvent) => company.Apply(domainEvent);

    public void Apply(Company company, ApplicationSubmitted domainEvent) => company.Apply(domainEvent);

    public void Apply(Company company, ApplicationConfirmed domainEvent) => company.Apply(domainEvent);

    public void Apply(Company company, ApplicationAccepted domainEvent) => company.Apply(domainEvent);

    public void Apply(Company company, ApplicationDenied domainEvent) => company.Apply(domainEvent);

    public void Apply(Company company, CompanySharesIssued domainEvent) => company.Apply(domainEvent);
}
