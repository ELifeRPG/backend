using ELifeRPG.Companies.Api.Companies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class CompanyModule
{
    public const string CompaniesWriteScope = "gameserver:companies:write";
    private const string CompaniesWritePolicy = "Companies.Write";

    public static IServiceCollection AddCompanyModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCompanyInfrastructure(configuration);

        services.AddAuthorizationBuilder()
            .AddPolicy(CompaniesWritePolicy, policy => policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(CompaniesWriteScope)));

        return services;
    }

    public static WebApplication MapCompanyModule(this WebApplication app)
    {
        var group = app.MapGroup("api").WithTags("Companies");

        group.MapPost("companies", async (
                [FromBody] CreateCompanyRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(), cancellationToken);

                return result switch
                {
                    CreateCompanyResult.Created created => Results.Ok(CompanyDto.Create(created, request.Name)),
                    CreateCompanyResult.FounderNotFound => Results.Problem(
                        title: "Founder character not found",
                        statusCode: StatusCodes.Status404NotFound),
                    CreateCompanyResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this company",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces<CompanyDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreateCompany")
            .WithDescription("Creates a new company; the founder becomes its first member.");

        group.MapGet("companies", async (
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var companies = await mediator.Send(new CompaniesQuery(), cancellationToken);
                return Results.Ok(companies.Select(CompanyDto.Create).ToList());
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces<List<CompanyDto>>()
            .WithName("ListCompanies")
            .WithDescription("Lists companies.");

        group.MapGet("companies/{companyId:guid}", async (
                Guid companyId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new CompanyDetailsQuery(new CompanyId(companyId)), cancellationToken);

                return result switch
                {
                    CompanyDetailsResult.Found found => Results.Ok(CompanyDetailsDto.Create(found.Company)),
                    CompanyDetailsResult.NotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces<CompanyDetailsDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCompany")
            .WithDescription("Gets company details, including its members.");

        group.MapPost("companies/{companyId:guid}/members", async (
                Guid companyId,
                [FromBody] AddMemberRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(companyId), cancellationToken);

                return result switch
                {
                    AddMemberResult.Added => Results.Ok(),
                    AddMemberResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    AddMemberResult.CharacterNotFound => Results.Problem(title: "Character not found", statusCode: StatusCodes.Status404NotFound),
                    AddMemberResult.AlreadyMember => Results.Problem(
                        title: "Character is already a member",
                        statusCode: StatusCodes.Status409Conflict),
                    AddMemberResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this company",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("AddCompanyMember")
            .WithDescription("Adds a character as a member of a company.");

        group.MapPost("companies/{companyId:guid}/applications", async (
                Guid companyId,
                [FromBody] SubmitApplicationRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (request.Message.Length > 1000)
                {
                    return Results.Problem(
                        title: "Message must be 1000 characters or fewer",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var result = await mediator.Send(request.ToCommand(companyId), cancellationToken);

                return result switch
                {
                    SubmitApplicationResult.Submitted submitted => Results.Ok(CompanyApplicationDto.Create(submitted, request)),
                    SubmitApplicationResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    SubmitApplicationResult.CharacterNotFound => Results.Problem(title: "Character not found", statusCode: StatusCodes.Status404NotFound),
                    SubmitApplicationResult.AlreadyMember => Results.Problem(
                        title: "Character is already a member",
                        statusCode: StatusCodes.Status409Conflict),
                    SubmitApplicationResult.DuplicateApplication => Results.Problem(
                        title: "Character already has an open application to this company",
                        statusCode: StatusCodes.Status409Conflict),
                    SubmitApplicationResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this company",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces<CompanyApplicationDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("SubmitCompanyApplication")
            .WithDescription("Submits a character's application to join a company.");

        group.MapGet("companies/{companyId:guid}/applications", async (
                Guid companyId,
                [FromQuery] Guid actingCharacterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new CompanyApplicationsQuery(new CompanyId(companyId), new CharacterId(actingCharacterId)),
                    cancellationToken);

                return result switch
                {
                    CompanyApplicationsResult.Found found => Results.Ok(found.Applications.Select(CompanyApplicationDto.Create).ToList()),
                    CompanyApplicationsResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    CompanyApplicationsResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to manage members of this company",
                        statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces<List<CompanyApplicationDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListCompanyApplications")
            .WithDescription("Lists a company's applications. Requires ManageMembers permission in the company.");

        group.MapPut("companies/{companyId:guid}/applications/{applicationId:guid}/confirm", async (
                Guid companyId,
                Guid applicationId,
                [FromBody] ActingCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToConfirmCommand(companyId, applicationId), cancellationToken);

                return result switch
                {
                    ConfirmApplicationResult.Confirmed => Results.Ok(),
                    ConfirmApplicationResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    ConfirmApplicationResult.ApplicationNotFound => Results.Problem(title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    ConfirmApplicationResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to manage members of this company",
                        statusCode: StatusCodes.Status403Forbidden),
                    ConfirmApplicationResult.InvalidState => Results.Problem(
                        title: "Application must be Pending to be confirmed",
                        statusCode: StatusCodes.Status409Conflict),
                    ConfirmApplicationResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this company",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ConfirmCompanyApplication")
            .WithDescription("Marks a pending application as InProgress. Requires ManageMembers permission in the company.");

        group.MapPut("companies/{companyId:guid}/applications/{applicationId:guid}/accept", async (
                Guid companyId,
                Guid applicationId,
                [FromBody] ActingCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToAcceptCommand(companyId, applicationId), cancellationToken);

                return result switch
                {
                    AcceptApplicationResult.Accepted => Results.Ok(),
                    AcceptApplicationResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    AcceptApplicationResult.ApplicationNotFound => Results.Problem(title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    AcceptApplicationResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to manage members of this company",
                        statusCode: StatusCodes.Status403Forbidden),
                    AcceptApplicationResult.InvalidState => Results.Problem(
                        title: "Application has already been decided",
                        statusCode: StatusCodes.Status409Conflict),
                    AcceptApplicationResult.AlreadyMember => Results.Problem(
                        title: "Character is already a member",
                        statusCode: StatusCodes.Status409Conflict),
                    AcceptApplicationResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this company",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("AcceptCompanyApplication")
            .WithDescription("Accepts an application, adding the character as a member in the company's default position. Requires ManageMembers permission in the company.");

        group.MapPut("companies/{companyId:guid}/applications/{applicationId:guid}/deny", async (
                Guid companyId,
                Guid applicationId,
                [FromBody] ActingCharacterRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToDenyCommand(companyId, applicationId), cancellationToken);

                return result switch
                {
                    DenyApplicationResult.Denied => Results.Ok(),
                    DenyApplicationResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    DenyApplicationResult.ApplicationNotFound => Results.Problem(title: "Application not found", statusCode: StatusCodes.Status404NotFound),
                    DenyApplicationResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to manage members of this company",
                        statusCode: StatusCodes.Status403Forbidden),
                    DenyApplicationResult.InvalidState => Results.Problem(
                        title: "Application has already been decided",
                        statusCode: StatusCodes.Status409Conflict),
                    DenyApplicationResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this company",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(CompaniesWritePolicy)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("DenyCompanyApplication")
            .WithDescription("Denies an application. Requires ManageMembers permission in the company.");

        return app;
    }
}
