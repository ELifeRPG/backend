using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Application.Devices;

namespace ELifeRPG.Phone.Application.Admin;

/// <summary>
/// Staff surface. These carry no acting character and run no guard chain on purpose — a moderator
/// inspecting a number they do not own is exactly what this is for. The `phone:manage` scope on the
/// endpoints is what gates them.
///
/// The list and the number search are one query now rather than two: with the SIM gone there is only
/// one kind of record to sweep, and an absent fragment means "all of them".
/// </summary>
public sealed record ModerationPhonesQuery(string? NumberFragment) : IRequest<IReadOnlyList<PhoneDetails>>;

public sealed class ModerationPhonesHandler(IPhoneModerationRepository repository, IPhonePreferencesRepository preferencesRepository)
    : IRequestHandler<ModerationPhonesQuery, IReadOnlyList<PhoneDetails>>
{
    public async ValueTask<IReadOnlyList<PhoneDetails>> Handle(ModerationPhonesQuery request, CancellationToken cancellationToken)
    {
        var phones = await repository.SearchPhonesAsync(request.NumberFragment, cancellationToken);
        return await preferencesRepository.PairAsync(phones, cancellationToken);
    }
}

public sealed record ModerationThreadsQuery(PhoneDeviceId PhoneId) : IRequest<IReadOnlyList<MessageThread>>;

public sealed class ModerationThreadsHandler(IPhoneModerationRepository repository)
    : IRequestHandler<ModerationThreadsQuery, IReadOnlyList<MessageThread>>
{
    public async ValueTask<IReadOnlyList<MessageThread>> Handle(ModerationThreadsQuery request, CancellationToken cancellationToken)
        => await repository.ListThreadsForPhoneAsync(request.PhoneId, cancellationToken);
}
