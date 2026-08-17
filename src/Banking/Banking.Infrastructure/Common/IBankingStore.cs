using Marten;

namespace ELifeRPG.Banking.Infrastructure.Common;

/// <summary>
/// Banking's own independent Marten store (own schema, own connection lifecycle), same pattern as
/// Characters' ICharactersStore — see ARCHITECTURE.md §9e. Hosts both the Bank and BankAccount
/// aggregates; they're separate streams but share this module's schema.
/// </summary>
public interface IBankingStore : IDocumentStore;
