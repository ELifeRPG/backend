using ELifeRPG.World.Application.Common;

namespace ELifeRPG.World.Application.Inventory;

/// <summary>
/// Backs <c>GET /api/inventory/undeliverable</c> — the staff queue for instances that hit
/// <c>WorldSettings.MaxDeliveryAttempts</c> without ever being acked. Each row carries its
/// <see cref="ItemInstance.OriginRef"/> so a human can redeliver or refund; there is no automatic
/// refund (see the design spec's "Delivering a granted instance").
/// </summary>
public sealed record UndeliverableInstancesQuery : IRequest<IReadOnlyList<ItemInstance>>;

public sealed class UndeliverableInstancesHandler(IItemInstanceRepository repository, IWorldSettingsRepository settingsRepository)
    : IRequestHandler<UndeliverableInstancesQuery, IReadOnlyList<ItemInstance>>
{
    public async ValueTask<IReadOnlyList<ItemInstance>> Handle(UndeliverableInstancesQuery request, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(cancellationToken);
        return await repository.FindUndeliverableAsync(settings.MaxDeliveryAttempts, cancellationToken);
    }
}
