namespace ELifeRPG.Characters.Api.Characters;

public sealed record CreateCharacterRequestDto
{
    public required Guid AccountId { get; init; }

    public required string Name { get; init; }

    public CreateCharacterCommand ToCommand() => new(new AccountId(AccountId), Name);
}
