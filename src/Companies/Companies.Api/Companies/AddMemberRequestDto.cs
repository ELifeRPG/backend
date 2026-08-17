namespace ELifeRPG.Companies.Api.Companies;

public sealed record AddMemberRequestDto
{
    public required Guid CharacterId { get; init; }

    public AddMemberCommand ToCommand(Guid companyId) => new(new CompanyId(companyId), new CharacterId(CharacterId));
}
