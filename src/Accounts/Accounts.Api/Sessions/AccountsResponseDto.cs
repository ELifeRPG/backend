namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record AccountsResponseDto
{
    public required List<AccountDto> Accounts { get; init; }
}
