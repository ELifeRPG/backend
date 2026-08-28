using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Devices;

public union PhoneDeviceLookupResult(PhoneDeviceLookupResult.Found, PhoneDeviceLookupResult.NotFound)
{
    public record Found(PhoneDevice Phone);

    public record NotFound;
}

public sealed record PhoneDeviceLookupQuery(PhoneDeviceId PhoneId) : IRequest<PhoneDeviceLookupResult>;

public sealed class PhoneDeviceLookupHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<PhoneDeviceLookupQuery, PhoneDeviceLookupResult>
{
    public async ValueTask<PhoneDeviceLookupResult> Handle(PhoneDeviceLookupQuery request, CancellationToken cancellationToken)
        => await phoneRepository.FindByIdAsync(request.PhoneId, cancellationToken) is { } phone
            ? new PhoneDeviceLookupResult.Found(phone)
            : new PhoneDeviceLookupResult.NotFound();
}

public sealed record CharacterPhonesQuery(CharacterId CharacterId) : IRequest<IReadOnlyList<PhoneDevice>>;

public sealed class CharacterPhonesHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<CharacterPhonesQuery, IReadOnlyList<PhoneDevice>>
{
    public async ValueTask<IReadOnlyList<PhoneDevice>> Handle(CharacterPhonesQuery request, CancellationToken cancellationToken)
        => await phoneRepository.FindByCharacterAsync(request.CharacterId, cancellationToken);
}
