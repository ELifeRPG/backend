using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record AccountDto
{
    public required Guid Id { get; init; }

    public required string BohemiaId { get; init; }

    // Not modeled on the Account aggregate yet — blocked on the separate, unmerged
    // account-linking work. Always null until that lands; matches the webapp's already-nullable
    // `discordUsername` field.
    public string? DiscordUsername { get; init; }

    public required string Status { get; init; }

    public static AccountDto Create(Account source) => new()
    {
        Id = source.Id.Value,
        BohemiaId = source.BohemiaId.Value.ToString(),
        DiscordUsername = null,
        Status = source.Status.ToString(),
    };
}
