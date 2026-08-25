using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Accounts.Domain.UnitTests;

public class GameServerIdTests
{
    [Fact]
    public void Value_RoundTripsTheUnderlyingGuid()
    {
        var raw = Guid.NewGuid();

        var id = new GameServerId(raw);

        Assert.Equal(raw, id.Value);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var raw = Guid.NewGuid();

        Assert.Equal(new GameServerId(raw), new GameServerId(raw));
        Assert.NotEqual(new GameServerId(raw), new GameServerId(Guid.NewGuid()));
    }
}
