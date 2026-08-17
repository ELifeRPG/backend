using ELifeRPG.Shops.Domain.Events;
using ELifeRPG.Shops.Domain.Exceptions;

namespace ELifeRPG.Shops.Application.Common;

public interface IShopListingRepository
{
    ValueTask<ShopListing?> FindByIdAsync(ShopListingId listingId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ShopListing>> FindByShopIdAsync(ShopId shopId, CancellationToken cancellationToken);

    void StartStream(ShopListing listing, ListingCreated domainEvent);

    /// <summary>Appends an event to an already-started listing stream — same pattern as IBankAccountRepository.Append.</summary>
    void Append<TEvent>(ShopListingId listingId, TEvent domainEvent) where TEvent : notnull;

    /// <summary>
    /// Loads the listing and applies a purchase of `quantity` in one operation. On the plain
    /// (non-cross-module) repository, this uses Marten's FetchForWriting-based optimistic
    /// concurrency (fetch and append against the same tracked stream version) so two concurrent
    /// purchases can never both succeed. On the cross-module repository (obtained via
    /// IShopListingRepositoryFactory), Marten's version-checked append machinery does not work on a
    /// SessionOptions.ForTransaction-bound session (verified against live Postgres — see
    /// docs/superpowers/specs/2026-08-16-purchase-listing-cross-module-migration.md) — that
    /// implementation instead acquires a Postgres row lock (`SELECT ... FOR UPDATE` on the listing's
    /// doc-table row) before loading/mutating, then appends a plain, unversioned event; the lock
    /// held for the rest of the transaction gives the same "two concurrent purchases can never both
    /// succeed" guarantee. Either way this is the one repository method that calls into domain
    /// behavior (ShopListing.Purchase) rather than just persisting an event the caller already
    /// produced.
    ///
    /// As of the PurchaseListingCommand migration onto ICrossModuleTransaction, the cross-module
    /// (row-lock) branch above is the only branch a real purchase exercises in production —
    /// PurchaseListingHandler is the only caller of this method, and it always goes through
    /// IShopListingRepositoryFactory. The FetchForWriting-based branch and its
    /// ListingPurchaseConflictException translation (see SaveChangesAsync below) are retained as
    /// this interface's general-purpose optimistic-concurrency primitive, available to any future
    /// non-cross-module caller, but they are not currently exercised by the purchase flow.
    ///
    /// The row lock's scope is narrower than "all writes to this listing": it only mutually excludes
    /// concurrent *purchases* of the same listing (i.e. other calls to this method). It does not
    /// participate in the same lock as UpdateListingCommand's or RemoveListingCommand's plain
    /// (unlocked) writes to the same doc row, so a purchase's stock decrement can in theory be
    /// silently overwritten by a concurrent listing edit/removal racing it (money is never at risk —
    /// Banking's side of the transaction is fully atomic regardless — only the listing's stock count
    /// can drift, and only when a seller admin action races a buyer purchase). Extending the lock to
    /// those handlers is a reasonable follow-up, deliberately left out of this fix.
    ///
    /// Deliberately does NOT call SaveChangesAsync — callers using a cross-module transaction (see
    /// IShopListingRepositoryFactory) need to defer the actual database round-trip until every
    /// participating repository is ready to flush together, right before one CommitAsync. Throws
    /// InsufficientStockException immediately (from ShopListing.Purchase) if the freshly-loaded
    /// stock can't cover `quantity`.
    /// </summary>
    ValueTask<ShopListing> ReserveStockAsync(ShopListingId listingId, int quantity, CancellationToken cancellationToken);

    /// <summary>
    /// Flushes pending appends to Postgres. On the plain (non-cross-module) repository, throws
    /// ListingPurchaseConflictException — translated from Marten/JasperFx's ConcurrencyException —
    /// if a ReserveStockAsync-fetched stream was committed to by another writer since the fetch. On
    /// the cross-module repository this should never actually throw a concurrency conflict here,
    /// since ReserveStockAsync's row lock already serialized access before this point — a conflict
    /// there would mean the row-lock invariant was violated, which should be treated as a genuine
    /// bug, not a normal business-result case.
    ///
    /// As with ReserveStockAsync above, the plain (non-cross-module) branch this exception comes
    /// from is not exercised by any purchase in production today — PurchaseListingHandler always
    /// goes through the cross-module, row-locked path. It's retained here as general-purpose
    /// optimistic-concurrency behavior for any future non-cross-module caller of this interface.
    /// </summary>
    ValueTask SaveChangesAsync(CancellationToken cancellationToken);
}
