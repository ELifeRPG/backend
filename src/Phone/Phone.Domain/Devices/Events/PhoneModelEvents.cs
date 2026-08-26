using ELifeRPG.Phone.Domain.Apps;

namespace ELifeRPG.Phone.Domain.Devices.Events;

public sealed record PhoneModelCreated(
    PhoneModelId Id,
    string DisplayName,
    int Tier,
    ItemId? ItemId,
    int SimSlots,
    IReadOnlyList<AppKey> SupportedApps,
    int ContactLimit,
    int ThreadMessageLimit,
    int MaxGroupParticipants);
