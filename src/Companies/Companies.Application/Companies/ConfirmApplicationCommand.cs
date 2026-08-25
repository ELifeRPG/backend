using ELifeRPG.Companies.Application.Common;
using ELifeRPG.Companies.Domain.Exceptions;

namespace ELifeRPG.Companies.Application.Companies;

public union ConfirmApplicationResult(
    ConfirmApplicationResult.Confirmed,
    ConfirmApplicationResult.CompanyNotFound,
    ConfirmApplicationResult.NotAuthorized,
    ConfirmApplicationResult.ApplicationNotFound,
    ConfirmApplicationResult.InvalidState,
    ConfirmApplicationResult.ConcurrentModification)
{
    public record Confirmed;

    public record CompanyNotFound;

    public record NotAuthorized;

    public record ApplicationNotFound;

    public record InvalidState;

    public record ConcurrentModification;
}

public sealed record ConfirmApplicationCommand(CompanyId CompanyId, CompanyApplicationId ApplicationId, CharacterId ActingCharacterId)
    : IRequest<ConfirmApplicationResult>;

public sealed class ConfirmApplicationHandler(ICompanyRepository companyRepository)
    : IRequestHandler<ConfirmApplicationCommand, ConfirmApplicationResult>
{
    public async ValueTask<ConfirmApplicationResult> Handle(ConfirmApplicationCommand request, CancellationToken cancellationToken)
    {
        var company = await companyRepository.FetchForUpdateAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return new ConfirmApplicationResult.CompanyNotFound();
        }

        if (!CompanyMemberAuthorization.CanManageMembers(company, request.ActingCharacterId))
        {
            return new ConfirmApplicationResult.NotAuthorized();
        }

        try
        {
            var domainEvent = company.ConfirmApplication(request.ApplicationId);
            companyRepository.Append(request.CompanyId, domainEvent);
        }
        catch (ApplicationNotFoundException)
        {
            return new ConfirmApplicationResult.ApplicationNotFound();
        }
        catch (InvalidApplicationStateException)
        {
            return new ConfirmApplicationResult.InvalidState();
        }

        try
        {
            await companyRepository.SaveChangesAsync(cancellationToken);
        }
        catch (CompanyConcurrencyException)
        {
            return new ConfirmApplicationResult.ConcurrentModification();
        }

        return new ConfirmApplicationResult.Confirmed();
    }
}
