using ELifeRPG.Accounts.Api.Common;
using ELifeRPG.Accounts.Api.Whitelist;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class WhitelistModule
{
    public const string WhitelistWriteScope = "gameserver:whitelist:write";
    public const string WhitelistReviewerRole = "whitelist-reviewer";
    private const string WhitelistWritePolicy = "Whitelist.Write";
    public const string WhitelistReviewerPolicy = "Whitelist.Reviewer";

    public static IServiceCollection AddWhitelistModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(WhitelistWritePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(WhitelistWriteScope)))
            .AddPolicy(WhitelistReviewerPolicy, policy => policy.RequireAssertion(context =>
                RealmRoleAuthorization.HasRole(context.User, WhitelistReviewerRole)));

        return services;
    }

    public static WebApplication MapWhitelistModule(this WebApplication app)
    {
        var group = app.MapGroup("api/whitelist-applications").WithTags("Whitelist");

        group.MapPost("", async (
                [FromBody] SubmitWhitelistApplicationRequestDto request,
                ClaimsPrincipal user,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (request.ApplicationText.Length > 4000)
                {
                    return Results.Problem(
                        title: "Application text must be 4000 characters or fewer",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var serverClientId = user.FindFirst("client_id")?.Value ?? string.Empty;
                var result = await mediator.Send(request.ToCommand(serverClientId), cancellationToken);

                return result switch
                {
                    SubmitWhitelistApplicationResult.Submitted submitted => Results.Ok(WhitelistApplicationSubmittedDto.Create(submitted)),
                    SubmitWhitelistApplicationResult.AccountNotFound => Results.Problem(
                        title: "Account not found", statusCode: StatusCodes.Status404NotFound),
                    SubmitWhitelistApplicationResult.AlreadyPending => Results.Problem(
                        title: "Account already has a pending application for this server",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(WhitelistWritePolicy)
            .Produces<WhitelistApplicationSubmittedDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SubmitWhitelistApplication")
            .WithDescription("Submits an account's whitelist application for the calling server.");

        group.MapPost("{id:guid}/start-review", async (
                Guid id,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new StartWhitelistApplicationReviewCommand(new WhitelistApplicationId(id)), cancellationToken);

                return result switch
                {
                    StartWhitelistApplicationReviewResult.Started => Results.NoContent(),
                    StartWhitelistApplicationReviewResult.NotFound => Results.Problem(
                        title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(WhitelistReviewerPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("StartWhitelistApplicationReview")
            .WithDescription("Marks an Open application as InReview. Idempotent if already InReview.");

        group.MapPost("{id:guid}/approve", async (
                Guid id,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new ApproveWhitelistApplicationCommand(new WhitelistApplicationId(id)), cancellationToken);

                return result switch
                {
                    ApproveWhitelistApplicationResult.Approved => Results.NoContent(),
                    ApproveWhitelistApplicationResult.NotFound => Results.Problem(
                        title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    ApproveWhitelistApplicationResult.InvalidState => Results.Problem(
                        title: "Application must be InReview to be approved", statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(WhitelistReviewerPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ApproveWhitelistApplication")
            .WithDescription("Approves an InReview application. Idempotent if already Approved.");

        group.MapPost("{id:guid}/reject", async (
                Guid id,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new RejectWhitelistApplicationCommand(new WhitelistApplicationId(id)), cancellationToken);

                return result switch
                {
                    RejectWhitelistApplicationResult.Rejected => Results.NoContent(),
                    RejectWhitelistApplicationResult.NotFound => Results.Problem(
                        title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    RejectWhitelistApplicationResult.InvalidState => Results.Problem(
                        title: "Application must be InReview to be rejected", statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(WhitelistReviewerPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RejectWhitelistApplication")
            .WithDescription("Rejects an InReview application. Idempotent if already Rejected.");

        group.MapGet("", async (
                [FromQuery] WhitelistApplicationStatus status,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new WhitelistApplicationsQuery(status), cancellationToken);

                return result switch
                {
                    WhitelistApplicationsResult.Found found => Results.Ok(found.Applications.Select(WhitelistApplicationDto.Create).ToList()),
                };
            })
            .RequireAuthorization(WhitelistReviewerPolicy)
            .Produces<List<WhitelistApplicationDto>>()
            .WithName("ListWhitelistApplications")
            .WithDescription("Lists whitelist applications by status, for the review queue.");

        return app;
    }
}
