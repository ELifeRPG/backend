namespace ELifeRPG.Companies.Domain.Events;

public sealed record ApplicationDenied(CompanyId Id, CompanyApplicationId ApplicationId);
