namespace ELifeRPG.Banking.Application.Common;

/// <summary>
/// The gameserver whose data the current request should be scoped to — resolves to the calling
/// Bridge's own OAuth client id. Every session this module opens is scoped to this value, so a
/// Bank/BankAccount opened via one gameserver is invisible from another, even within the same
/// tenant. See docs/superpowers/specs/2026-08-15-multi-gameserver-tenancy-design.md.
/// </summary>
public interface ICurrentGameServer
{
    string ClientId { get; }
}
