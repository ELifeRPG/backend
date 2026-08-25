using ELifeRPG.Accounts.Domain;
using Xunit;

namespace ELifeRPG.Accounts.Domain.UnitTests;

public class HiveSettingsTests
{
    [Fact]
    public void Defaults_HaveWhitelistDisabledAndTheSingletonId()
    {
        var settings = new HiveSettings();

        Assert.False(settings.WhitelistEnabled);
        Assert.Equal(HiveSettings.SingletonId, settings.Id);
    }
}
