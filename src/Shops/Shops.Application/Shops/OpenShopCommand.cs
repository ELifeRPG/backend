using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Companies.Application.Companies;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Domain.Events;

namespace ELifeRPG.Shops.Application.Shops;

public union OpenShopResult(OpenShopResult.Opened, OpenShopResult.CharacterNotFound, OpenShopResult.CompanyNotFound)
{
    public record Opened(ShopId ShopId);

    public record CharacterNotFound;

    public record CompanyNotFound;
}

public sealed record OpenShopCommand(
    ShopOwnerType OwnerType,
    CharacterId? OwnerCharacterId,
    CompanyId? OwnerCompanyId,
    string DisplayName,
    BankAccountId PayoutBankAccountId) : IRequest<OpenShopResult>;

public sealed class OpenShopHandler(IShopRepository shopRepository, IMediator mediator, ICurrentGameServer currentGameServer)
    : IRequestHandler<OpenShopCommand, OpenShopResult>
{
    public async ValueTask<OpenShopResult> Handle(OpenShopCommand request, CancellationToken cancellationToken)
    {
        if (request.OwnerType == ShopOwnerType.Personal)
        {
            var characterLookup = await mediator.Send(new CharacterLookupQuery(request.OwnerCharacterId!.Value), cancellationToken);
            if (characterLookup is CharacterLookupResult.NotFound)
            {
                return new OpenShopResult.CharacterNotFound();
            }
        }
        else
        {
            var companyLookup = await mediator.Send(new CompanyLookupQuery(request.OwnerCompanyId!.Value), cancellationToken);
            if (companyLookup is CompanyLookupResult.NotFound)
            {
                return new OpenShopResult.CompanyNotFound();
            }
        }

        var shopId = new ShopId(Guid.NewGuid());
        var serverId = await currentGameServer.GetIdAsync(cancellationToken);
        var domainEvent = new ShopOpened(shopId, request.OwnerType, request.OwnerCharacterId, request.OwnerCompanyId, request.DisplayName, request.PayoutBankAccountId, serverId);
        var shop = Shop.Create(domainEvent);

        shopRepository.StartStream(shop, domainEvent);
        await shopRepository.SaveChangesAsync(cancellationToken);

        return new OpenShopResult.Opened(shopId);
    }
}
