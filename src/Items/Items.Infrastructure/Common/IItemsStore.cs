using Marten;

namespace ELifeRPG.Items.Infrastructure.Common;

/// <summary>Items' own independent Marten store (own schema, own connection lifecycle) — see ARCHITECTURE.md §9e.</summary>
public interface IItemsStore : IDocumentStore;
