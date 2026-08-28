namespace ELifeRPG.World.Domain.Exceptions;

/// <summary>
/// Raised by <see cref="ELifeRPG.World.Domain.Items.ItemAttributes"/> when a caller supplies more
/// keys than <see cref="ELifeRPG.World.Domain.Items.ItemAttributes.MaxKeys"/> allows, a key/value
/// past its length cap, or a null value for a key (review round 3: System.Text.Json can put a JSON
/// <c>null</c> into a <c>Dictionary&lt;string, string&gt;</c> value despite the declared type having
/// no nullable annotation — this is the domain-level backstop for that, behind the endpoint's own
/// parse-layer rejection of the same input). The bag is validated on construction so a malformed
/// attribute set can never reach storage in the first place.
/// </summary>
public sealed class AttributeLimitExceededException(string message) : InvalidOperationException(message);
