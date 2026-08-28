namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised when <c>PATCH /api/inventory/limits</c> names a value outside the range
/// <see cref="ELifeRPG.World.Domain.WorldSettings"/>' own bounds table allows for that knob.
///
/// A named type rather than the framework's <c>ArgumentOutOfRangeException</c> that the
/// <c>HiveSettings</c> precedent throws, and that is a deliberate deviation: this module's contract is
/// that every rejection on its write surface is an RFC 9457 problem document carrying a
/// <c>retryable</c> flag (see docs/bridge.md and <c>WorldModule.NotRetryableExtensions</c>), and an
/// unhandled framework exception is a 500 with neither. Endpoint-level catch-and-map on a named
/// exception is ARCHITECTURE.md §9e's convention for exactly this shape — a domain guard representing
/// an outcome the caller can reasonably trigger — and it lets the rejection name the offending knob and
/// its allowed range, which is what a staff operator needs to correct the request.
/// </summary>
public sealed class WorldSettingOutOfRangeException(string setting, int value, int min, int max)
    : InvalidOperationException($"{setting} must be between {min} and {max}; {value} was supplied.")
{
    public string Setting { get; } = setting;
}
