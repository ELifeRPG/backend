using System.Threading.RateLimiting;

namespace ELifeRPG.Phone.Api.Apps.Phone;

/// <summary>
/// The bucket bounding the ring poll, and the single place its numbers are written down. Modelled on
/// <c>InventoryRateLimits</c>, including the choice of a token bucket and <c>QueueLimit = 0</c>, for
/// the same reasons given there.
///
/// <b>Why this is the first phone endpoint with a limit at all.</b> Every other phone route is driven
/// by a player doing something — sending a text, installing an app — and is self-limiting at human
/// speed, with the one rate that matters already enforced per phone in the domain
/// (<c>HiveSettings.SmsPerMinutePerPhone</c>). The ring poll is the opposite: it is driven by a timer,
/// has to run at a second or two to make ringing feel instant, and is multiplied by every connected
/// player. That is the first traffic shape in this module a misconfigured client can turn into a
/// flood without any player doing anything, and <c>docs/bridge.md</c> is explicit that an unlimited
/// endpoint is not permission to poll it without bound.
///
/// <b>The numbers.</b> A Bridge polls one phone per connected player, so a 60-player server at a
/// 1-second interval is about 60 requests a second sustained. 100/s leaves headroom above that
/// without being so loose that a client polling in a tight loop goes unnoticed, and the 300 reserve
/// covers the burst when a server fills up and every phone starts polling within the same few
/// seconds. Per <c>client_id</c>, so that is per gameserver — one server's bad behaviour stays its own
/// problem.
///
/// A deployment that needs ringing faster than a 1-second poll should raise
/// <c>TokensPerPeriod</c> here rather than let clients overrun the bucket, and should expect the
/// published 429 to be what tells it the setting is wrong.
/// </summary>
internal static class CallRateLimits
{
    internal static TokenBucketRateLimiterOptions RingPolls() => new()
    {
        TokenLimit = 300,
        TokensPerPeriod = 100,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
