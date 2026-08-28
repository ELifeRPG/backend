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
    public void Get_WithAnUndefinedKey_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => AppCatalog.Get((AppKey)999));
    }
}
