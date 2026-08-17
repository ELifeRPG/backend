namespace ELifeRPG.Companies.Api.Companies;

public sealed record CreateCompanyRequestDto
{
    public required string Name { get; init; }

    public required Guid FounderCharacterId { get; init; }

    public CreateCompanyCommand ToCommand() => new(Name, new CharacterId(FounderCharacterId));
}
