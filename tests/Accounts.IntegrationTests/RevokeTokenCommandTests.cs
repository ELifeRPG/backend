using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Application.Tokens;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

public sealed class RevokeTokenCommandTests
{
    [Fact]
    public async Task Handle_RevokesTheGivenJti()
    {
        await using var provider = TestServices.BuildProvider();

        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new RevokeTokenCommand("some-jti", DateTimeOffset.UtcNow.AddMinutes(5)));

        var store = provider.GetRequiredService<ITokenRevocationStore>();
        Assert.True(store.IsRevoked("some-jti"));
    }
}
