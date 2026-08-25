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
public sealed class GrantSkillXpCommandTests : IAsyncLifetime
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
    public async Task Handle_ForKnownCharacterAndSkill_GrantsRawXp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new GrantSkillXpCommand(characterId, nameof(SkillType.Cooking), 500));

        Assert.True(result is GrantSkillXpResult.Granted, $"Expected Granted, got {result}");
        if (result is not GrantSkillXpResult.Granted granted)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        Assert.Equal(500, granted.NewTotalXp);

        var skills = await mediator.Send(new CharacterSkillsQuery(characterId));
        Assert.Contains(skills, s => s.Skill == SkillType.Cooking && s.TotalXp == 500);
    }

    [Fact]
    public async Task Handle_ForUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GrantSkillXpCommand(new CharacterId(Guid.NewGuid()), nameof(SkillType.Cooking), 500));

        Assert.True(result is GrantSkillXpResult.CharacterNotFound, $"Expected CharacterNotFound, got {result}");
    }

    [Fact]
    public async Task Handle_ForUnrecognizedSkill_ReturnsUnknownSkill()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new GrantSkillXpCommand(characterId, "NotARealSkill", 500));

        Assert.True(result is GrantSkillXpResult.UnknownSkill, $"Expected UnknownSkill, got {result}");
    }

    [Fact]
    public async Task Handle_WithNumericSkillString_ReturnsUnknownSkill()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new GrantSkillXpCommand(characterId, "11", 100));

        Assert.True(result is GrantSkillXpResult.UnknownSkill, $"Expected UnknownSkill, got {result}");
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
