using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Admin;

/// <summary>
/// Staff surface. These carry no acting character and run no guard chain on purpose — a moderator
/// inspecting a number they do not own is exactly what this is for. The `phone:manage` scope on the
/// endpoints is what gates them.
///
/// The list and the number search are one query now rather than two: with the SIM gone there is only
/// one kind of record to sweep, and an absent fragment means "all of them".
/// </summary>
public sealed record ModerationPhonesQuery(string? NumberFragment) : IRequest<IReadOnlyList<PhoneDevice>>;

public sealed class ModerationPhonesHandler(IPhoneModerationRepository repository)
    : IRequestHandler<ModerationPhonesQuery, IReadOnlyList<PhoneDevice>>
{
    public async ValueTask<IReadOnlyList<PhoneDevice>> Handle(ModerationPhonesQuery request, CancellationToken cancellationToken)
        => await repository.SearchPhonesAsync(request.NumberFragment, cancellationToken);
}

public sealed record ModerationThreadsQuery(PhoneDeviceId PhoneId) : IRequest<IReadOnlyList<MessageThread>>;

public sealed class ModerationThreadsHandler(IPhoneModerationRepository repository)
    : IRequestHandler<ModerationThreadsQuery, IReadOnlyList<MessageThread>>
{
    public async ValueTask<IReadOnlyList<MessageThread>> Handle(ModerationThreadsQuery request, CancellationToken cancellationToken)
        => await repository.ListThreadsForPhoneAsync(request.PhoneId, cancellationToken);
}
