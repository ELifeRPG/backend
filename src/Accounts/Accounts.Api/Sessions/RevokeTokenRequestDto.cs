using ELifeRPG.Accounts.Application.Tokens;

namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record RevokeTokenRequestDto
{
    public required string Jti { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public RevokeTokenCommand ToCommand() => new(Jti, ExpiresAt);
}
