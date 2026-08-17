namespace ELifeRPG.Companies.Domain;

public sealed record CompanyPosition(CompanyPositionId Id, string Name, int Ordering, CompanyPermissions Permissions);
