namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// Which platform a <see cref="GameAccountLink"/> was first seen on. Descriptive only — a Game
/// Identity ID is already globally unique, so nothing keys on this and a null platform is a normal
/// state (the gameserver may not report one).
///
/// Append only — ordinals are persisted in Marten payloads for the same reason as
/// <see cref="AccountStatus"/>: no <c>JsonStringEnumConverter</c> is configured, so inserting a
/// member mid-list remaps every stored value.
/// </summary>
public enum GamePlatform
{
    Pc = 0,
    Xbox = 1,
    PlayStation = 2,
}
