using System.Text.Json.Serialization;
using ELifeRPG.Phone.Domain.Apps;
using ELifeRPG.Phone.Domain.Devices.Events;

namespace ELifeRPG.Phone.Domain.Devices;

/// <summary>
/// The staff-curated catalog of handsets, in the same spirit as <c>Item</c>. Tier is what makes a
/// model mean something: the capability numbers here are read by the apps — <see cref="ContactLimit"/>
/// by Contacts, <see cref="ThreadMessageLimit"/> and <see cref="MaxGroupParticipants"/> by Messages —
/// and <see cref="SupportedApps"/> is what stops a burner running what a smartphone runs.
/// </summary>
public class PhoneModel
{
    [JsonInclude]
    public PhoneModelId Id { get; private set; }

    [JsonInclude]
    public string DisplayName { get; private set; } = string.Empty;

    [JsonInclude]
    public int Tier { get; private set; }

    /// <summary>The Items catalog entry the bridge spawns in-game. Null for a model with no prefab yet.</summary>
    [JsonInclude]
    public ItemId? ItemId { get; private set; }

    [JsonInclude]
    public int SimSlots { get; private set; }

    [JsonInclude]
    public List<AppKey> SupportedApps { get; private set; } = [];

    [JsonInclude]
    public int ContactLimit { get; private set; }

    [JsonInclude]
    public int ThreadMessageLimit { get; private set; }

    [JsonInclude]
    public int MaxGroupParticipants { get; private set; }

    /// <summary>
    /// Validates then produces the creation event. Validation lives here rather than in
    /// <see cref="Create"/> so that replaying a historical stream can never throw — a model stored
    /// before a rule tightened must still load.
    /// </summary>
    public static PhoneModelCreated Define(
        PhoneModelId id,
        string displayName,
        int tier,
        ItemId? itemId,
        int simSlots,
        IReadOnlyList<AppKey> supportedApps,
        int contactLimit,
        int threadMessageLimit,
        int maxGroupParticipants)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        // A handset with no slot could never carry a number, so it could never do anything at all.
        if (simSlots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(simSlots), simSlots, "A model must have at least one SIM slot.");
        }

        if (contactLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(contactLimit), contactLimit, "Contact limit must be greater than zero.");
        }

        if (threadMessageLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threadMessageLimit), threadMessageLimit, "Thread message limit must be greater than zero.");
        }

        // Two is the floor because a "group" of one participant is just a 1:1 thread.
        if (maxGroupParticipants < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGroupParticipants), maxGroupParticipants, "A model must allow at least two group participants.");
        }

        if (supportedApps.Distinct().Count() != supportedApps.Count)
        {
            throw new ArgumentException("Supported apps must not contain duplicates.", nameof(supportedApps));
        }

        foreach (var key in supportedApps)
        {
            if (!AppCatalog.Contains(key))
            {
                throw new ArgumentException($"App '{key}' has no catalog entry.", nameof(supportedApps));
            }
        }

        return new PhoneModelCreated(id, displayName, tier, itemId, simSlots, supportedApps, contactLimit, threadMessageLimit, maxGroupParticipants);
    }

    public static PhoneModel Create(PhoneModelCreated domainEvent)
    {
        var model = new PhoneModel();
        model.Apply(domainEvent);
        return model;
    }

    public bool Supports(AppKey key) => SupportedApps.Contains(key);

    public void Apply(PhoneModelCreated domainEvent)
    {
        Id = domainEvent.Id;
        DisplayName = domainEvent.DisplayName;
        Tier = domainEvent.Tier;
        ItemId = domainEvent.ItemId;
        SimSlots = domainEvent.SimSlots;
        SupportedApps = [.. domainEvent.SupportedApps];
        ContactLimit = domainEvent.ContactLimit;
        ThreadMessageLimit = domainEvent.ThreadMessageLimit;
        MaxGroupParticipants = domainEvent.MaxGroupParticipants;
    }
}
