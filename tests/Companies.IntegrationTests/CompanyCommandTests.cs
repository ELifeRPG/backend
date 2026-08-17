using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Companies.Domain;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Companies.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class CompanyCommandTests : IAsyncLifetime
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
    public async Task CreateCompany_ForKnownFounder_FounderBecomesFirstMember()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));

        Assert.True(result is CreateCompanyResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCompanyResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var details = await mediator.Send(new CompanyDetailsQuery(created.CompanyId));
        Assert.True(details is CompanyDetailsResult.Found, $"Expected Found, got {details}");
        if (details is CompanyDetailsResult.Found found)
        {
            Assert.Contains(found.Company.Memberships, m => m.CharacterId == founderId);
        }
    }

    [Fact]
    public async Task CreateCompany_ForUnknownFounder_ReturnsFounderNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CreateCompanyCommand("Ghost Corp", new CharacterId(Guid.NewGuid())));

        Assert.True(result is CreateCompanyResult.FounderNotFound, $"Expected FounderNotFound, got {result}");
    }

    [Fact]
    public async Task AddMember_ForKnownCharacter_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var newMemberId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new AddMemberCommand(companyId, newMemberId));

        Assert.True(result is AddMemberResult.Added, $"Expected Added, got {result}");
    }

    [Fact]
    public async Task AddMember_ForUnknownCompany_ReturnsCompanyNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var characterId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new AddMemberCommand(new CompanyId(Guid.NewGuid()), characterId));

        Assert.True(result is AddMemberResult.CompanyNotFound, $"Expected CompanyNotFound, got {result}");
    }

    [Fact]
    public async Task AddMember_ForUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);

        var result = await mediator.Send(new AddMemberCommand(companyId, new CharacterId(Guid.NewGuid())));

        Assert.True(result is AddMemberResult.CharacterNotFound, $"Expected CharacterNotFound, got {result}");
    }

    [Fact]
    public async Task AddMember_SameCharacterTwice_ReturnsAlreadyMember()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var memberId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, memberId));

        var result = await mediator.Send(new AddMemberCommand(companyId, memberId));

        Assert.True(result is AddMemberResult.AlreadyMember, $"Expected AlreadyMember, got {result}");
    }

    [Fact]
    public async Task CreateCompany_FounderGetsOwnerPermissions()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));

        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var permissions = await mediator.Send(new CompanyMemberPermissionsQuery(createdCompany.CompanyId, founderId));

        Assert.True(permissions is CompanyMemberPermissionsResult.Found, $"Expected Found, got {permissions}");
        if (permissions is CompanyMemberPermissionsResult.Found found)
        {
            Assert.Equal(
                CompanyPermissions.ManageCompany | CompanyPermissions.ManageMembers | CompanyPermissions.ManageWages | CompanyPermissions.ManageFinances | CompanyPermissions.ManageShops,
                found.Permissions);
        }
    }

    [Fact]
    public async Task CompanyMemberPermissionsQuery_ForNonMember_ReturnsNotMember()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var nonMemberId = await CreateCharacterAsync(mediator);

        var permissions = await mediator.Send(new CompanyMemberPermissionsQuery(companyId, nonMemberId));

        Assert.True(permissions is CompanyMemberPermissionsResult.NotMember, $"Expected NotMember, got {permissions}");
    }

    [Fact]
    public async Task AddedMember_WithoutExplicitPosition_HasNonePermissions()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var memberId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, memberId));

        var permissions = await mediator.Send(new CompanyMemberPermissionsQuery(companyId, memberId));

        Assert.True(permissions is CompanyMemberPermissionsResult.Found, $"Expected Found, got {permissions}");
        if (permissions is CompanyMemberPermissionsResult.Found found)
        {
            Assert.Equal(CompanyPermissions.None, found.Permissions);
        }
    }

    [Fact]
    public async Task CompanyLookupQuery_ForKnownCompany_ReturnsFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);

        var result = await mediator.Send(new CompanyLookupQuery(companyId));

        Assert.True(result is CompanyLookupResult.Found, $"Expected Found, got {result}");
    }

    [Fact]
    public async Task CompanyLookupQuery_ForUnknownCompany_ReturnsNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CompanyLookupQuery(new CompanyId(Guid.NewGuid())));

        Assert.True(result is CompanyLookupResult.NotFound, $"Expected NotFound, got {result}");
    }

    [Fact]
    public async Task CompaniesQuery_IncludesCreatedCompany()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Findable Corp", founderId));

        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var companies = await mediator.Send(new CompaniesQuery());

        Assert.Contains(companies, c => c.Id == createdCompany.CompanyId && c.Name == "Findable Corp");
    }

    [Fact]
    public async Task Company_CreatedOnOneServer_IsInvisibleFromAnotherServer()
    {
        await using var providerB = TestServices.BuildProvider("gameserver-two");

        await using var scopeA = _provider.CreateAsyncScope();
        var mediatorA = scopeA.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediatorA);

        await using var scopeB = providerB.CreateAsyncScope();
        var mediatorB = scopeB.ServiceProvider.GetRequiredService<IMediator>();

        var lookupFromCreatingServer = await mediatorA.Send(new CompanyLookupQuery(companyId));
        var lookupFromOtherServer = await mediatorB.Send(new CompanyLookupQuery(companyId));

        Assert.True(lookupFromCreatingServer is CompanyLookupResult.Found, $"Expected Found from the creating server, got {lookupFromCreatingServer}");
        Assert.True(lookupFromOtherServer is CompanyLookupResult.NotFound, $"Expected NotFound from a different server, got {lookupFromOtherServer}");
    }

    [Fact]
    public async Task SubmitApplication_ForKnownCharacter_Succeeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var applicantId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "Please let me join."));

        Assert.True(result is SubmitApplicationResult.Submitted, $"Expected Submitted, got {result}");
    }

    [Fact]
    public async Task SubmitApplication_ForUnknownCompany_ReturnsCompanyNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var applicantId = await CreateCharacterAsync(mediator);

        var result = await mediator.Send(new SubmitApplicationCommand(new CompanyId(Guid.NewGuid()), applicantId, "Hire me."));

        Assert.True(result is SubmitApplicationResult.CompanyNotFound, $"Expected CompanyNotFound, got {result}");
    }

    [Fact]
    public async Task SubmitApplication_ForUnknownCharacter_ReturnsCharacterNotFound()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);

        var result = await mediator.Send(new SubmitApplicationCommand(companyId, new CharacterId(Guid.NewGuid()), "Hire me."));

        Assert.True(result is SubmitApplicationResult.CharacterNotFound, $"Expected CharacterNotFound, got {result}");
    }

    [Fact]
    public async Task SubmitApplication_ForExistingMember_ReturnsAlreadyMember()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var memberId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, memberId));

        var result = await mediator.Send(new SubmitApplicationCommand(companyId, memberId, "Hire me."));

        Assert.True(result is SubmitApplicationResult.AlreadyMember, $"Expected AlreadyMember, got {result}");
    }

    [Fact]
    public async Task SubmitApplication_WithOpenApplication_ReturnsDuplicateApplication()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var applicantId = await CreateCharacterAsync(mediator);
        await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "First try."));

        var result = await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "Second try."));

        Assert.True(result is SubmitApplicationResult.DuplicateApplication, $"Expected DuplicateApplication, got {result}");
    }

    [Fact]
    public async Task CompanyApplicationsQuery_ForManagerCharacter_ReturnsApplications()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));
        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }
        var applicantId = await CreateCharacterAsync(mediator);
        await mediator.Send(new SubmitApplicationCommand(createdCompany.CompanyId, applicantId, "Hire me."));

        var result = await mediator.Send(new CompanyApplicationsQuery(createdCompany.CompanyId, founderId));

        Assert.True(result is CompanyApplicationsResult.Found, $"Expected Found, got {result}");
        if (result is CompanyApplicationsResult.Found found)
        {
            Assert.Contains(found.Applications, a => a.CharacterId == applicantId && a.Status == CompanyApplicationStatus.Pending);
        }
    }

    [Fact]
    public async Task CompanyApplicationsQuery_ForNonManagerCharacter_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var rookieId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, rookieId));

        var result = await mediator.Send(new CompanyApplicationsQuery(companyId, rookieId));

        Assert.True(result is CompanyApplicationsResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task ConfirmApplication_ByManager_SetsInProgress()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));
        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(createdCompany.CompanyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new ConfirmApplicationCommand(createdCompany.CompanyId, submittedApplication.ApplicationId, founderId));

        Assert.True(result is ConfirmApplicationResult.Confirmed, $"Expected Confirmed, got {result}");
        var applications = await mediator.Send(new CompanyApplicationsQuery(createdCompany.CompanyId, founderId));
        if (applications is CompanyApplicationsResult.Found found)
        {
            Assert.Contains(found.Applications, a => a.Id == submittedApplication.ApplicationId && a.Status == CompanyApplicationStatus.InProgress);
        }
    }

    [Fact]
    public async Task AcceptApplication_ByManager_AddsMemberWithDefaultPermissions()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));
        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(createdCompany.CompanyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new AcceptApplicationCommand(createdCompany.CompanyId, submittedApplication.ApplicationId, founderId));

        Assert.True(result is AcceptApplicationResult.Accepted, $"Expected Accepted, got {result}");
        var permissions = await mediator.Send(new CompanyMemberPermissionsQuery(createdCompany.CompanyId, applicantId));
        Assert.True(permissions is CompanyMemberPermissionsResult.Found, $"Expected Found, got {permissions}");
        if (permissions is CompanyMemberPermissionsResult.Found found)
        {
            Assert.Equal(CompanyPermissions.None, found.Permissions);
        }
    }

    [Fact]
    public async Task DenyApplication_ByManager_SetsDenied()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var created = await mediator.Send(new CreateCompanyCommand("Acme Corp", founderId));
        Assert.True(created is CreateCompanyResult.Created, $"Expected Created, got {created}");
        if (created is not CreateCompanyResult.Created createdCompany)
        {
            throw new InvalidOperationException("Unreachable.");
        }
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(createdCompany.CompanyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new DenyApplicationCommand(createdCompany.CompanyId, submittedApplication.ApplicationId, founderId));

        Assert.True(result is DenyApplicationResult.Denied, $"Expected Denied, got {result}");
    }

    [Fact]
    public async Task ConfirmApplication_ByNonManager_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var rookieId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, rookieId));
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new ConfirmApplicationCommand(companyId, submittedApplication.ApplicationId, rookieId));

        Assert.True(result is ConfirmApplicationResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task AcceptApplication_ByNonManager_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var rookieId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, rookieId));
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new AcceptApplicationCommand(companyId, submittedApplication.ApplicationId, rookieId));

        Assert.True(result is AcceptApplicationResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    [Fact]
    public async Task DenyApplication_ByNonManager_ReturnsNotAuthorized()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var companyId = await CreateCompanyAsync(mediator);
        var rookieId = await CreateCharacterAsync(mediator);
        await mediator.Send(new AddMemberCommand(companyId, rookieId));
        var applicantId = await CreateCharacterAsync(mediator);
        var submitted = await mediator.Send(new SubmitApplicationCommand(companyId, applicantId, "Hire me."));
        Assert.True(submitted is SubmitApplicationResult.Submitted, $"Expected Submitted, got {submitted}");
        if (submitted is not SubmitApplicationResult.Submitted submittedApplication)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var result = await mediator.Send(new DenyApplicationCommand(companyId, submittedApplication.ApplicationId, rookieId));

        Assert.True(result is DenyApplicationResult.NotAuthorized, $"Expected NotAuthorized, got {result}");
    }

    private async Task<AccountId> CreateActiveAccountAsync(IMediator mediator)
    {
        var bohemiaId = new GameId(Guid.NewGuid());
        var result = await mediator.Send(new CreateSessionCommand(bohemiaId, "gameserver-dev"));

        _createdUsernames.Add(result.KeycloakUsername);

        return result.AccountId;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var accountId = await CreateActiveAccountAsync(mediator);
        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Company Test Character"));

        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }

    private async Task<CompanyId> CreateCompanyAsync(IMediator mediator)
    {
        var founderId = await CreateCharacterAsync(mediator);
        var result = await mediator.Send(new CreateCompanyCommand("Test Corp", founderId));

        Assert.True(result is CreateCompanyResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCompanyResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CompanyId;
    }
}
