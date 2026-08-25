using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Shared.Kernel;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Companies.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) — see README.md. Proves
/// FetchForUpdateAsync/CompanyConcurrencyException serializes concurrent single-module writes
/// against the same company (mirrors Banking.IntegrationTests.BankAccountConcurrencyTests).
/// </summary>
public sealed class CompanyConcurrencyTests : IAsyncLifetime
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
    public async Task TwoConcurrentApplications_ToSameCompany_BothSucceedWithoutLostUpdates()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var founderId = await CreateCharacterAsync(mediator);
        var companyResult = await mediator.Send(new CreateCompanyCommand("Concurrency Test Co", founderId));
        Assert.True(companyResult is CreateCompanyResult.Created, $"Expected Created, got {companyResult}");
        var companyId = companyResult is CreateCompanyResult.Created created ? created.CompanyId : throw new InvalidOperationException("Unreachable.");
        var applicantAId = await CreateCharacterAsync(mediator);
        var applicantBId = await CreateCharacterAsync(mediator);

        // Two different characters submitting applications to the same company at once — both
        // events append to the same Company stream. Before this fix, one could silently overwrite
        // the other's projected state; the retry loop below proves neither is lost. Run genuinely
        // concurrently via Task.WhenAll (mirrors BankAccountConcurrencyTests/
        // PurchaseCompanySharesCommandTests) — without real overlap, the ConcurrentModification
        // branch below is unreachable and the retry loop proves nothing.
        var results = await Task.WhenAll(
            Task.Run(() => SubmitWithRetryAsync(companyId, applicantAId)),
            Task.Run(() => SubmitWithRetryAsync(companyId, applicantBId)));
        var resultA = results[0];
        var resultB = results[1];

        Assert.True(resultA is SubmitApplicationResult.Submitted, $"Expected Submitted, got {resultA}");
        Assert.True(resultB is SubmitApplicationResult.Submitted, $"Expected Submitted, got {resultB}");

        // Reload the company's applications independently of the two SubmitApplicationCommand calls
        // above, to genuinely prove neither update was lost — not just that both calls returned
        // Submitted (which the earlier sequential version of this test only checked).
        var applicationsResult = await mediator.Send(new CompanyApplicationsQuery(companyId, founderId));
        Assert.True(applicationsResult is CompanyApplicationsResult.Found, $"Expected Found, got {applicationsResult}");
        if (applicationsResult is CompanyApplicationsResult.Found found)
        {
            Assert.Contains(found.Applications, a => a.CharacterId == applicantAId);
            Assert.Contains(found.Applications, a => a.CharacterId == applicantBId);
        }

        async Task<SubmitApplicationResult> SubmitWithRetryAsync(CompanyId company, CharacterId applicant)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await using var innerScope = _provider.CreateAsyncScope();
                var innerMediator = innerScope.ServiceProvider.GetRequiredService<IMediator>();
                var result = await innerMediator.Send(new SubmitApplicationCommand(company, applicant, "please let me in"));
                if (result is not SubmitApplicationResult.ConcurrentModification)
                {
                    return result;
                }
            }

            throw new InvalidOperationException("Did not converge after 5 retries.");
        }
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        var session = await mediator.Send(new CreateSessionCommand(new GameId(Guid.NewGuid())));
        _createdUsernames.Add(session.KeycloakUsername);
        var result = await mediator.Send(new CreateCharacterCommand(session.AccountId, "Concurrency Test Character"));
        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }
}
