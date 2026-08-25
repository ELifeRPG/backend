using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Characters.Application.Common;
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
    public async Task Handle_CharacterCreatedUnderOneServer_IsVisibleFromAnotherServer()
    {
        // Hive model: servers are maps in one shared world, so a character created via one
        // gameserver must be reachable from another. This asserts the exact opposite of the
        // pre-hive behaviour — see docs/superpowers/specs/2026-08-22-hive-tenancy-design.md.
        var accountId = await CreateAccountAsync();

        CharacterId characterId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new CreateCharacterCommand(accountId, "Traveller"), CancellationToken.None);
            Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
            if (result is not CreateCharacterResult.Created createdCharacter)
            {
                throw new InvalidOperationException("Unreachable.");
            }

            characterId = createdCharacter.CharacterId;

            var lookupFromCreatingServer = await mediator.Send(new CharacterLookupQuery(characterId), CancellationToken.None);
            Assert.True(lookupFromCreatingServer is CharacterLookupResult.Found, $"Expected Found from the creating server, got {lookupFromCreatingServer}");
        }

        await using var otherProvider = TestServices.BuildProvider("gameserver-two");
        await using var otherScope = otherProvider.CreateAsyncScope();
        var otherMediator = otherScope.ServiceProvider.GetRequiredService<IMediator>();

        var lookupFromOtherServer = await otherMediator.Send(new CharacterLookupQuery(characterId), CancellationToken.None);

        Assert.True(lookupFromOtherServer is CharacterLookupResult.Found, $"Expected Found from another server, got {lookupFromOtherServer}");
    }

    [Fact]
    public async Task Handle_StampsTheCreatingServerOntoTheCharacter()
    {
        var accountId = await CreateAccountAsync();

        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var currentServer = scope.ServiceProvider.GetRequiredService<ICurrentGameServer>();
        var expectedServerId = await currentServer.GetIdAsync(CancellationToken.None);

        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Stamped"), CancellationToken.None);
        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var characters = await mediator.Send(new CharactersQuery(accountId), CancellationToken.None);
        var stamped = characters.Single(x => x.Id == created.CharacterId);

        Assert.Equal(expectedServerId, stamped.CurrentServerId);
    }

    [Fact]
    public async Task Handle_ForNonexistentCharacter_ReturnsNotFound()
    {
        // CreateScope()/Dispose() throws here: MartenCharacterRepository (scoped) only implements
        // IAsyncDisposable, and the sync ServiceProviderEngineScope.Dispose() path rejects that.
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var nonexistentCharacterId = new CharacterId(Guid.NewGuid());

        var lookup = await mediator.Send(new CharacterLookupQuery(nonexistentCharacterId), CancellationToken.None);

        Assert.True(lookup is CharacterLookupResult.NotFound, $"Expected NotFound, got {lookup}");
    }

    private async Task<AccountId> CreateAccountAsync()
    {
        // CreateScope()/Dispose() throws here: MartenCharacterRepository (scoped) only implements
        // IAsyncDisposable, and the sync ServiceProviderEngineScope.Dispose() path rejects that.
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await CreateActiveAccountAsync(mediator);
    }

    private async Task<AccountId> CreateActiveAccountAsync(IMediator mediator)
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId));

        _createdUsernames.Add(result.KeycloakUsername);

        return result.AccountId;
    }
}
