using Marten;

namespace ELifeRPG.World.Infrastructure.Common;

/// <summary>World's own independent Marten store (own schema, own connection lifecycle) — see ARCHITECTURE.md §9e.</summary>
public interface IWorldStore : IDocumentStore;
