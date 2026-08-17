namespace ELifeRPG.Companies.Domain.Events;

public sealed record ApplicationSubmitted(CompanyId Id, CompanyApplicationId ApplicationId, CharacterId CharacterId, string Message);
