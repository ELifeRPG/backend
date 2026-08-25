using ELifeRPG.Accounts.Application.Hive;
using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Api.Hive;

public sealed record HiveSettingsDto(bool WhitelistEnabled)
{
    public static HiveSettingsDto Create(HiveSettings source) => new(source.WhitelistEnabled);
}

public sealed record UpdateHiveSettingsRequestDto(bool? WhitelistEnabled)
{
    public UpdateHiveSettingsCommand ToCommand() => new(WhitelistEnabled);
}
