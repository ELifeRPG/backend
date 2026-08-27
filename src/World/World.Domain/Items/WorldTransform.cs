namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// A plain 3-component vector — position or Euler-angle rotation, depending on where it's used.
/// Defined here rather than shared: the presence-heartbeat plan's <c>CharacterTransform</c> was
/// never implemented (only a forward-reference comment survives in
/// <c>Characters.Domain/Character.cs</c>), so there is no existing vector shape to reuse.
/// </summary>
public sealed record WorldVector3(float X, float Y, float Z);

/// <summary>
/// Where a world-parented instance sits. Only meaningful when <see cref="ItemInstance.ParentKind"/>
/// is <see cref="ParentKind.World"/> — <see cref="ItemInstance.Transform"/> is null otherwise.
/// </summary>
public sealed record WorldTransform(WorldVector3 Position, WorldVector3 Rotation);
