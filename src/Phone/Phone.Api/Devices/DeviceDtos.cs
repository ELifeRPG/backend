using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Devices;

namespace ELifeRPG.Phone.Api.Devices;

/// <summary>
/// Note what is absent: <c>Pin</c>. It is never returned by any read, moderation included — a read
/// endpoint that echoed it would hand every holder of `phone:read` the key to every handset.
/// </summary>
public sealed record PhoneDto(
    Guid Id,
    string Number,
    Guid RegisteredTo,
    string Status,
    bool IsPoweredOn,
    IReadOnlyList<string> BlockedNumbers,
    IReadOnlyList<string> InstalledApps)
{
    public static PhoneDto Create(PhoneDevice source) => new(
        source.Id.Value,
        source.Number.Value,
        source.RegisteredTo.Value,
        source.Status.ToString(),
        source.IsPoweredOn,
        [.. source.BlockedNumbers.Select(number => number.Value)],
        [.. source.InstalledApps.Select(app => app.Key.ToString())]);
}

public sealed record ProvisionPhoneRequestDto(Guid CharacterId, string Pin)
{
    public ProvisionPhoneCommand ToCommand() => new(new CharacterId(CharacterId), Pin);
}

public sealed record ProvisionPhoneResponseDto(Guid PhoneId, string Number);

/// <summary>
/// <paramref name="CharacterId"/> is the acting character, carried in the body rather than read from
/// the JWT — the module-wide rule, and what lets the NPC service drive a phone through these same
/// endpoints.
///
/// <paramref name="Pin"/> is omitted by the phone's own owner, whose client has no need to send it,
/// and supplied by anyone else holding the handset. It replaces the biolock: possession plus the PIN
/// is what authorizes, rather than being bound to the device at provisioning.
///
/// Named for the phone rather than the generic "acting character" this used to be called: Companies
/// ships its own <c>ActingCharacterRequestDto</c>, and the OpenAPI generator keys schema components
/// on a DTO's *short* type name, silently folding two identically-named DTOs into one (ARCHITECTURE
/// §9c). That is harmless while both sides are structurally identical — the documented
/// GrantedInstanceDto/SkillXpGrantDto pairs are — but this one grew a Pin that Companies' has no
/// reason to. Before the rename the published spec carried Companies' shape for these endpoints, so
/// a generated client could not have sent a PIN at all.
/// </summary>
public sealed record PhoneActorRequestDto(Guid CharacterId, string? Pin = null)
{
    public PhoneActor ToActor() => new(new CharacterId(CharacterId), Pin);
}

public sealed record SetPhonePowerRequestDto(Guid CharacterId, bool IsPoweredOn, string? Pin = null)
{
    public PhoneActor ToActor() => new(new CharacterId(CharacterId), Pin);
}

public sealed record ChangePinRequestDto(Guid CharacterId, string NewPin, string? Pin = null)
{
    public PhoneActor ToActor() => new(new CharacterId(CharacterId), Pin);
}

/// <summary>Reason is recorded on the event so an enforcement action is auditable after the fact.</summary>
public sealed record SuspendPhoneRequestDto(string Reason);
