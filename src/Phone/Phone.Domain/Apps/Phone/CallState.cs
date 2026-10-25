namespace ELifeRPG.Phone.Domain.Apps.Phone;

/// <summary>
/// Append only — ordinals are persisted in Marten event/document payloads (no
/// JsonStringEnumConverter configured), so inserting a member mid-list remaps every stored value.
///
/// Nothing sits at 0, for the reason <see cref="AppKey"/> gives: a payload missing the field would
/// otherwise name whichever state sat there, and there is no state that is a safe answer to "what
/// happened to this call" when the real answer is "the field was absent". An undefined value fails
/// <see cref="Enum.IsDefined{TEnum}"/> instead.
///
/// <see cref="Ringing"/> and <see cref="Accepted"/> are the non-terminal pair; the other three are
/// final, and <see cref="PhoneCall"/> refuses every transition out of them.
/// </summary>
public enum CallState
{
    Ringing = 1,
    Accepted = 2,
    Rejected = 3,
    Missed = 4,
    Ended = 5,
}
