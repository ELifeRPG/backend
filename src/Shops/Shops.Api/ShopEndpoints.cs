using ELifeRPG.Shops.Api;
using ELifeRPG.Shops.Api.Common;
using ELifeRPG.Shops.Api.Shops;
using ELifeRPG.Shops.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class ShopModule
{
    public const string ShopsManageScope = "gameserver:shops:manage";
    public const string ShopsWriteScope = "gameserver:shops:write";
    private const string ShopsManagePolicy = "Shops.Manage";
    private const string ShopsWritePolicy = "Shops.Write";

    public static IServiceCollection AddShopModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddShopInfrastructure(configuration);
        services.AddSignalR();
        services.AddSingleton<ShopsHubNotifier>();
        services.AddScoped<ICurrentGameServer, HttpContextCurrentGameServer>();

        services.AddAuthorizationBuilder()
            .AddPolicy(ShopsManagePolicy, policy => policy.RequireAssertion(context => HasScope(context, ShopsManageScope)))
            .AddPolicy(ShopsWritePolicy, policy => policy.RequireAssertion(context => HasScope(context, ShopsWriteScope)));

        return services;
    }

    public static WebApplication MapShopModule(this WebApplication app)
    {
        var group = app.MapGroup("api").WithTags("Shops");

        app.MapHub<ShopsHub>("hubs/shops").RequireAuthorization(ShopsWritePolicy);

        group.MapPost("shops", async (
                [FromBody] OpenShopRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (!Enum.TryParse<ShopOwnerType>(request.OwnerType, out var ownerType))
                {
                    return Results.Problem(
                        title: $"ownerType must be one of: {string.Join(", ", Enum.GetNames<ShopOwnerType>())}",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var hasCharacterId = request.OwnerCharacterId is not null;
                var hasCompanyId = request.OwnerCompanyId is not null;
                var ownerReferenceValid = ownerType == ShopOwnerType.Personal
                    ? hasCharacterId && !hasCompanyId
                    : hasCompanyId && !hasCharacterId;

                if (!ownerReferenceValid)
                {
                    return Results.Problem(
                        title: "A Personal shop requires exactly ownerCharacterId; a Corporate shop requires exactly ownerCompanyId",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var result = await mediator.Send(request.ToCommand(ownerType), cancellationToken);

                return result switch
                {
                    OpenShopResult.Opened opened => Results.Ok(new ShopDto
                    {
                        ShopId = opened.ShopId.Value,
                        OwnerType = request.OwnerType,
                        OwnerCharacterId = request.OwnerCharacterId,
                        OwnerCompanyId = request.OwnerCompanyId,
                        DisplayName = request.DisplayName,
                        PayoutBankAccountId = request.PayoutBankAccountId,
                    }),
                    OpenShopResult.CharacterNotFound => Results.Problem(title: "Character not found", statusCode: StatusCodes.Status404NotFound),
                    OpenShopResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ShopsManagePolicy)
            .Produces<ShopDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("OpenShop")
            .WithDescription("Opens a shop for a character (Personal) or a company (Corporate) — provide exactly one of ownerCharacterId/ownerCompanyId matching ownerType.");

        group.MapGet("shops", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                var shops = await mediator.Send(new ShopsQuery(), cancellationToken);
                return Results.Ok(shops.Select(ShopDto.Create).ToList());
            })
            .RequireAuthorization(ShopsWritePolicy)
            .Produces<List<ShopDto>>()
            .WithName("GetShops")
            .WithDescription("Lists all shops.");

        group.MapGet("shops/{shopId:guid}", async (
                Guid shopId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new ShopQuery(new ShopId(shopId)), cancellationToken);
                return result switch
                {
                    ShopQueryResult.Found found => Results.Ok(ShopDetailsDto.Create(found.Shop, found.Listings)),
                    ShopQueryResult.NotFound => Results.Problem(title: "Shop not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(ShopsWritePolicy)
            .Produces<ShopDetailsDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetShop")
            .WithDescription("Gets a shop and its active listings.");

        group.MapPost("shops/{shopId:guid}/listings", async (
                Guid shopId,
                [FromBody] AddListingRequestDto request,
                IMediator mediator,
                ShopsHubNotifier notifier,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(shopId), cancellationToken);

                if (result is AddListingResult.Added addedNotify)
                {
                    try
                    {
                        await notifier.NotifyListingChangedAsync(shopId, addedNotify.ListingId.Value, request.Price, request.Stock, cancellationToken);
                    }
                    catch (Exception)
                    {
                        // The write already committed; a failed live push must never turn a successful
                        // mutation into a 500 for the caller. Swallowed rather than logged because no
                        // endpoint in this codebase injects an ILogger yet — introducing that convention
                        // here would be out of scope for this fix.
                    }
                }

                return result switch
                {
                    AddListingResult.Added added => Results.Ok(new ShopListingDto
                    {
                        ListingId = added.ListingId.Value,
                        ItemId = request.ItemId,
                        Price = request.Price,
                        Stock = request.Stock,
                    }),
                    AddListingResult.InvalidPrice => Results.Problem(
                        title: "Price must be greater than zero", statusCode: StatusCodes.Status400BadRequest),
                    AddListingResult.ShopNotFound => Results.Problem(title: "Shop not found", statusCode: StatusCodes.Status404NotFound),
                    AddListingResult.ItemNotFound => Results.Problem(title: "Item not found", statusCode: StatusCodes.Status404NotFound),
                    AddListingResult.NotAuthorized => Results.Problem(title: "Not authorized to manage this shop", statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(ShopsWritePolicy)
            .Produces<ShopListingDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("AddShopListing")
            .WithDescription("Adds a catalog item listing to a shop.");

        group.MapPut("shops/{shopId:guid}/listings/{listingId:guid}", async (
                Guid shopId,
                Guid listingId,
                [FromBody] UpdateListingRequestDto request,
                IMediator mediator,
                ShopsHubNotifier notifier,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(shopId, listingId), cancellationToken);

                if (result is UpdateListingResult.Updated)
                {
                    try
                    {
                        await notifier.NotifyListingChangedAsync(shopId, listingId, request.Price, request.Stock, cancellationToken);
                    }
                    catch (Exception)
                    {
                        // See the note on the add-listing push: a push failure must not mask a
                        // committed write.
                    }
                }

                return result switch
                {
                    UpdateListingResult.Updated => Results.Ok(),
                    UpdateListingResult.ShopNotFound => Results.Problem(title: "Shop not found", statusCode: StatusCodes.Status404NotFound),
                    UpdateListingResult.ListingNotFound => Results.Problem(title: "Listing not found", statusCode: StatusCodes.Status404NotFound),
                    UpdateListingResult.NotAuthorized => Results.Problem(title: "Not authorized to manage this shop", statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(ShopsWritePolicy)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UpdateShopListing")
            .WithDescription("Updates a shop listing's price and stock.");

        group.MapDelete("shops/{shopId:guid}/listings/{listingId:guid}", async (
                Guid shopId,
                Guid listingId,
                [FromQuery] Guid actingCharacterId,
                IMediator mediator,
                ShopsHubNotifier notifier,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new RemoveListingCommand(new ShopId(shopId), new ShopListingId(listingId), new CharacterId(actingCharacterId)),
                    cancellationToken);

                if (result is RemoveListingResult.Removed)
                {
                    try
                    {
                        await notifier.NotifyListingRemovedAsync(shopId, listingId, cancellationToken);
                    }
                    catch (Exception)
                    {
                        // See the note on the add-listing push: a push failure must not mask a
                        // committed write.
                    }
                }

                return result switch
                {
                    RemoveListingResult.Removed => Results.NoContent(),
                    RemoveListingResult.ShopNotFound => Results.Problem(title: "Shop not found", statusCode: StatusCodes.Status404NotFound),
                    RemoveListingResult.ListingNotFound => Results.Problem(title: "Listing not found", statusCode: StatusCodes.Status404NotFound),
                    RemoveListingResult.NotAuthorized => Results.Problem(title: "Not authorized to manage this shop", statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(ShopsWritePolicy)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RemoveShopListing")
            .WithDescription("Removes (soft-deletes) a shop listing.");

        group.MapPost("shops/{shopId:guid}/listings/{listingId:guid}/purchase", async (
                Guid shopId,
                Guid listingId,
                [FromBody] PurchaseListingRequestDto request,
                IMediator mediator,
                ShopsHubNotifier notifier,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(shopId, listingId), cancellationToken);

                if (result is PurchaseListingResult.Purchased purchasedNotify)
                {
                    // TotalPaid is the whole-order amount the handler computed as listing.Price * Quantity,
                    // so dividing recovers the per-unit price exactly. Quantity is necessarily >= 1 here:
                    // ShopListing.Purchase rejects a non-positive quantity before any Purchased result exists.
                    var unitPrice = purchasedNotify.TotalPaid / request.Quantity;

                    try
                    {
                        await notifier.NotifyListingChangedAsync(shopId, listingId, unitPrice, purchasedNotify.NewStock, cancellationToken);
                    }
                    catch (Exception)
                    {
                        // See the note on the add-listing push: a push failure must not mask a
                        // committed write — least of all one that already moved money.
                    }
                }

                return result switch
                {
                    PurchaseListingResult.Purchased purchased => Results.Ok(new { purchased.TotalPaid, purchased.NewStock }),
                    PurchaseListingResult.ShopNotFound => Results.Problem(title: "Shop not found", statusCode: StatusCodes.Status404NotFound),
                    PurchaseListingResult.ListingNotFound => Results.Problem(title: "Listing not found", statusCode: StatusCodes.Status404NotFound),
                    PurchaseListingResult.BuyerAccountNotFound => Results.Problem(title: "Buyer bank account not found", statusCode: StatusCodes.Status404NotFound),
                    PurchaseListingResult.InsufficientStock => Results.Problem(title: "Not enough stock", statusCode: StatusCodes.Status409Conflict),
                    PurchaseListingResult.InsufficientBalance => Results.Problem(title: "Insufficient balance", statusCode: StatusCodes.Status409Conflict),
                    PurchaseListingResult.ListingChangedConcurrently => Results.Problem(
                        title: "Listing changed concurrently, try again", statusCode: StatusCodes.Status409Conflict),
                    PurchaseListingResult.NotAuthorized => Results.Problem(
                        title: "Buyer is not authorized on the given bank account", statusCode: StatusCodes.Status403Forbidden),
                };
            })
            .RequireAuthorization(ShopsWritePolicy)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("PurchaseShopListing")
            .WithDescription("Purchases a quantity of a shop listing, settling payment via Banking.");

        return app;
    }

    private static bool HasScope(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, string scope)
        => (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(scope);
}
