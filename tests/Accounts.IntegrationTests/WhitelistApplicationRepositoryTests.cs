using ELifeRPG.Accounts.Application.Common;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Accounts.Domain.Events;
using ELifeRPG.Shared.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Accounts.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d`) and the devcontainer connected to its
/// network — see README.md. Not run as part of a normal `dotnet test` against an empty environment.
/// </summary>
public sealed class WhitelistApplicationRepositoryTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider(withInfrastructure: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task FindPendingAsync_AfterSubmit_ReturnsTheOpenApplication()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWhitelistApplicationRepository>();
        var accountId = new AccountId(Guid.NewGuid());
        var domainEvent = new WhitelistApplicationSubmitted(new WhitelistApplicationId(Guid.NewGuid()), accountId, "text");
        var application = WhitelistApplication.Create(domainEvent);
        repository.StartStream(application, domainEvent);
        await repository.SaveChangesAsync(CancellationToken.None);

        var pending = await repository.FindPendingAsync(accountId, CancellationToken.None);

        Assert.NotNull(pending);
        Assert.Equal(application.Id, pending!.Id);
    }

    [Fact]
    public async Task FindApprovedAsync_BeforeApproval_ReturnsNull()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWhitelistApplicationRepository>();
        var accountId = new AccountId(Guid.NewGuid());
        var domainEvent = new WhitelistApplicationSubmitted(new WhitelistApplicationId(Guid.NewGuid()), accountId, "text");
        var application = WhitelistApplication.Create(domainEvent);
        repository.StartStream(application, domainEvent);
        await repository.SaveChangesAsync(CancellationToken.None);

        var approved = await repository.FindApprovedAsync(accountId, CancellationToken.None);

        Assert.Null(approved);
    }

    [Fact]
    public async Task FindApprovedAsync_AfterApproval_ReturnsIt()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWhitelistApplicationRepository>();
        var accountId = new AccountId(Guid.NewGuid());
        var submitted = new WhitelistApplicationSubmitted(new WhitelistApplicationId(Guid.NewGuid()), accountId, "text");
        var application = WhitelistApplication.Create(submitted);
        repository.StartStream(application, submitted);
        await repository.SaveChangesAsync(CancellationToken.None);

        var reviewStarted = application.StartReview()!;
        repository.Append(application.Id, reviewStarted);
        var approved = application.Approve()!;
        repository.Append(application.Id, approved);
        await repository.SaveChangesAsync(CancellationToken.None);

        var found = await repository.FindApprovedAsync(accountId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(WhitelistApplicationStatus.Approved, found!.Status);
    }
}
