using ELifeRPG.Phone.Application.Devices;

namespace ELifeRPG.Phone.Api.Devices;

public sealed record PhoneModelDto(
    Guid Id,
    string DisplayName,
    int Tier,
    Guid? ItemId,
    int SimSlots,
    IReadOnlyList<string> SupportedApps,
    int ContactLimit,
    int ThreadMessageLimit,
    int MaxGroupParticipants)
{
    public static PhoneModelDto Create(PhoneModel source) => new(
        source.Id.Value,
        source.DisplayName,
        source.Tier,
        source.ItemId?.Value,
        source.SimSlots,
        [.. source.SupportedApps.Select(app => app.ToString())],
        source.ContactLimit,
        source.ThreadMessageLimit,
        source.MaxGroupParticipants);
}

public sealed record CreatePhoneModelRequestDto(
    string DisplayName,
    int Tier,
    Guid? ItemId,
    int SimSlots,
    IReadOnlyList<string> SupportedApps,
    int ContactLimit,
    int ThreadMessageLimit,
    int MaxGroupParticipants);

public sealed record PhoneDeviceDto(
    Guid Id,
    Guid ModelId,
    Guid BoundCharacterId,
    bool IsPoweredOn,
    IReadOnlyList<Guid> InstalledSimCardIds,
    IReadOnlyList<string> InstalledApps)
{
    public static PhoneDeviceDto Create(PhoneDevice source) => new(
        source.Id.Value,
        source.ModelId.Value,
        source.BoundCharacterId.Value,
        source.IsPoweredOn,
        [.. source.InstalledSims.Select(sim => sim.Value)],
        [.. source.InstalledApps.Select(app => app.Key.ToString())]);
}

public sealed record ProvisionPhoneRequestDto(Guid CharacterId, Guid ModelId)
{
    public ProvisionPhoneDeviceCommand ToCommand() => new(new CharacterId(CharacterId), new PhoneModelId(ModelId));
}

/// <summary>
/// <paramref name="CharacterId"/> is the acting character, carried in the body rather than read from
/// the JWT — the module-wide rule, and what lets the NPC service drive a phone through these same
/// endpoints.
/// </summary>
public sealed record SetPhonePowerRequestDto(Guid CharacterId, bool IsPoweredOn);

public sealed record ActingCharacterRequestDto(Guid CharacterId);
