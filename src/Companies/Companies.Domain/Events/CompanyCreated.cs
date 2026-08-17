namespace ELifeRPG.Companies.Domain.Events;

/// <summary>
/// Every company is seeded with two positions: "Owner" (Ordering 1, every CompanyPermissions flag)
/// and "Rookie" (Ordering 10, no permissions). Legacy's own Company constructor only ever seeded a
/// single "Rookie" position and never implemented any way to add more — but legacy also never had a
/// create-company-with-a-founder flow at all (Companies were seeded via the Migrator, unrelated to
/// any specific founding member). Since this codebase added founder-auto-join itself, an owner-less
/// founder made no sense — and without a way to grant elevated permissions to anyone, corporate bank
/// account authorization (see Banking) would have had no reachable success path to test against.
/// Position *management* (adding further custom positions) is still not implemented, matching
/// legacy's own gap — this only adds the one extra seeded position needed to make founder ownership
/// and corporate authorization meaningful.
/// </summary>
public sealed record CompanyCreated(CompanyId Id, string Name, CompanyPositionId OwnerPositionId, CompanyPositionId DefaultPositionId);
