namespace ELifeRPG.Companies.Domain.Events;

public sealed record CompanySharesIssued(CompanyId Id, CharacterId Buyer, int Quantity);
