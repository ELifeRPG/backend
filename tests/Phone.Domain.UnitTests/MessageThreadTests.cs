using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class MessageThreadTests
{
    private static readonly PhoneNumber Owner = PhoneNumber.Parse("44127788");
    private static readonly PhoneNumber Dispatcher = PhoneNumber.Parse("55009911");
    private static readonly PhoneNumber Mechanic = PhoneNumber.Parse("55009912");
    private static readonly DateTimeOffset At = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private const int RetentionLimit = 30;
    private const int MaxGroupParticipants = 5;

    private static MessageThread Thread(int maxGroupParticipants = MaxGroupParticipants, params PhoneNumber[] participants) =>
        MessageThread.Create(MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()),
            new PhoneDeviceId(Guid.NewGuid()),
            Owner,
            participants.Length == 0 ? [Dispatcher] : participants,
            maxGroupParticipants));

    private static MessageId AMessage() => new(Guid.NewGuid());

    [Fact]
    public void Start_RecordsTheParticipantsAndAKey()
    {
        var thread = Thread();

        Assert.Equal([Dispatcher], thread.Participants);
        Assert.False(string.IsNullOrWhiteSpace(thread.ThreadKey));
        Assert.Empty(thread.Messages);
        Assert.Equal(0, thread.UnreadCount);
    }

    [Fact]
    public void Start_SortsParticipantsSoTheKeyDoesNotDependOnSendOrder()
    {
        // Two sends naming the same people in different orders must land in one thread, not two.
        var first = MessageThread.Start(new MessageThreadId(Guid.NewGuid()), new PhoneDeviceId(Guid.NewGuid()), Owner, [Mechanic, Dispatcher], MaxGroupParticipants);
        var second = MessageThread.Start(new MessageThreadId(Guid.NewGuid()), new PhoneDeviceId(Guid.NewGuid()), Owner, [Dispatcher, Mechanic], MaxGroupParticipants);

        Assert.Equal(first.ThreadKey, second.ThreadKey);
        Assert.Equal(first.Participants, second.Participants);
    }

    [Fact]
    public void Start_DeduplicatesParticipants()
    {
        var thread = Thread(MaxGroupParticipants, Dispatcher, Dispatcher);

        Assert.Equal([Dispatcher], thread.Participants);
    }

    [Fact]
    public void Start_DropsTheOwnersOwnNumberFromTheParticipants()
    {
        // Addressing a group that includes yourself is normal; the thread is still "the others".
        var thread = Thread(MaxGroupParticipants, Dispatcher, Owner);

        Assert.Equal([Dispatcher], thread.Participants);
    }

    [Fact]
    public void Start_WithNoParticipantsBesidesTheOwner_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()), new PhoneDeviceId(Guid.NewGuid()), Owner, [Owner], MaxGroupParticipants));
    }

    [Fact]
    public void Start_BeyondTheGroupLimit_ThrowsArgumentOutOfRange()
    {


        Assert.Throws<ArgumentOutOfRangeException>(() => MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()), new PhoneDeviceId(Guid.NewGuid()), Owner,
            [Dispatcher, Mechanic, PhoneNumber.Parse("55009913")], maxGroupParticipants: 2));
    }

    [Fact]
    public void BuildThreadKey_IsOrderAndFormattingIndependent()
    {
        Assert.Equal(
            MessageThread.BuildThreadKey([PhoneNumber.Parse("5500-9912"), PhoneNumber.Parse("55009911")]),
            MessageThread.BuildThreadKey([PhoneNumber.Parse("+55009911"), PhoneNumber.Parse("55009912")]));
    }

    [Fact]
    public void RecordOutbound_AppendsAnOutboundMessageAndLeavesUnreadAlone()
    {
        var thread = Thread();

        thread.RecordOutbound(AMessage(), Owner, "on my way", At, RetentionLimit);

        var message = Assert.Single(thread.Messages);
        Assert.True(message.IsOutbound);
        Assert.Equal("on my way", message.Body);
        Assert.Equal(0, thread.UnreadCount);
        Assert.Equal(At, thread.LastMessageAt);
    }

    [Fact]
    public void RecordInbound_AppendsAndIncrementsUnread()
    {
        var thread = Thread();

        thread.RecordInbound(AMessage(), Dispatcher, "where are you", At, RetentionLimit);
        thread.RecordInbound(AMessage(), Dispatcher, "hello?", At.AddMinutes(1), RetentionLimit);

        Assert.Equal(2, thread.UnreadCount);
        Assert.False(thread.Messages[0].IsOutbound);
    }

    [Fact]
    public void MarkRead_ResetsUnread()
    {
        var thread = Thread();
        thread.RecordInbound(AMessage(), Dispatcher, "where are you", At, RetentionLimit);

        thread.MarkRead();

        Assert.Equal(0, thread.UnreadCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordOutbound_WithBlankBody_ThrowsArgumentException(string body)
    {
        var thread = Thread();

        Assert.Throws<ArgumentException>(() => thread.RecordOutbound(AMessage(), Owner, body, At, RetentionLimit));
    }

    [Fact]
    public void Record_BeyondTheRetentionLimit_DropsTheOldest()
    {
        const int limit = 2;
        var thread = Thread();

        thread.RecordInbound(AMessage(), Dispatcher, "one", At, limit);
        thread.RecordInbound(AMessage(), Dispatcher, "two", At.AddMinutes(1), limit);
        thread.RecordInbound(AMessage(), Dispatcher, "three", At.AddMinutes(2), limit);

        Assert.Equal(["two", "three"], thread.Messages.Select(message => message.Body));
    }

    [Fact]
    public void Record_AfterTheRetentionLimitIsLowered_TrimsExistingHistory()
    {
        // Trimming happens on append, against whatever the cap is at that moment. Lowering
        // HiveSettings.PhoneThreadMessageLimit therefore costs every thread its backlog on its next
        // message rather than immediately — which is the behaviour to keep in mind before editing it.
        var thread = Thread();
        thread.RecordInbound(AMessage(), Dispatcher, "one", At, 30);
        thread.RecordInbound(AMessage(), Dispatcher, "two", At.AddMinutes(1), 30);
        thread.RecordInbound(AMessage(), Dispatcher, "three", At.AddMinutes(2), 30);

        thread.RecordInbound(AMessage(), Dispatcher, "four", At.AddMinutes(3), 2);

        Assert.Equal(["three", "four"], thread.Messages.Select(message => message.Body));
    }

    [Fact]
    public void Apply_ReplayingUsesTheLimitRecordedOnTheEventNotTheCurrentSetting()
    {
        // The retention limit is a fact about the moment of the append, so it rides on the event —
        // otherwise a replay after a settings change would rebuild a different history.
        var threadId = new MessageThreadId(Guid.NewGuid());
        var thread = new MessageThread();

        thread.Apply(new MessageThreadStarted(threadId, new PhoneDeviceId(Guid.NewGuid()), [Dispatcher], MessageThread.BuildThreadKey([Dispatcher])));
        thread.Apply(new InboundMessageRecorded(threadId, AMessage(), Dispatcher, "one", At, 2));
        thread.Apply(new InboundMessageRecorded(threadId, AMessage(), Dispatcher, "two", At.AddMinutes(1), 2));
        thread.Apply(new InboundMessageRecorded(threadId, AMessage(), Dispatcher, "three", At.AddMinutes(2), 2));
        thread.Apply(new ThreadMarkedRead(threadId));

        Assert.Equal(["two", "three"], thread.Messages.Select(message => message.Body));
        Assert.Equal(0, thread.UnreadCount);
    }
}
