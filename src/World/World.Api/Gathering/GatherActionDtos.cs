using ELifeRPG.Characters.Application.Skills;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Application.Common;
using ELifeRPG.World.Application.Gathering;

namespace ELifeRPG.World.Api.Gathering;

public sealed record GatherActionRequestDto
{
    public required Guid CharacterId { get; init; }

    public required string Action { get; init; }

    public required Guid ItemId { get; init; }

    public required int Quantity { get; init; }

    /// <summary>
    /// Takes the calling gameserver's id rather than reading it here: a gather is an inventory write,
    /// and the handler guards it against <c>Character.CurrentServerId</c> exactly as the ack and
    /// spawn-failed paths do. See <c>GatherResult.WrongServer</c>.
    /// </summary>
    public GatherCommand ToCommand(GameServerId gameServerId)
        => new(gameServerId, new CharacterId(CharacterId), Action, new ItemId(ItemId), Quantity);
}

/// <summary>
/// One freshly minted instance handed over by a gather action. Deliberately a separate type from
/// Shops.Api's own GrantedInstanceDto (which has an identical shape) rather than a shared contract —
/// per ARCHITECTURE.md §9e, DTOs live beside their endpoint and own their mapping; there is no shared
/// DTO project, and a shared type here would couple two modules' API surfaces together. Field-for-field
/// identical to Shops.Api's version on purpose, so the mod's adopt-and-ack path is written once and
/// works unmodified against either response body.
/// </summary>
public sealed record GrantedInstanceDto
{
    public required Guid InstanceId { get; init; }

    public required Guid ItemId { get; init; }

    public required string PrefabClassName { get; init; }

    public static GrantedInstanceDto Create(GrantedInstance source) => new()
    {
        InstanceId = source.InstanceId.Value,
        ItemId = source.ItemId.Value,
        PrefabClassName = source.PrefabClassName,
    };
}

/// <summary>Field-for-field identical to Characters.Api's own SkillXpGrantDto — same "separate type per endpoint" reasoning as GrantedInstanceDto above.</summary>
public sealed record SkillXpGrantDto
{
    public required string Skill { get; init; }

    public required long XpGained { get; init; }

    public required long NewTotalXp { get; init; }

    public required int NewLevel { get; init; }

    public required bool DidLevelUp { get; init; }

    public static SkillXpGrantDto Create(SkillXpGrant source) => new()
    {
        Skill = source.Skill.ToString(),
        XpGained = source.XpGained,
        NewTotalXp = source.NewTotalXp,
        NewLevel = source.NewLevel,
        DidLevelUp = source.DidLevelUp,
    };
}

public sealed record GatherActionResultDto
{
    public required IReadOnlyList<SkillXpGrantDto> Gains { get; init; }

    public required IReadOnlyList<GrantedInstanceDto> GrantedInstances { get; init; }

    public static GatherActionResultDto Create(GatherResult.Gathered source) => new()
    {
        Gains = source.Gains.Select(SkillXpGrantDto.Create).ToList(),
        GrantedInstances = source.GrantedInstances.Select(GrantedInstanceDto.Create).ToList(),
    };
}
