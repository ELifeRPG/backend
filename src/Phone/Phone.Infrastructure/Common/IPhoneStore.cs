using Marten;

namespace ELifeRPG.Phone.Infrastructure.Common;

/// <summary>Phone's own independent Marten store (own schema, own connection lifecycle) — see ARCHITECTURE.md §9e.</summary>
public interface IPhoneStore : IDocumentStore;
