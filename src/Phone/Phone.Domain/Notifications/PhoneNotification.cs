using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Domain.Notifications;

/// <summary>
/// A platform-level, app-independent banner — the APNs/FCM idea grafted onto a phone that has no OS
/// of its own. Rooted outside <c>Apps/</c> on purpose: an app publishes into this queue, but the
/// queue is not itself an app, and every phone has one regardless of what is installed.
///
/// A plain mutable document, not an event: like <see cref="Apps.Messages.PendingDelivery"/>, this is
/// delivery state rather than history worth replaying. It is also **immutable once written** —
/// nothing here is ever updated in place, only stored or deleted whole — which is what lets a client
/// ack a batch of ids with no version field to race.
///
/// <see cref="Category"/> is a free-form string rather than an enum: <see cref="AppKey"/> already
/// says which app, and a within-app kind ("message.received" today, perhaps "delivery.failed"
/// later) does not need append-only enum discipline the way a cross-module identity does.
///
/// <see cref="GroupKey"/> is the iOS "thread-id" analogue: it groups banners for client-side
/// rendering ("3 from 0155 12345678") without collapsing them into one row, so ack-by-id stays exact
/// even when several notifications share a group. For Messages it is the recipient's own
/// <see cref="Apps.Messages.MessageThreadId"/>, rendered as a string because the queue is meant to
/// stay blind to what any particular app's reference type is.
///
/// <see cref="Payload"/> is a plain string dictionary for the same reason: no polymorphism, no
/// <c>object</c>-typed member that would round-trip as a bare <c>JsonElement</c>, and it stays
/// readable in the stored JSONB when debugging. Named <c>Payload</c> rather than <c>Ref</c> so it
/// does not sit next to OpenAPI's own <c>$ref</c> in a generated client.
/// </summary>
public class PhoneNotification
{
    public Guid Id { get; set; }

    public PhoneDeviceId PhoneId { get; set; }

    /// <summary>
    /// A plain duplicate of <see cref="PhoneId"/>'s underlying <c>Guid</c>, kept for the same reason
    /// <c>PhoneDevice.NumberValue</c> duplicates <c>PhoneNumber.Value</c>: Marten can neither index
    /// nor efficiently filter a query against a strongly-typed id wrapper, only against a plain
    /// scalar. This is the phone-scope every read and the unique index below run against — a queue
    /// polled by every phone on every tick cannot afford to be the one document type in this module
    /// without one. Set only by <see cref="Create"/>, so it can never drift from <see cref="PhoneId"/>.
    /// </summary>
    public Guid PhoneIdValue { get; set; }

    public AppKey AppKey { get; set; }

    public string Category { get; set; } = string.Empty;

    public string GroupKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public Dictionary<string, string> Payload { get; set; } = [];

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// The only supported way to build one — so <see cref="PhoneId"/> and <see cref="PhoneIdValue"/>
    /// are never set independently and cannot disagree.
    /// </summary>
    public static PhoneNotification Create(
        PhoneDeviceId phoneId,
        AppKey appKey,
        string category,
        string groupKey,
        string title,
        string body,
        Dictionary<string, string> payload,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            PhoneId = phoneId,
            PhoneIdValue = phoneId.Value,
            AppKey = appKey,
            Category = category,
            GroupKey = groupKey,
            Title = title,
            Body = body,
            Payload = payload,
            OccurredAt = occurredAt,
        };

    /// <summary>
    /// Fits a phone's notification queue into <paramref name="limit"/> slots, oldest dropped first —
    /// the same "we do not accumulate a backlog" property APNs has. A pure function so the cap is
    /// exercised by a domain unit test even though CI runs no integration tests: the caller passes
    /// what is already stored (<paramref name="existing"/>, oldest first) and what a single commit is
    /// about to add (<paramref name="incoming"/>, oldest first), and gets back exactly what should be
    /// stored and exactly what should be deleted to land at <paramref name="limit"/> or fewer.
    ///
    /// If <paramref name="incoming"/> alone exceeds the limit, the oldest of the incoming batch is
    /// dropped too — a single flush posting more than the cap must not silently grow the queue past
    /// it just because nothing existing needed evicting.
    /// </summary>
    public static (IReadOnlyList<PhoneNotification> ToStore, IReadOnlyList<Guid> ToDelete) Fit(
        IReadOnlyList<PhoneNotification> existing,
        IReadOnlyList<PhoneNotification> incoming,
        int limit)
    {
        if (incoming.Count >= limit)
        {
            // The batch alone fills or overflows the queue: nothing existing survives, and only the
            // newest `limit` of the batch are kept.
            return ([.. incoming.TakeLast(limit)], [.. existing.Select(n => n.Id)]);
        }

        var room = limit - incoming.Count;
        var survivingExisting = existing.TakeLast(room).ToList();
        var evicted = existing.Except(survivingExisting).Select(n => n.Id).ToList();

        return ([.. survivingExisting, .. incoming], evicted);
    }
}
