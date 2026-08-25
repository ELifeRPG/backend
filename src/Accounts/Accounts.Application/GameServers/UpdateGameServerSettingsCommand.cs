using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Application.GameServers;

/// <summary>
/// Follows the same Found/NotFound-style convention as the rest of Accounts.Application (see
/// AccountLookupQuery) instead of throwing for an unregistered client id.
/// </summary>
public union UpdateGameServerSettingsResult(UpdateGameServerSettingsResult.Updated, UpdateGameServerSettingsResult.NotFound)
{
    public record Updated(GameServer Server);

    public record NotFound;
}

public sealed record UpdateGameServerSettingsCommand(string ClientId, string? DisplayName, string? MapName)
    : IRequest<UpdateGameServerSettingsResult>;

public sealed class UpdateGameServerSettingsHandler(IGameServerRepository repository)
    : IRequestHandler<UpdateGameServerSettingsCommand, UpdateGameServerSettingsResult>
{
    public async ValueTask<UpdateGameServerSettingsResult> Handle(UpdateGameServerSettingsCommand request, CancellationToken cancellationToken)
    {
        var server = await repository.FindByClientIdAsync(request.ClientId, cancellationToken);
        if (server is null)
        {
            return new UpdateGameServerSettingsResult.NotFound();
        }

        if (request.DisplayName is not null)
        {
            server.DisplayName = request.DisplayName;
        }

        if (request.MapName is not null)
        {
            server.MapName = request.MapName;
        }

        await repository.UpsertAsync(server, cancellationToken);
        return new UpdateGameServerSettingsResult.Updated(server);
    }
}
