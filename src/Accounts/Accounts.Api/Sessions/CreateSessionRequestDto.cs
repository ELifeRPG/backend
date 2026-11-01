using ELifeRPG.Accounts.Domain;

namespace ELifeRPG.Accounts.Api.Sessions;

public sealed record CreateSessionRequestDto
{
    /// <summary>
    /// The player's per-platform Game Identity ID, as the gameserver reports it — <b>not</b> their
    /// Bohemia account, which is the web identity they may link later.
    /// </summary>
    public required Guid GameIdentityId { get; init; }

    /// <summary>
    /// Optional: "Pc", "Xbox" or "PlayStation". Descriptive only, recorded on the link for staff.
    /// An unrecognised or absent value is simply ignored rather than rejected — nothing keys on it,
    /// and the mod may not report one.
    /// </summary>
    public string? Platform { get; init; }

    public CreateSessionCommand ToCommand() => new(
        new GameIdentityId(GameIdentityId),
        Enum.TryParse<GamePlatform>(Platform, ignoreCase: true, out var platform) ? platform : null);
}
