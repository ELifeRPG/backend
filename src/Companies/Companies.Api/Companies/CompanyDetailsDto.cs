namespace ELifeRPG.Companies.Api.Companies;

public sealed record CompanyMembershipDto
{
    public required Guid CharacterId { get; init; }

    public required string PositionName { get; init; }

    public static CompanyMembershipDto Create(CompanyMembership source, IReadOnlyCollection<CompanyPosition> positions) => new()
    {
        CharacterId = source.CharacterId.Value,
        PositionName = positions.SingleOrDefault(x => x.Id == source.PositionId)?.Name ?? "Unknown",
    };
}

public sealed record CompanyDetailsDto
{
    public required Guid CompanyId { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<CompanyMembershipDto> Members { get; init; }

    public static CompanyDetailsDto Create(Company source) => new()
    {
        CompanyId = source.Id.Value,
        Name = source.Name,
        Members = source.Memberships.Select(x => CompanyMembershipDto.Create(x, source.Positions)).ToList(),
    };
}
