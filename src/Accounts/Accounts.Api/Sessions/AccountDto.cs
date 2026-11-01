using ELifeRPG.Accounts.Application.Accounts;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Api.Sessions;

/// <summary>One linked in-game identity. <c>Platform</c> is null when the gameserver never reported one.</summary>
public sealed record GameAccountDto
{
    public required string GameIdentityId { get; init; }

    public string? Platform { get; init; }

    public static GameAccountDto Create(GameAccountLink source) => new()
    {
        GameIdentityId = source.GameIdentityId.Value.ToString(),
        Platform = source.Platform?.ToString(),
    };
}

public sealed record AccountDto
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Every game identity linked to this account — empty for a fresh portal signup, and more than
    /// one for a player who plays on several platforms.
    /// </summary>
    public required List<GameAccountDto> GameAccounts { get; init; }

    // Not modeled on the Account aggregate yet — blocked on the separate, unmerged
    // account-linking work. Always null until that lands; matches the webapp's already-nullable
    // `discordUsername` field.
    public string? DiscordUsername { get; init; }

    public required string Status { get; init; }

    public static AccountDto Create(AccountWithGameAccounts source) => new()
    {
        Id = source.Account.Id.Value,
        GameAccounts = source.GameAccounts.Select(GameAccountDto.Create).ToList(),
        DiscordUsername = null,
        Status = source.Account.Status.ToString(),
    };
}
