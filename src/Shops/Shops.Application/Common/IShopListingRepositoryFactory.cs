using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Shops.Application.Common;

/// <summary>
/// Builds a Shops listing repository bound to a shared cross-module transaction's session instead of
/// this module's normal per-request session — used only by orchestrating commands (e.g.
/// Shops.Application.Shops.PurchaseListingCommand) that write to Shops and another module atomically.
/// </summary>
public interface IShopListingRepositoryFactory
{
    IShopListingRepository CreateFor(CrossModuleSessionHandle handle);
}
