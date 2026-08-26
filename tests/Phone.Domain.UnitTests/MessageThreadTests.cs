using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Sims;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class MessageThreadTests
{
    private static readonly PhoneNumber Owner = PhoneNumber.Parse("44127788");
    private static readonly PhoneNumber Dispatcher = PhoneNumber.Parse("55009911");
    private static readonly PhoneNumber Mechanic = PhoneNumber.Parse("55009912");
    private static readonly DateTimeOffset At = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static PhoneModel Model(int threadMessageLimit = 30, int maxGroupParticipants = 5) =>
        PhoneModel.Create(PhoneModel.Define(
            new PhoneModelId(Guid.NewGuid()), "Burner", 1, null, 1,
            [AppKey.Messages, AppKey.Contacts], 50, threadMessageLimit, maxGroupParticipants));

    private static MessageThread Thread(PhoneModel? model = null, params PhoneNumber[] participants) =>
        MessageThread.Create(MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()),
            new SimCardId(Guid.NewGuid()),
            Owner,
            participants.Length == 0 ? [Dispatcher] : participants,
            model ?? Model()));

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
        var first = MessageThread.Start(new MessageThreadId(Guid.NewGuid()), new SimCardId(Guid.NewGuid()), Owner, [Mechanic, Dispatcher], Model());
        var second = MessageThread.Start(new MessageThreadId(Guid.NewGuid()), new SimCardId(Guid.NewGuid()), Owner, [Dispatcher, Mechanic], Model());

        Assert.Equal(first.ThreadKey, second.ThreadKey);
        Assert.Equal(first.Participants, second.Participants);
    }

    [Fact]
    public void Start_DeduplicatesParticipants()
    {
        var thread = Thread(Model(), Dispatcher, Dispatcher);

        Assert.Equal([Dispatcher], thread.Participants);
    }

    [Fact]
    public void Start_DropsTheOwnersOwnNumberFromTheParticipants()
    {
        // Addressing a group that includes yourself is normal; the thread is still "the others".
        var thread = Thread(Model(), Dispatcher, Owner);

        Assert.Equal([Dispatcher], thread.Participants);
    }

    [Fact]
    public void Start_WithNoParticipantsBesidesTheOwner_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()), new SimCardId(Guid.NewGuid()), Owner, [Owner], Model()));
    }

    [Fact]
    public void Start_BeyondTheModelGroupLimit_ThrowsArgumentOutOfRange()
    {
        var model = Model(maxGroupParticipants: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => MessageThread.Start(
            new MessageThreadId(Guid.NewGuid()), new SimCardId(Guid.NewGuid()), Owner,
            [Dispatcher, Mechanic, PhoneNumber.Parse("55009913")], model));
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

        thread.RecordOutbound(AMessage(), Owner, "on my way", At, Model());

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

        thread.RecordInbound(AMessage(), Dispatcher, "where are you", At, Model());
        thread.RecordInbound(AMessage(), Dispatcher, "hello?", At.AddMinutes(1), Model());

        Assert.Equal(2, thread.UnreadCount);
        Assert.False(thread.Messages[0].IsOutbound);
    }

    [Fact]
    public void MarkRead_ResetsUnread()
    {
        var thread = Thread();
        thread.RecordInbound(AMessage(), Dispatcher, "where are you", At, Model());

        thread.MarkRead();

        Assert.Equal(0, thread.UnreadCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordOutbound_WithBlankBody_ThrowsArgumentException(string body)
    {
        var thread = Thread();

        Assert.Throws<ArgumentException>(() => thread.RecordOutbound(AMessage(), Owner, body, At, Model()));
    }

    [Fact]
    public void Record_BeyondTheModelRetentionLimit_DropsTheOldest()
    {
        var model = Model(threadMessageLimit: 2);
        var thread = Thread(model);

        thread.RecordInbound(AMessage(), Dispatcher, "one", At, model);
        thread.RecordInbound(AMessage(), Dispatcher, "two", At.AddMinutes(1), model);
        thread.RecordInbound(AMessage(), Dispatcher, "three", At.AddMinutes(2), model);

        Assert.Equal(["two", "three"], thread.Messages.Select(message => message.Body));
    }

    [Fact]
    public void Record_AfterTheSimMovesToASmallerHandset_TrimsExistingHistory()
    {
        // The in-fiction consequence of history living on the SIM while the cap comes from the
        // handset: drop a smartphone SIM into a burner and the next message costs you the backlog.
        var smartphone = Model(threadMessageLimit: 30);
        var burner = Model(threadMessageLimit: 2);
        var thread = Thread(smartphone);
        thread.RecordInbound(AMessage(), Dispatcher, "one", At, smartphone);
        thread.RecordInbound(AMessage(), Dispatcher, "two", At.AddMinutes(1), smartphone);
        thread.RecordInbound(AMessage(), Dispatcher, "three", At.AddMinutes(2), smartphone);

        thread.RecordInbound(AMessage(), Dispatcher, "four", At.AddMinutes(3), burner);

        Assert.Equal(["three", "four"], thread.Messages.Select(message => message.Body));
    }

    [Fact]
    public void Apply_ReplayingUsesTheLimitRecordedOnTheEventNotTheCurrentModel()
    {
        // The retention limit is a fact about the moment of the append, so it rides on the event —
        // otherwise a replay after a model change would rebuild a different history.
        var threadId = new MessageThreadId(Guid.NewGuid());
        var thread = new MessageThread();

        thread.Apply(new MessageThreadStarted(threadId, new SimCardId(Guid.NewGuid()), [Dispatcher], MessageThread.BuildThreadKey([Dispatcher])));
        thread.Apply(new InboundMessageRecorded(threadId, AMessage(), Dispatcher, "one", At, 2));
        thread.Apply(new InboundMessageRecorded(threadId, AMessage(), Dispatcher, "two", At.AddMinutes(1), 2));
        thread.Apply(new InboundMessageRecorded(threadId, AMessage(), Dispatcher, "three", At.AddMinutes(2), 2));
        thread.Apply(new ThreadMarkedRead(threadId));

        Assert.Equal(["two", "three"], thread.Messages.Select(message => message.Body));
        Assert.Equal(0, thread.UnreadCount);
    }
}
