namespace ELifeRPG.Accounts.Domain;

/// <summary>
/// One in-game identity bound to one account. Arma Reforger mints a Game Identity per
/// game-platform combination, so a player on PC, Xbox and PS5 holds three — hence a document per
/// link rather than a field on <see cref="Account"/>.
///
/// Identity is <see cref="Id"/>, not <see cref="GameIdentityId"/>, mirroring
/// <see cref="GameServer"/>/<c>ClientId</c>: the natural key carries a unique index, which is what
/// makes "one game identity belongs to at most one account" a write-time guarantee rather than a
/// race. That index is also why <c>FindByGameIdentityIdAsync</c> can safely use
/// <c>SingleOrDefaultAsync</c>, and why two concurrent joins for one identity cannot produce two
/// accounts.
/// </summary>
public sealed class GameAccountLink
{
    public GameAccountLinkId Id { get; private set; }

    public GameIdentityId GameIdentityId { get; private set; }

    /// <summary>Mutable: the absorb re-points surviving links rather than recreating them.</summary>
    public AccountId AccountId { get; private set; }

    public GamePlatform? Platform { get; private set; }

    public DateTimeOffset LinkedAt { get; private set; }

    public static GameAccountLink Create(
        GameIdentityId gameIdentityId,
        AccountId accountId,
        GamePlatform? platform,
        DateTimeOffset linkedAt)
        => new()
        {
            Id = new GameAccountLinkId(Guid.NewGuid()),
            GameIdentityId = gameIdentityId,
            AccountId = accountId,
            Platform = platform,
            LinkedAt = linkedAt,
        };

    public void RepointTo(AccountId survivor) => AccountId = survivor;
}
