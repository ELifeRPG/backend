using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Devices;

public union PhoneModelLookupResult(PhoneModelLookupResult.Found, PhoneModelLookupResult.NotFound)
{
    public record Found(PhoneModel Model);

    public record NotFound;
}

public sealed record PhoneModelLookupQuery(PhoneModelId ModelId) : IRequest<PhoneModelLookupResult>;

public sealed class PhoneModelLookupHandler(IPhoneModelRepository modelRepository)
    : IRequestHandler<PhoneModelLookupQuery, PhoneModelLookupResult>
{
    public async ValueTask<PhoneModelLookupResult> Handle(PhoneModelLookupQuery request, CancellationToken cancellationToken)
        => await modelRepository.FindByIdAsync(request.ModelId, cancellationToken) is { } model
            ? new PhoneModelLookupResult.Found(model)
            : new PhoneModelLookupResult.NotFound();
}

public sealed record PhoneModelsQuery : IRequest<IReadOnlyList<PhoneModel>>;

public sealed class PhoneModelsHandler(IPhoneModelRepository modelRepository)
    : IRequestHandler<PhoneModelsQuery, IReadOnlyList<PhoneModel>>
{
    public async ValueTask<IReadOnlyList<PhoneModel>> Handle(PhoneModelsQuery request, CancellationToken cancellationToken)
        => await modelRepository.FindAllAsync(cancellationToken);
}
