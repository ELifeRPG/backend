namespace ELifeRPG.Companies.Domain.Events;

public sealed record MemberAdded(CompanyId Id, CharacterId CharacterId, CompanyPositionId PositionId);
