using ELifeRPG.Phone.Domain.Apps;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class AppCatalogTests
{
    [Fact]
    public void Entries_CoverEveryDeclaredAppKey()
    {
        // The catalog is the backend's answer to "what apps exist", so a key without an entry would
        // be an app the API accepts and then cannot describe.
        foreach (var key in Enum.GetValues<AppKey>())
        {
            Assert.True(AppCatalog.Contains(key), $"AppKey.{key} has no catalog entry.");
        }
    }

    [Fact]
    public void Get_ReturnsTheDefinition()
    {
        var definition = AppCatalog.Get(AppKey.Messages);

        Assert.Equal(AppKey.Messages, definition.Key);
        Assert.False(string.IsNullOrWhiteSpace(definition.DisplayName));
    }

    [Fact]
    public void SystemApps_AreTheOsApps()
    {
        // Pinned as a set rather than checked per app: the flag decides whether an app can be
        // removed from a handset at all, so flipping one by accident must fail a test rather than
        // quietly make the dialer deletable — or Messages undeletable.
        Assert.Equal(
            [AppKey.Messages, AppKey.Contacts, AppKey.Settings, AppKey.Store, AppKey.Phone],
            AppCatalog.SystemApps.Select(definition => definition.Key));
    }

    [Fact]
    public void StoreApps_AreTheComplementOfTheSystemApps()
    {
        Assert.Equal([AppKey.Bank, AppKey.Map], AppCatalog.StoreApps.Select(definition => definition.Key));
        Assert.DoesNotContain(true, AppCatalog.StoreApps.Select(definition => definition.IsSystemApp));
    }

    [Fact]
    public void IsSystemApp_WithAnUndefinedKey_IsFalse()
    {
        // Total where Get throws, because the uninstall path consults it and must not blow up on a
        // key that names no app — it falls through to "not installed" instead.
        Assert.False(AppCatalog.IsSystemApp((AppKey)999));
    }

    [Fact]
    public void Get_WithAnUndefinedKey_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => AppCatalog.Get((AppKey)999));
    }
}
