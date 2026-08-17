namespace ELifeRPG.Companies.Api.Companies;

public sealed record CompanyDto
{
    public required Guid CompanyId { get; init; }

    public required string Name { get; init; }

    public required int MemberCount { get; init; }

    public static CompanyDto Create(Company source) => new()
    {
        CompanyId = source.Id.Value,
        Name = source.Name,
        MemberCount = source.Memberships.Count,
    };

    public static CompanyDto Create(CreateCompanyResult.Created source, string name) => new()
    {
        CompanyId = source.CompanyId.Value,
        Name = name,
        MemberCount = 1,
    };
}
