namespace ELifeRPG.Accounts.Domain.Events;

/// <summary>
/// Raised on the <b>losing</b> account when two accounts turn out to be the same player: its web
/// identity and whitelist applications move to <paramref name="AbsorbedIntoAccountId"/>, and its
/// own subject is cleared so a subject lookup stays single-valued.
///
/// The stream is kept rather than deleted so "what happened to my old account id" stays answerable.
/// Safe only under the assumption that the loser has no game-account links — see
/// <c>IAccountIdentityReconciler</c>, which refuses the merge outright when both sides have them,
/// because this module cannot see Characters/Banking/Shops state and so cannot know which side is
/// safe to lose.
/// </summary>
public sealed record AccountAbsorbed(AccountId Id, AccountId AbsorbedIntoAccountId);
