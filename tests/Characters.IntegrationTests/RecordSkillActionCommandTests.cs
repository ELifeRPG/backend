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
public sealed class RecordSkillActionCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly KeycloakTestClient _keycloak = new();
    private readonly List<string> _createdUsernames = [];

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var username in _createdUsernames)
        {
            await _keycloak.DeleteUserAsync(username);
        }

        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Handle_SingleSkillAction_GrantsXpAndReturnsFullState()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new RecordSkillActionCommand(characterId, nameof(SkillAction.MinedOreDeposit)));

        Assert.True(result is RecordSkillActionResult.Recorded, $"Expected Recorded, got {result}");
        if (result is not RecordSkillActionResult.Recorded recorded)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.Single(recorded.Gains);
        Assert.Equal(SkillType.Mining, recorded.Gains[0].Skill);
        Assert.Equal(25, recorded.Gains[0].XpGained);
        Assert.Equal(25, recorded.Gains[0].NewTotalXp);
        Assert.Equal(10, recorded.FullState.Count);
        Assert.Contains(recorded.FullState, s => s.Skill == SkillType.Mining && s.TotalXp == 25);
    }

    [Fact]
    public async Task Handle_MultiSkillAction_CreditsEveryRewardedSkill()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new RecordSkillActionCommand(characterId, nameof(SkillAction.ForgedIngot)));

        Assert.True(result is RecordSkillActionResult.Recorded, $"Expected Recorded, got {result}");
        if (result is not RecordSkillActionResult.Recorded recorded)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.Equal(2, recorded.Gains.Count);
        Assert.Contains(recorded.Gains, g => g.Skill == SkillType.Blacksmithing && g.XpGained == 40);
        Assert.Contains(recorded.Gains, g => g.Skill == SkillType.Mining && g.XpGained == 5);
    }

    [Fact]
    public async Task Handle_WithQuantity_ScalesXpReward()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new RecordSkillActionCommand(characterId, nameof(SkillAction.MinedOreDeposit), Quantity: 3));

        Assert.True(result is RecordSkillActionResult.Recorded, $"Expected Recorded, got {result}");
        if (result is not RecordSkillActionResult.Recorded recorded)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.Equal(75, recorded.Gains[0].XpGained);
    }

    [Fact]
    public async Task Handle_RepeatedActionsCrossingLevelThreshold_ReportsDidLevelUpExactlyOnce()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);
        var xpForLevelTwo = SkillLeveling.XpForNextLevel(1);
        var quantityToCrossThreshold = (int)(xpForLevelTwo / 25) + 1;

        var beforeLastGrant = await mediator.Send(new RecordSkillActionCommand(characterId, nameof(SkillAction.MinedOreDeposit), Quantity: quantityToCrossThreshold - 1));
        var afterLastGrant = await mediator.Send(new RecordSkillActionCommand(characterId, nameof(SkillAction.MinedOreDeposit), Quantity: 1));

        Assert.True(beforeLastGrant is RecordSkillActionResult.Recorded, $"Expected Recorded, got {beforeLastGrant}");
        Assert.True(afterLastGrant is RecordSkillActionResult.Recorded, $"Expected Recorded, got {afterLastGrant}");
        if (beforeLastGrant is not RecordSkillActionResult.Recorded beforeRecorded)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        if (afterLastGrant is not RecordSkillActionResult.Recorded afterRecorded)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.False(beforeRecorded.Gains[0].DidLevelUp);
        Assert.True(afterRecorded.Gains[0].DidLevelUp);
    }

    [Fact]
    public async Task Handle_ForUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new RecordSkillActionCommand(new CharacterId(Guid.NewGuid()), nameof(SkillAction.MinedOreDeposit)));

        Assert.True(result is RecordSkillActionResult.CharacterNotFound, $"Expected CharacterNotFound, got {result}");
    }

    [Fact]
    public async Task Handle_ForUnrecognizedAction_ReturnsUnknownAction()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new RecordSkillActionCommand(characterId, "NotARealAction"));

        Assert.True(result is RecordSkillActionResult.UnknownAction, $"Expected UnknownAction, got {result}");
    }

    private async Task<AccountId> CreateActiveAccountAsync(IMediator mediator)
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(result.KeycloakUsername);

        return result.AccountId;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var accountId = await CreateActiveAccountAsync(mediator);
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Skills Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }
}
