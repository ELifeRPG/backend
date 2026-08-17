using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Domain.Events;
using ELifeRPG.Shops.Domain.Exceptions;

namespace ELifeRPG.Shops.Application.Shops;

public union RemoveListingResult(RemoveListingResult.Removed, RemoveListingResult.ShopNotFound, RemoveListingResult.ListingNotFound, RemoveListingResult.NotAuthorized)
{
    public record Removed;

    public record ShopNotFound;

    public record ListingNotFound;

    public record NotAuthorized;
}

public sealed record RemoveListingCommand(ShopId ShopId, ShopListingId ListingId, CharacterId ActingCharacterId) : IRequest<RemoveListingResult>;

public sealed class RemoveListingHandler(IShopRepository shopRepository, IShopListingRepository listingRepository, IMediator mediator)
    : IRequestHandler<RemoveListingCommand, RemoveListingResult>
{
    public async ValueTask<RemoveListingResult> Handle(RemoveListingCommand request, CancellationToken cancellationToken)
    {
        var shop = await shopRepository.FindByIdAsync(request.ShopId, cancellationToken);
        if (shop is null)
        {
            return new RemoveListingResult.ShopNotFound();
        }

        if (!await ShopAuthorization.CanManageAsync(shop, request.ActingCharacterId, mediator, cancellationToken))
        {
            return new RemoveListingResult.NotAuthorized();
        }

        var listing = await listingRepository.FindByIdAsync(request.ListingId, cancellationToken);
        if (listing is null || listing.ShopId != request.ShopId)
        {
            return new RemoveListingResult.ListingNotFound();
        }

        ListingRemoved domainEvent;
        try
        {
            domainEvent = listing.Remove();
        }
        catch (ListingAlreadyRemovedException)
        {
            // DELETE is idempotent: the listing is already in the requested end state, so report
            // success without appending a second removal event.
            return new RemoveListingResult.Removed();
        }

        listingRepository.Append(request.ListingId, domainEvent);
        await listingRepository.SaveChangesAsync(cancellationToken);

        return new RemoveListingResult.Removed();
    }
}
