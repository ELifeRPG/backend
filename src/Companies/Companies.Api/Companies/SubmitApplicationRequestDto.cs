namespace ELifeRPG.Companies.Api.Companies;

public sealed record SubmitApplicationRequestDto
{
    public required Guid CharacterId { get; init; }

    public required string Message { get; init; }

    public SubmitApplicationCommand ToCommand(Guid companyId) => new(new CompanyId(companyId), new CharacterId(CharacterId), Message);
}
