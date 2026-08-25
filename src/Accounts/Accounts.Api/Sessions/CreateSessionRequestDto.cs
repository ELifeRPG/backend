namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record CreateSessionRequestDto
{
    public required Guid BohemiaId { get; init; }

    public CreateSessionCommand ToCommand() => new(new GameId(BohemiaId));
}
