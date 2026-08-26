using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Sims.Events;

namespace ELifeRPG.Phone.Application.Sims;

public union ProvisionSimCardResult(
    ProvisionSimCardResult.Provisioned,
    ProvisionSimCardResult.NumberExhausted)
{
    public record Provisioned(SimCardId SimCardId, PhoneNumber Number);

    /// <summary>Every generated candidate collided. Practically unreachable; surfaced rather than swallowed.</summary>
    public record NumberExhausted;
}

/// <summary>
/// TODO(inventory): like devices, SIMs exist only through this endpoint until Reforger inventory can
/// persist composed items — see ProvisionPhoneDeviceCommand for the full note. A SIM is the property
/// a phone item would carry, so the two land together.
/// </summary>
public sealed record ProvisionSimCardCommand(CharacterId RegisteredTo) : IRequest<ProvisionSimCardResult>;

public sealed class ProvisionSimCardHandler(ISimCardRepository simCardRepository)
    : IRequestHandler<ProvisionSimCardCommand, ProvisionSimCardResult>
{
    // Generous enough that exhausting it means something is genuinely wrong, not merely unlucky:
    // with 90 million numbers, five straight collisions is not a scenario a live hive reaches.
    private const int MaxAttempts = 5;

    public async ValueTask<ProvisionSimCardResult> Handle(ProvisionSimCardCommand request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var simCardId = new SimCardId(Guid.NewGuid());
            var number = PhoneNumberGenerator.Generate();
            var issued = new SimCardIssued(simCardId, number, request.RegisteredTo);

            simCardRepository.StartStream(SimCard.Create(issued), issued);

            try
            {
                await simCardRepository.SaveChangesAsync(cancellationToken);
                return new ProvisionSimCardResult.Provisioned(simCardId, number);
            }
            catch (PhoneNumberTakenException)
            {
                // The unique index on the number arbitrates, rather than a pre-check two concurrent
                // issues could both pass — same approach as bank account numbers.
            }
        }

        return new ProvisionSimCardResult.NumberExhausted();
    }
}
