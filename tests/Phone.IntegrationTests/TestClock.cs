namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// A clock the test moves by hand. The module injects <see cref="TimeProvider"/> rather than calling
/// <c>DateTimeOffset.UtcNow</c> precisely so a window can be crossed without waiting for it — the
/// send throttle needed that first, and a call's ring window needs it more: at the real default a
/// test would sit for thirty seconds to watch one ring expire.
/// </summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
