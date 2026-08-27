using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Companies.Application.Common;

/// <summary>
/// Builds a Companies repository bound to a shared cross-module transaction's session instead of
/// this module's normal per-request session — used only by orchestrating commands elsewhere (e.g.
/// Banking.Application.Companies.PurchaseCompanySharesCommand) that write to Companies and another
/// module atomically.
/// </summary>
public interface ICompanyRepositoryFactory
{
    ICompanyRepository CreateFor(CrossModuleSessionHandle handle);
}
