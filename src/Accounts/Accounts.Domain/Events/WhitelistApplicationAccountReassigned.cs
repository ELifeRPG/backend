namespace ELifeRPG.Accounts.Domain.Events;

/// <summary>
/// Re-points an application at the surviving account during an absorb. Applications are the one
/// thing besides the subject that has to carry over: a portal-first player typically applies before
/// ever joining, and an <c>Approved</c> application left behind would drop an already-whitelisted
/// player back to <c>not_whitelisted</c> on their next join.
/// </summary>
public sealed record WhitelistApplicationAccountReassigned(WhitelistApplicationId Id, AccountId AccountId);
