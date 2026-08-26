using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Apps.Messages;

public sealed record Message(MessageId Id, PhoneNumber From, string Body, DateTimeOffset SentAt, bool IsOutbound);
