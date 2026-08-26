using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Devices;

public union PhoneDeviceLookupResult(PhoneDeviceLookupResult.Found, PhoneDeviceLookupResult.NotFound)
{
    public record Found(PhoneDevice Device);

    public record NotFound;
}

public sealed record PhoneDeviceLookupQuery(PhoneDeviceId DeviceId) : IRequest<PhoneDeviceLookupResult>;

public sealed class PhoneDeviceLookupHandler(IPhoneDeviceRepository deviceRepository)
    : IRequestHandler<PhoneDeviceLookupQuery, PhoneDeviceLookupResult>
{
    public async ValueTask<PhoneDeviceLookupResult> Handle(PhoneDeviceLookupQuery request, CancellationToken cancellationToken)
        => await deviceRepository.FindByIdAsync(request.DeviceId, cancellationToken) is { } device
            ? new PhoneDeviceLookupResult.Found(device)
            : new PhoneDeviceLookupResult.NotFound();
}

public sealed record CharacterPhoneDevicesQuery(CharacterId CharacterId) : IRequest<IReadOnlyList<PhoneDevice>>;

public sealed class CharacterPhoneDevicesHandler(IPhoneDeviceRepository deviceRepository)
    : IRequestHandler<CharacterPhoneDevicesQuery, IReadOnlyList<PhoneDevice>>
{
    public async ValueTask<IReadOnlyList<PhoneDevice>> Handle(CharacterPhoneDevicesQuery request, CancellationToken cancellationToken)
        => await deviceRepository.FindByCharacterAsync(request.CharacterId, cancellationToken);
}
