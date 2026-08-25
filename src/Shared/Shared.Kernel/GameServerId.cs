namespace ELifeRPG.Shared.Kernel;

/// <summary>
/// Owned by the Accounts module; lives here so other modules can reference a game server without
/// depending on Accounts.Domain (see ARCHITECTURE.md §9e). Replaces the OAuth client_id string as
/// the durable identity of a server — character rows reference this, so rotating a Keycloak client
/// must not orphan them (see docs/superpowers/specs/2026-08-22-hive-tenancy-design.md, Part 2).
/// </summary>
[StronglyTypedId]
public partial struct GameServerId;
