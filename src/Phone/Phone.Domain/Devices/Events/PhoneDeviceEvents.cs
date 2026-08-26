using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Devices.Events;

public sealed record PhoneDeviceProvisioned(PhoneDeviceId Id, PhoneModelId ModelId, CharacterId BoundCharacterId);

public sealed record PhoneDevicePoweredOn(PhoneDeviceId Id);

public sealed record PhoneDevicePoweredOff(PhoneDeviceId Id);

public sealed record SimInstalledIntoDevice(PhoneDeviceId Id, SimCardId SimCardId);

public sealed record SimEjectedFromDevice(PhoneDeviceId Id, SimCardId SimCardId);

public sealed record AppInstalled(PhoneDeviceId Id, AppKey Key);

public sealed record AppUninstalled(PhoneDeviceId Id, AppKey Key);
