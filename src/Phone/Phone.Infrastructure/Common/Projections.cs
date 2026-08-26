using ELifeRPG.Phone.Domain.Apps.Contacts;
using ELifeRPG.Phone.Domain.Apps.Contacts.Events;
using ELifeRPG.Phone.Domain.Apps.Messages;
using ELifeRPG.Phone.Domain.Apps.Messages.Events;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Sims;
using ELifeRPG.Phone.Domain.Sims.Events;
using Marten.Events.Aggregation;

namespace ELifeRPG.Phone.Infrastructure.Common;

/// <summary>
/// All five projections are inline. Note the standing warning from AccountProjection: an inline
/// projection silently ignores events it has no Apply for, leaving a stale document rather than
/// failing — so every new event must be applied on its aggregate.
/// </summary>
public sealed partial class PhoneModelProjection : SingleStreamProjection<PhoneModel, PhoneModelId>
{
    public static PhoneModel Create(PhoneModelCreated domainEvent) => PhoneModel.Create(domainEvent);
}

public sealed partial class PhoneDeviceProjection : SingleStreamProjection<PhoneDevice, PhoneDeviceId>
{
    public static PhoneDevice Create(PhoneDeviceProvisioned domainEvent) => PhoneDevice.Create(domainEvent);
}

public sealed partial class SimCardProjection : SingleStreamProjection<SimCard, SimCardId>
{
    public static SimCard Create(SimCardIssued domainEvent) => SimCard.Create(domainEvent);
}

public sealed partial class ContactBookProjection : SingleStreamProjection<ContactBook, ContactBookId>
{
    public static ContactBook Create(ContactBookOpened domainEvent) => ContactBook.Create(domainEvent);
}

public sealed partial class MessageThreadProjection : SingleStreamProjection<MessageThread, MessageThreadId>
{
    public static MessageThread Create(MessageThreadStarted domainEvent) => MessageThread.Create(domainEvent);
}
