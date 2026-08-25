using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Characters.Application.Skills;
using ELifeRPG.Characters.Domain.Skills;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Characters.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class CharacterSkillsQueryTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Handle_ForCharacterWithNoSkillsYet_ReturnsAllTenSkillsAtLevelOneZeroXp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var skills = await mediator.Send(new CharacterSkillsQuery(characterId));

        Assert.Equal(10, skills.Count);
        Assert.All(skills, s => Assert.Equal(1, s.Level));
        Assert.All(skills, s => Assert.Equal(0, s.TotalXp));
    }

    [Fact]
    public async Task Handle_ForUnknownCharacter_StillReturnsDefaultSkillList()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var skills = await mediator.Send(new CharacterSkillsQuery(new CharacterId(Guid.NewGuid())));

        Assert.Equal(10, skills.Count);
    }

    // Accounts come from portal signup now, not from joining the gameserver:
    // CreateSessionCommand no longer creates one. See TestAccounts.
    private async Task<AccountId> CreateActiveAccountAsync()
    {
        using var scope = _provider.CreateScope();
        return (await TestAccounts.CreateAsync(scope.ServiceProvider)).Id;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var accountId = await CreateActiveAccountAsync();
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Skills Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }
}
