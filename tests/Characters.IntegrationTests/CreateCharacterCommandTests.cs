using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Shared.Kernel;
using Marten;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Characters.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class CreateCharacterCommandTests : IAsyncLifetime
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
    public async Task Handle_ForActiveAccount_CreatesCharacter()
    {
        // CreateScope()/Dispose() throws here: MartenCharacterRepository (scoped) only implements
        // IAsyncDisposable, and the sync ServiceProviderEngineScope.Dispose() path rejects that.
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var accountId = await CreateActiveAccountAsync(mediator);

        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Alice"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
    }

    [Fact]
    public async Task Handle_ForUnknownAccount_ReturnsAccountNotFound()
    {
        // CreateScope()/Dispose() throws here: MartenCharacterRepository (scoped) only implements
        // IAsyncDisposable, and the sync ServiceProviderEngineScope.Dispose() path rejects that.
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var unknownAccountId = new AccountId(Guid.NewGuid());

        var result = await mediator.Send(new CreateCharacterCommand(unknownAccountId, "Ghost"));

        Assert.True(result is CreateCharacterResult.AccountNotFound, $"Expected AccountNotFound, got {result}");
    }

    [Fact]
    public async Task Handle_ForLockedAccount_ReturnsAccountLocked()
    {
        // CreateScope()/Dispose() throws here: MartenCharacterRepository (scoped) only implements
        // IAsyncDisposable, and the sync ServiceProviderEngineScope.Dispose() path rejects that.
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var accountId = await CreateActiveAccountAsync(mediator);

        var accountSession = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        accountSession.Events.Append(accountId.Value, new AccountLocked(accountId));
        await accountSession.SaveChangesAsync();

        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Locked Out"));

        Assert.True(result is CreateCharacterResult.AccountLocked, $"Expected AccountLocked, got {result}");
    }

    [Fact]
    public async Task Handle_CharactersQuery_ReturnsCreatedCharactersForAccount()
    {
        // CreateScope()/Dispose() throws here: MartenCharacterRepository (scoped) only implements
        // IAsyncDisposable, and the sync ServiceProviderEngineScope.Dispose() path rejects that.
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var accountId = await CreateActiveAccountAsync(mediator);

        var created = await mediator.Send(new CreateCharacterCommand(accountId, "Queryable"));
        Assert.True(created is CreateCharacterResult.Created, $"Expected Created, got {created}");

        var characters = await mediator.Send(new CharactersQuery(accountId));

        Assert.Contains(characters, character => character.Name == "Queryable" && character.AccountId == accountId);
    }

    [Fact]
    public async Task Handle_CharacterCreatedUnderOneServer_IsInvisibleFromAnotherServer()
    {
        await using var providerB = TestServices.BuildProvider("gameserver-two");

        // CreateScope()/Dispose() throws here: MartenCharacterRepository (scoped) only implements
        // IAsyncDisposable, and the sync ServiceProviderEngineScope.Dispose() path rejects that.
        await using var scopeA = _provider.CreateAsyncScope();
        var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
        var accountId = await CreateActiveAccountAsync(mediatorA);

        var created = await mediatorA.Send(new CreateCharacterCommand(accountId, "Server A Character"));
        Assert.True(created is CreateCharacterResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCharacterResult.Created createdCharacter)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        await using var scopeB = providerB.CreateAsyncScope();
        var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();

        var lookupFromCreatingServer = await mediatorA.Send(new CharacterLookupQuery(createdCharacter.CharacterId));
        var lookupFromOtherServer = await mediatorB.Send(new CharacterLookupQuery(createdCharacter.CharacterId));

        Assert.True(lookupFromCreatingServer is CharacterLookupResult.Found, $"Expected Found from the creating server, got {lookupFromCreatingServer}");
        Assert.True(lookupFromOtherServer is CharacterLookupResult.NotFound, $"Expected NotFound from a different server, got {lookupFromOtherServer}");
    }

    private async Task<AccountId> CreateActiveAccountAsync(IMediator mediator)
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, "gameserver-dev"));

        _createdUsernames.Add(result.KeycloakUsername);

        return result.AccountId;
    }
}
