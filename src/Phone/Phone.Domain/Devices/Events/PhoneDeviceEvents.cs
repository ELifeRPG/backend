using ELifeRPG.Phone.Domain.Apps;

namespace ELifeRPG.Phone.Domain.Devices.Events;

/// <summary>
/// Provisioning mints the number as part of the handset. There is no separate "number issued" event
/// any more, because there is no separate thing to issue it to.
/// </summary>
public sealed record PhoneDeviceProvisioned(PhoneDeviceId Id, PhoneNumber Number, string Pin, CharacterId RegisteredTo);

public sealed record PhonePinChanged(PhoneDeviceId Id, string Pin);

public sealed record PhoneDevicePoweredOn(PhoneDeviceId Id);

public sealed record PhoneDevicePoweredOff(PhoneDeviceId Id);

public sealed record PhoneSuspended(PhoneDeviceId Id, string Reason);

public sealed record PhoneRestored(PhoneDeviceId Id);

public sealed record PhoneDeactivated(PhoneDeviceId Id);

public sealed record PhoneNumberBlocked(PhoneDeviceId Id, PhoneNumber Number);

public sealed record PhoneNumberUnblocked(PhoneDeviceId Id, PhoneNumber Number);

public sealed record AppInstalled(PhoneDeviceId Id, AppKey Key);

public sealed record AppUninstalled(PhoneDeviceId Id, AppKey Key);
