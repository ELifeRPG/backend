using ELifeRPG.Phone.Domain.Sims;

namespace ELifeRPG.Phone.Domain.Apps.Messages;

/// <summary>
/// A message that could not be appended yet because the recipient's SIM was loose or its device was
/// powered off. Flushed into the thread when the SIM is installed or the device powers on.
///
/// A plain mutable document, not an event: this is transport state in flight, and once flushed the
/// thread's own stream is the record. Note that a *suspended* SIM never queues — enforcement means
/// the message is dropped, not held.
/// </summary>
public class PendingDelivery
{
    public Guid Id { get; set; }

    public SimCardId RecipientSimCardId { get; set; }

    public MessageId MessageId { get; set; }

    public PhoneNumber From { get; set; }

    public List<PhoneNumber> Participants { get; set; } = [];

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset SentAt { get; set; }
}
