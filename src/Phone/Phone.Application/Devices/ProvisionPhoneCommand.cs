using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Devices.Events;
using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Application.Devices;

public union ProvisionPhoneResult(
    ProvisionPhoneResult.Provisioned,
    ProvisionPhoneResult.InvalidPin,
    ProvisionPhoneResult.NumberExhausted)
{
    public record Provisioned(PhoneDeviceId PhoneId, PhoneNumber Number);

    public record InvalidPin(string Reason);

    /// <summary>Every generated candidate collided. Practically unreachable; surfaced rather than swallowed.</summary>
    public record NumberExhausted;
}

/// <summary>
/// Creates a phone: a handset, its number and its PIN in one act. There is no separate SIM to issue
/// and seat, so a character who wants two numbers provisions two phones.
///
/// The PIN is what a character other than <paramref name="RegisteredTo"/> needs in order to use it.
/// The owner never supplies one — see <c>PhoneActor</c>.
///
/// TODO(inventory): this is the only way a phone comes into existence today, because eliferpg-core
/// has no per-character inventory to hand one over through. Shops can sell a phone Item, but nothing
/// connects that purchase to a device record, so provisioning is driven by the bridge under
/// `phone:provision` instead.
///
/// Closing that gap needs Reforger inventory persistence for *composed* items: an item instance
/// carrying a property that references the PhoneDeviceId it is. Once an inventory item can hold
/// that, provisioning moves into the purchase flow (see PurchaseListingHandler's
/// ICrossModuleTransaction) and buying, dropping, looting and trading a phone all become inventory
/// operations rather than separate API calls. That is also the point at which the PIN starts to
/// matter in earnest: a looted handset is usable by whoever knows it.
/// </summary>
public sealed record ProvisionPhoneCommand(CharacterId RegisteredTo, string Pin) : IRequest<ProvisionPhoneResult>;

public sealed class ProvisionPhoneHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<ProvisionPhoneCommand, ProvisionPhoneResult>
{
    // Generous enough that exhausting it means something is genuinely wrong, not merely unlucky:
    // with 90 million numbers, five straight collisions is not a scenario a live hive reaches.
    private const int MaxAttempts = 5;

    public async ValueTask<ProvisionPhoneResult> Handle(ProvisionPhoneCommand request, CancellationToken cancellationToken)
    {
        string pin;
        try
        {
            pin = PhoneDevice.EnsurePin(request.Pin);
        }
        catch (InvalidPhonePinException exception)
        {
            return new ProvisionPhoneResult.InvalidPin(exception.Message);
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var phoneId = new PhoneDeviceId(Guid.NewGuid());
            var number = PhoneNumberGenerator.Generate();
            var provisioned = new PhoneDeviceProvisioned(phoneId, number, pin, request.RegisteredTo);
            var phone = PhoneDevice.Create(provisioned);

            phoneRepository.StartStream(phone, provisioned);

            // A phone ships with every app in the catalog installed, the way a real one arrives with
            // its stock software. There is no model to ask what it supports any more, so uninstalling
            // is a deliberate act rather than a required setup step.
            foreach (var appKey in AppCatalog.Entries.Keys)
            {
                phoneRepository.Append(phoneId, phone.InstallApp(appKey));
            }

            try
            {
                await phoneRepository.SaveChangesAsync(cancellationToken);
                return new ProvisionPhoneResult.Provisioned(phoneId, number);
            }
            catch (PhoneNumberTakenException)
            {
                // The unique index on the number arbitrates, rather than a pre-check two concurrent
                // provisions could both pass — same approach as bank account numbers.
            }
        }

        return new ProvisionPhoneResult.NumberExhausted();
    }
}
