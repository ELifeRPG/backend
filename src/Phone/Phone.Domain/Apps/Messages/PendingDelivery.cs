using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Domain.Apps.Messages;

/// <summary>
/// A message that could not be appended yet because the recipient's phone was powered off or had
/// Messages uninstalled. Flushed into the thread when the phone powers on or the app is installed.
///
/// A plain mutable document, not an event: this is transport state in flight, and once flushed the
/// thread's own stream is the record. Note that a *suspended* phone never queues — enforcement means
/// the message is dropped, not held.
/// </summary>
public class PendingDelivery
{
    public Guid Id { get; set; }

    public PhoneDeviceId RecipientPhoneId { get; set; }

    public MessageId MessageId { get; set; }

    public PhoneNumber From { get; set; }

    public List<PhoneNumber> Participants { get; set; } = [];

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset SentAt { get; set; }
}
