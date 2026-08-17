using Marten;

namespace ELifeRPG.Companies.Infrastructure.Common;

/// <summary>Companies' own independent Marten store (own schema, own connection lifecycle) — see ARCHITECTURE.md §9e.</summary>
public interface ICompaniesStore : IDocumentStore;
