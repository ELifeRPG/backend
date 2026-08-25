using System.Text.Json.Serialization;
using ELifeRPG.Shops.Domain.Events;

namespace ELifeRPG.Shops.Domain;

public class Shop
{
    [JsonInclude]
    public ShopId Id { get; private set; }

    [JsonInclude]
    public ShopOwnerType OwnerType { get; private set; }

    [JsonInclude]
    public CharacterId? OwnerCharacterId { get; private set; }

    [JsonInclude]
    public CompanyId? OwnerCompanyId { get; private set; }

    [JsonInclude]
    public string DisplayName { get; private set; } = string.Empty;

    [JsonInclude]
    public BankAccountId PayoutBankAccountId { get; private set; }

    /// <summary>
    /// Which server (map) this shop stands on. A shop is a building in the world, so placement is a
    /// fact about it — not an isolation boundary. Its owner and payout account are hive-wide.
    ///
    /// This field was added to <c>ShopOpened</c> on 2026-08-22. Events written before that date have
    /// no corresponding JSON property. The steady-state read path (Marten
    /// <c>ProjectionLifecycle.Inline</c> + <c>LoadAsync&lt;Shop&gt;</c>) never replays those raw
    /// events — it reads the already-materialised snapshot document — so this is silently harmless
    /// today. But System.Text.Json binds a missing constructor argument to its default rather than
    /// throwing, so anything that genuinely replays a pre-migration stream (a projection rebuild,
    /// <c>AggregateStreamAsync</c>, async-daemon catch-up, restore-from-events) will silently produce
    /// <c>default(GameServerId)</c> (<c>Guid.Empty</c>) for it — no exception, no warning. Treat
    /// <c>ServerId</c> on any shop replayed from pre-2026-08-22 events as unset, not trustworthy.
    /// </summary>
    [JsonInclude]
    public GameServerId ServerId { get; private set; }

    public static Shop Create(ShopOpened domainEvent)
    {
        var shop = new Shop();
        shop.Apply(domainEvent);
        return shop;
    }

    public void Apply(ShopOpened domainEvent)
    {
        Id = domainEvent.Id;
        OwnerType = domainEvent.OwnerType;
        OwnerCharacterId = domainEvent.OwnerCharacterId;
        OwnerCompanyId = domainEvent.OwnerCompanyId;
        DisplayName = domainEvent.DisplayName;
        PayoutBankAccountId = domainEvent.PayoutBankAccountId;
        ServerId = domainEvent.ServerId;
    }
}
