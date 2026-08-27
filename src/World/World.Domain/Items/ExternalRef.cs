namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// A live link to another module's own record about this same thing — e.g. a phone device bound to
/// the handset instance. Unlike <see cref="OriginRef"/>, this is not provenance and is free to change
/// over the instance's life (composed items can be re-linked). Kept as plain strings for the same
/// cross-module reason as <see cref="OriginRef"/>.
/// </summary>
public sealed record ExternalRef(string Module, string Id);
