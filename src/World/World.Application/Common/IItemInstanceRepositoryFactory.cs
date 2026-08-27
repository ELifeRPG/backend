using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.World.Application.Common;

/// <summary>
/// Builds a World repository bound to a shared cross-module transaction's session instead of this
/// module's normal per-request session — used only by orchestrating commands in another module (e.g.
/// Shops' <c>PurchaseListingHandler</c>, task 6) or this module's own gathering orchestrator (task 7)
/// that write to World and another module atomically. Mirrors
/// <c>Banking.Application.Common.IBankAccountRepositoryFactory</c> exactly. See
/// docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public interface IItemInstanceRepositoryFactory
{
    IItemInstanceRepository CreateFor(CrossModuleSessionHandle handle);
}
