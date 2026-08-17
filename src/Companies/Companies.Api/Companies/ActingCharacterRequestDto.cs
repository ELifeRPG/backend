namespace ELifeRPG.Companies.Api.Companies;

public sealed record ActingCharacterRequestDto
{
    public required Guid ActingCharacterId { get; init; }

    public ConfirmApplicationCommand ToConfirmCommand(Guid companyId, Guid applicationId) =>
        new(new CompanyId(companyId), new CompanyApplicationId(applicationId), new CharacterId(ActingCharacterId));

    public AcceptApplicationCommand ToAcceptCommand(Guid companyId, Guid applicationId) =>
        new(new CompanyId(companyId), new CompanyApplicationId(applicationId), new CharacterId(ActingCharacterId));

    public DenyApplicationCommand ToDenyCommand(Guid companyId, Guid applicationId) =>
        new(new CompanyId(companyId), new CompanyApplicationId(applicationId), new CharacterId(ActingCharacterId));
}
