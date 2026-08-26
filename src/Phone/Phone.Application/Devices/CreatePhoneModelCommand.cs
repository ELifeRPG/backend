using ELifeRPG.Phone.Application.Common;
using ELifeRPG.Phone.Domain.Devices.Events;

namespace ELifeRPG.Phone.Application.Devices;

public union CreatePhoneModelResult(CreatePhoneModelResult.Created, CreatePhoneModelResult.InvalidDefinition)
{
    public record Created(PhoneModelId ModelId);

    public record InvalidDefinition(string Reason);
}

public sealed record CreatePhoneModelCommand(
    string DisplayName,
    int Tier,
    ItemId? ItemId,
    int SimSlots,
    IReadOnlyList<AppKey> SupportedApps,
    int ContactLimit,
    int ThreadMessageLimit,
    int MaxGroupParticipants) : IRequest<CreatePhoneModelResult>;

public sealed class CreatePhoneModelHandler(IPhoneModelRepository modelRepository)
    : IRequestHandler<CreatePhoneModelCommand, CreatePhoneModelResult>
{
    public async ValueTask<CreatePhoneModelResult> Handle(CreatePhoneModelCommand request, CancellationToken cancellationToken)
    {
        var modelId = new PhoneModelId(Guid.NewGuid());

        PhoneModelCreated domainEvent;
        try
        {
            // PhoneModel.Define owns the rules; catching here turns them into a 400 rather than a 500.
            domainEvent = PhoneModel.Define(
                modelId,
                request.DisplayName,
                request.Tier,
                request.ItemId,
                request.SimSlots,
                request.SupportedApps,
                request.ContactLimit,
                request.ThreadMessageLimit,
                request.MaxGroupParticipants);
        }
        catch (ArgumentException exception)
        {
            return new CreatePhoneModelResult.InvalidDefinition(exception.Message);
        }

        modelRepository.StartStream(PhoneModel.Create(domainEvent), domainEvent);
        await modelRepository.SaveChangesAsync(cancellationToken);

        return new CreatePhoneModelResult.Created(modelId);
    }
}
