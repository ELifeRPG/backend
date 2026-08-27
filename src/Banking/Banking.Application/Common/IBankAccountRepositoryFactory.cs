using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// Builds a Banking repository bound to a shared cross-module transaction's session instead of this
/// module's normal per-request session — used only by orchestrating commands (e.g.
/// PurchaseCompanySharesCommand) that write to Banking and another module atomically.
/// </summary>
public interface IBankAccountRepositoryFactory
{
    IBankAccountRepository CreateFor(CrossModuleSessionHandle handle);
}
