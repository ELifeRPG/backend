using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Sims;

public union SimCardLookupResult(SimCardLookupResult.Found, SimCardLookupResult.NotFound)
{
    public record Found(SimCard SimCard);

    public record NotFound;
}

public sealed record SimCardLookupQuery(SimCardId SimCardId) : IRequest<SimCardLookupResult>;

public sealed class SimCardLookupHandler(ISimCardRepository simCardRepository)
    : IRequestHandler<SimCardLookupQuery, SimCardLookupResult>
{
    public async ValueTask<SimCardLookupResult> Handle(SimCardLookupQuery request, CancellationToken cancellationToken)
        => await simCardRepository.FindByIdAsync(request.SimCardId, cancellationToken) is { } sim
            ? new SimCardLookupResult.Found(sim)
            : new SimCardLookupResult.NotFound();
}

public sealed record CharacterSimCardsQuery(CharacterId CharacterId) : IRequest<IReadOnlyList<SimCard>>;

public sealed class CharacterSimCardsHandler(ISimCardRepository simCardRepository)
    : IRequestHandler<CharacterSimCardsQuery, IReadOnlyList<SimCard>>
{
    public async ValueTask<IReadOnlyList<SimCard>> Handle(CharacterSimCardsQuery request, CancellationToken cancellationToken)
        => await simCardRepository.FindByCharacterAsync(request.CharacterId, cancellationToken);
}

public sealed record DeviceSimCardsQuery(PhoneDeviceId DeviceId) : IRequest<IReadOnlyList<SimCard>>;

public sealed class DeviceSimCardsHandler(
    IPhoneDeviceRepository deviceRepository,
    ISimCardRepository simCardRepository)
    : IRequestHandler<DeviceSimCardsQuery, IReadOnlyList<SimCard>>
{
    public async ValueTask<IReadOnlyList<SimCard>> Handle(DeviceSimCardsQuery request, CancellationToken cancellationToken)
    {
        var device = await deviceRepository.FindByIdAsync(request.DeviceId, cancellationToken);
        return device is null
            ? []
            : await simCardRepository.FindByIdsAsync(device.InstalledSims, cancellationToken);
    }
}
