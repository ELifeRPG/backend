namespace ELifeRPG.Items.Domain;

/// <summary>
/// Whether an instance of this catalog entry, once left lying in the world, is eventually swept.
///
/// Ordered so that <see cref="Despawns"/> is 0: an event replayed from before this field existed
/// binds to the default, and "a dropped bandage eventually disappears" is a far safer wrong answer
/// than "a player's parked truck lives forever" would be in reverse.
/// </summary>
public enum ItemPersistence
{
    /// <summary>Gets a TTL when dropped on the ground, and is reclaimed once it expires.</summary>
    Despawns = 0,

    /// <summary>Never expires while parented to the world — vehicles, deployables, placed structures.</summary>
    Persistent = 1,
}
