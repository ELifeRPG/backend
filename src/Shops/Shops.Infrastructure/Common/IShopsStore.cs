using Marten;

namespace ELifeRPG.Shops.Infrastructure.Common;

/// <summary>Shops' own independent Marten store (own schema, own connection lifecycle) — see ARCHITECTURE.md §9e.</summary>
public interface IShopsStore : IDocumentStore;
