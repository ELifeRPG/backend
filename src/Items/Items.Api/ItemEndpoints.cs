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

    /// <summary>
    /// A bulk import is one Postgres transaction, so the cap doubles as a lock-duration cap. Sized
    /// for a full Reforger prefab dump arriving in a handful of chunks rather than one giant request.
    /// </summary>
    private const int MaxBulkImportItems = 1000;

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
                if (!TryParsePersistence(request.Persistence, out var persistence, out var problem))
                {
                    return problem;
                }

                var result = await mediator.Send(request.ToCommand(persistence), cancellationToken);
                return result switch
                {
                    CreateItemResult.Created created => Results.Ok(new ItemDto
                    {
                        ItemId = created.ItemId.Value,
                        DisplayName = request.DisplayName,
                        PrefabClassName = request.PrefabClassName,
                        Persistence = persistence.ToString(),
                    }),
                    CreateItemResult.DuplicatePrefabClassName duplicate => Results.Problem(
                        title: "Prefab class name already in the catalog",
                        detail: $"'{request.PrefabClassName}' is already registered as item {duplicate.ExistingItemId.Value}.",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(ItemsManagePolicy)
            .Produces<ItemDto>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CreateItem")
            .WithDescription("Adds an item to the catalog. Prefab class names are unique.");

        group.MapPost("items/bulk", async (
                [FromBody] BulkImportItemsRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (request.Items.Count > MaxBulkImportItems)
                {
                    return Results.Problem(
                        title: "Too many items",
                        detail: $"At most {MaxBulkImportItems} items may be imported per request.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var persistence = new List<ItemPersistence>(request.Items.Count);
                foreach (var candidate in request.Items)
                {
                    if (!TryParsePersistence(candidate.Persistence, out var parsed, out var itemProblem))
                    {
                        return itemProblem;
                    }

                    persistence.Add(parsed);
                }

                var result = await mediator.Send(request.ToCommand(persistence), cancellationToken);
                return result switch
                {
                    BulkImportItemsResult.Imported imported => Results.Ok(BulkImportItemsResponseDto.Create(imported.Results)),
                    BulkImportItemsResult.DuplicateInPayload duplicate => Results.Problem(
                        title: "Duplicate prefab class names",
                        detail: $"The payload names these prefabs more than once: {string.Join(", ", duplicate.PrefabClassNames)}.",
                        statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .RequireAuthorization(ItemsManagePolicy)
            .Produces<BulkImportItemsResponseDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("BulkImportItems")
            .WithDescription(
                "Registers many prefabs at once. Idempotent on prefab class name: prefabs already in "
                + "the catalog are returned untouched. Only catalogued prefabs are persisted by the "
                + "World module, so this is how a world is made to persist anything at all.");

        group.MapGet("items", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var catalog = await mediator.Send(new ItemsQuery(), cancellationToken);
                return Results.Ok(ItemCatalogDto.Create(catalog));
            })
            .RequireAuthorization(ItemsManagePolicy)
            .Produces<ItemCatalogDto>()
            .WithName("GetItems")
            .WithDescription(
                "Lists the item catalog with a version stamp. The Bridge fetches this at boot and "
                + "re-fetches when catalogVersion changes.");

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

    // No JsonStringEnumConverter is configured in this solution — enum-typed DTO properties would
    // only bind from their ordinal — so persistence crosses the wire as a string and is parsed here.
    // Same convention as ShopEndpoints' ownerType.
    private static bool TryParsePersistence(string? raw, out ItemPersistence persistence, out IResult? problem)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            persistence = ItemPersistence.Despawns;
            problem = null;
            return true;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out persistence) && Enum.IsDefined(persistence))
        {
            problem = null;
            return true;
        }

        persistence = ItemPersistence.Despawns;
        problem = Results.Problem(
            title: $"persistence must be one of: {string.Join(", ", Enum.GetNames<ItemPersistence>())}",
            statusCode: StatusCodes.Status400BadRequest);
        return false;
    }

    private static bool HasScope(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, string scope)
        => (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(scope);
}
