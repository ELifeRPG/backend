using ELifeRPG.Items.Api.Items;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class ItemModule
{
    public const string ItemsManageScope = "gameserver:items:manage";
    private const string ItemsManagePolicy = "Items.Manage";

    public static IServiceCollection AddItemModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddItemInfrastructure(configuration);

        services.AddAuthorizationBuilder()
            .AddPolicy(ItemsManagePolicy, policy => policy.RequireAssertion(context => HasScope(context, ItemsManageScope)));

        return services;
    }

    public static WebApplication MapItemModule(this WebApplication app)
    {
        var group = app.MapGroup("api").WithTags("Items");

        group.MapPost("items", async (
                [FromBody] CreateItemRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(), cancellationToken);
                return result switch
                {
                    CreateItemResult.Created created => Results.Ok(new ItemDto
                    {
                        ItemId = created.ItemId.Value,
                        DisplayName = request.DisplayName,
                        PrefabClassName = request.PrefabClassName,
                    }),
                };
            })
            .RequireAuthorization(ItemsManagePolicy)
            .Produces<ItemDto>()
            .WithName("CreateItem")
            .WithDescription("Adds an item to the catalog.");

        group.MapGet("items", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var items = await mediator.Send(new ItemsQuery(), cancellationToken);
                return Results.Ok(items.Select(ItemDto.Create).ToList());
            })
            .RequireAuthorization(ItemsManagePolicy)
            .Produces<List<ItemDto>>()
            .WithName("GetItems")
            .WithDescription("Lists the item catalog.");

        group.MapGet("items/{itemId:guid}", async (
                Guid itemId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new ItemLookupQuery(new ItemId(itemId)), cancellationToken);
                return result switch
                {
                    ItemLookupResult.Found found => Results.Ok(ItemDto.Create(found.Item)),
                    ItemLookupResult.NotFound => Results.Problem(title: "Item not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ItemsManagePolicy)
            .Produces<ItemDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetItem")
            .WithDescription("Gets a single catalog item.");

        return app;
    }

    private static bool HasScope(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, string scope)
        => (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(scope);
}
