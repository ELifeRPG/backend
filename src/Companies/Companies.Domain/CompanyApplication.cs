namespace ELifeRPG.Companies.Domain;

public sealed record CompanyApplication(CompanyApplicationId Id, CharacterId CharacterId, string Message, CompanyApplicationStatus Status);
