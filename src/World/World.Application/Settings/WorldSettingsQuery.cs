using ELifeRPG.World.Application.Common;

namespace ELifeRPG.World.Application.Settings;

/// <summary>Backs the <c>GET /api/inventory/limits</c> endpoint. The Api layer composes the returned settings with the structural domain constants — see WorldSettings' class summary.</summary>
public sealed record WorldSettingsQuery : IRequest<WorldSettings>;

public sealed class WorldSettingsHandler(IWorldSettingsRepository repository) : IRequestHandler<WorldSettingsQuery, WorldSettings>
{
    public async ValueTask<WorldSettings> Handle(WorldSettingsQuery request, CancellationToken cancellationToken)
        => await repository.GetAsync(cancellationToken);
}
