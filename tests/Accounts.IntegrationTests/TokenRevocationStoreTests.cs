using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Infrastructure.Common;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Unlike its sibling tests in this project, these don't need the local infra stack running —
/// InMemoryTokenRevocationStore has no external dependencies. Lives here because this is the
/// project already wired to reference Accounts.Infrastructure.
/// </summary>
public sealed class TokenRevocationStoreTests
{
    [Fact]
    public void IsRevoked_ForUnknownJti_ReturnsFalse()
    {
        var store = new InMemoryTokenRevocationStore();

        Assert.False(store.IsRevoked("unknown-jti"));
    }

    [Fact]
    public void IsRevoked_AfterRevoke_ReturnsTrue()
    {
        var store = new InMemoryTokenRevocationStore();

        store.Revoke("some-jti", DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(store.IsRevoked("some-jti"));
    }

    [Fact]
    public void IsRevoked_AfterExpiry_ReturnsFalse()
    {
        var store = new InMemoryTokenRevocationStore();

        store.Revoke("expired-jti", DateTimeOffset.UtcNow.AddMilliseconds(-1));

        Assert.False(store.IsRevoked("expired-jti"));
    }
}
