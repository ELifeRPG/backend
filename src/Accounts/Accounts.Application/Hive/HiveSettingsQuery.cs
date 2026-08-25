using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Application.Hive;

public sealed record HiveSettingsQuery : IRequest<HiveSettings>;

public sealed class HiveSettingsHandler(IHiveSettingsRepository repository)
    : IRequestHandler<HiveSettingsQuery, HiveSettings>
{
    public async ValueTask<HiveSettings> Handle(HiveSettingsQuery request, CancellationToken cancellationToken)
        => await repository.GetAsync(cancellationToken);
}
