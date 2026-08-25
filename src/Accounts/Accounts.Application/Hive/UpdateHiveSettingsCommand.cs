using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Application.Hive;

public sealed record UpdateHiveSettingsCommand(bool? WhitelistEnabled) : IRequest<HiveSettings>;

public sealed class UpdateHiveSettingsHandler(IHiveSettingsRepository repository)
    : IRequestHandler<UpdateHiveSettingsCommand, HiveSettings>
{
    public async ValueTask<HiveSettings> Handle(UpdateHiveSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);

        if (request.WhitelistEnabled is { } whitelistEnabled)
        {
            settings.WhitelistEnabled = whitelistEnabled;
        }

        await repository.UpsertAsync(settings, cancellationToken);
        return settings;
    }
}
