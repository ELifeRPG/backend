using Marten;

namespace ELifeRPG.Characters.Infrastructure.Common;

/// <summary>
/// Characters' own independent Marten store (own schema, own connection lifecycle) — kept separate
/// from Accounts' default IDocumentStore/IDocumentSession via Marten's multi-store support
/// (AddMartenStore&lt;T&gt;), verified to actually isolate schemas rather than silently sharing/
/// colliding with the default store registered by Accounts.Infrastructure.
/// </summary>
public interface ICharactersStore : IDocumentStore;
