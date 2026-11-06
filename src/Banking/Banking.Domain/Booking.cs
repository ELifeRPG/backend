namespace ELifeRPG.Banking.Domain;

/// <summary>
/// Who made a booking and where: the transaction id, the game server that sent it, and an optional
/// free-form <see cref="Source"/> such as the ATM's id. Recorded on every booking event so each
/// ledger row can be traced back to the request that made it.
/// </summary>
public sealed record Booking(BankTransactionId TransactionId, GameServerId GameServerId, string? Source)
{
    public const int MaxSourceLength = 128;

    public static bool IsValidSource(string? source) => source is null || (source.Length is > 0 and <= MaxSourceLength);

    public static Booking New(GameServerId gameServerId, string? source = null) =>
        new(new BankTransactionId(Guid.NewGuid()), gameServerId, source);
}
