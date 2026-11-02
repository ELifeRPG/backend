namespace ELifeRPG.Accounts.Domain.Events;

/// <summary>
/// Audit record of a game identity attaching to this account, on the account's own stream.
/// <see cref="GameAccountLink"/> is what enforces uniqueness and answers lookups; this answers
/// "when did this identity attach, and on what platform", which is a moderation question.
///
/// Deliberately has no <c>Apply</c> on <see cref="Account"/>: the aggregate holds no game-identity
/// state any more, so there is nothing to project. That makes it the one event in this module where
/// a missing handler is correct rather than the duplicate-account hazard described on
/// <c>AccountProjection</c>.
/// </summary>
public sealed record GameAccountLinked(
    AccountId Id,
    GameIdentityId GameIdentityId,
    GamePlatform? Platform);
