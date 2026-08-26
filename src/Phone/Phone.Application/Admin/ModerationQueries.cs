using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Admin;

/// <summary>
/// Staff surface. These carry no acting character and run no guard chain on purpose — a moderator
/// inspecting a number they do not own is exactly what this is for. The `phone:manage` scope on the
/// endpoints is what gates them.
/// </summary>
public sealed record ModerationSimCardsQuery(string? NumberFragment) : IRequest<IReadOnlyList<SimCard>>;

public sealed class ModerationSimCardsHandler(IPhoneModerationRepository repository)
    : IRequestHandler<ModerationSimCardsQuery, IReadOnlyList<SimCard>>
{
    public async ValueTask<IReadOnlyList<SimCard>> Handle(ModerationSimCardsQuery request, CancellationToken cancellationToken)
        => await repository.SearchSimCardsAsync(request.NumberFragment, cancellationToken);
}

public sealed record ModerationDevicesQuery : IRequest<IReadOnlyList<PhoneDevice>>;

public sealed class ModerationDevicesHandler(IPhoneModerationRepository repository)
    : IRequestHandler<ModerationDevicesQuery, IReadOnlyList<PhoneDevice>>
{
    public async ValueTask<IReadOnlyList<PhoneDevice>> Handle(ModerationDevicesQuery request, CancellationToken cancellationToken)
        => await repository.ListDevicesAsync(cancellationToken);
}

public sealed record ModerationThreadsQuery(SimCardId SimCardId) : IRequest<IReadOnlyList<MessageThread>>;

public sealed class ModerationThreadsHandler(IPhoneModerationRepository repository)
    : IRequestHandler<ModerationThreadsQuery, IReadOnlyList<MessageThread>>
{
    public async ValueTask<IReadOnlyList<MessageThread>> Handle(ModerationThreadsQuery request, CancellationToken cancellationToken)
        => await repository.ListThreadsForSimAsync(request.SimCardId, cancellationToken);
}
