namespace ELifeRPG.Characters.Api.Skills;

public sealed record GrantSkillXpResponseDto
{
    public required long NewTotalXp { get; init; }

    public required int NewLevel { get; init; }

    public static GrantSkillXpResponseDto Create(GrantSkillXpResult.Granted source) => new()
    {
        NewTotalXp = source.NewTotalXp,
        NewLevel = source.NewLevel,
    };
}
