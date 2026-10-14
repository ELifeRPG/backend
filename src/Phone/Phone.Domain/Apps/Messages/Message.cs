using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Domain.Apps.Messages;

/// <summary>
/// <paramref name="Sequence"/> is a per-thread, ever-increasing counter — not this message's index in
/// <see cref="MessageThread.Messages"/>. Retention trims the front of that list, so an index would be
/// reused by whatever slides into the gap; a counter that only ever grows is what lets
/// <see cref="MessageThread.RetrievedThrough"/> mean the same thing before and after a trim.
/// </summary>
public sealed record Message(MessageId Id, PhoneNumber From, string Body, DateTimeOffset SentAt, bool IsOutbound, int Sequence);
