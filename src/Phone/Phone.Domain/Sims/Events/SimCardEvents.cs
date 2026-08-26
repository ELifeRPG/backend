using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Domain.Sims.Events;

public sealed record SimCardIssued(SimCardId Id, PhoneNumber Number, CharacterId RegisteredTo);

public sealed record SimCardInstalled(SimCardId Id, PhoneDeviceId DeviceId);

public sealed record SimCardEjected(SimCardId Id, PhoneDeviceId DeviceId);

public sealed record SimCardSuspended(SimCardId Id, string Reason);

public sealed record SimCardRestored(SimCardId Id);

public sealed record SimCardDeactivated(SimCardId Id);

public sealed record SimCardNumberBlocked(SimCardId Id, PhoneNumber Number);

public sealed record SimCardNumberUnblocked(SimCardId Id, PhoneNumber Number);
