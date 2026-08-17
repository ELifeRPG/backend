using ELifeRPG.Accounts.Application.Accounts;

namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record RoleSummaryDto
{
    public required string Name { get; init; }

    public string? Description { get; init; }
}

public sealed record AccountRolesDto
{
    public required List<string> AssignedRoles { get; init; }

    public required List<RoleSummaryDto> AvailableRoles { get; init; }

    public static AccountRolesDto Create(AccountRolesResult.Found source) => new()
    {
        AssignedRoles = source.AssignedRoles.ToList(),
        AvailableRoles = source.AvailableRoles.Select(r => new RoleSummaryDto { Name = r.Name, Description = r.Description }).ToList(),
    };
}
