namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record CreateSessionRequestDto
{
    public required Guid BohemiaId { get; init; }

    public CreateSessionCommand ToCommand(string serverClientId) => new(new GameId(BohemiaId), serverClientId);
}
