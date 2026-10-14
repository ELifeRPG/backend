using ELifeRPG.Phone.Application.Common;

namespace ELifeRPG.Phone.Application.Apps.Store;

/// <summary>
/// A catalog entry as the store presents it: what it is, plus whether this phone already has it.
/// The install state is the phone's, not the catalog's, which is why the store is a per-phone read
/// rather than a static list.
/// </summary>
public sealed record StoreApp(AppDefinition Definition, bool IsInstalled);

public union StoreAppsResult(StoreAppsResult.Apps, StoreAppsResult.AccessDenied)
{
    public record Apps(IReadOnlyList<StoreApp> Entries);

    public record AccessDenied(PhoneAccessResult Reason);
}

/// <summary>
/// What the store offers, which is every app that is not the phone's own software — a system app
/// can neither be installed nor removed from here, so listing one would only be an entry with no
/// action attached.
///
/// Runs the ordinary app guard chain against <see cref="AppKey.Store"/>: browsing the store happens
/// on the handset, so the phone has to be powered on and the store itself installed.
/// </summary>
public sealed record StoreAppsQuery(PhoneDeviceId PhoneId) : IRequest<StoreAppsResult>;

public sealed class StoreAppsHandler(IPhoneDeviceRepository phoneRepository)
    : IRequestHandler<StoreAppsQuery, StoreAppsResult>
{
    public async ValueTask<StoreAppsResult> Handle(StoreAppsQuery request, CancellationToken cancellationToken)
    {
        var access = await PhoneAccessPolicy.AuthorizeAsync(
            request.PhoneId, AppKey.Store, phoneRepository, cancellationToken);

        if (access is not PhoneAccessResult.Granted granted)
        {
            return new StoreAppsResult.AccessDenied(access);
        }

        return new StoreAppsResult.Apps(
            [.. AppCatalog.StoreApps.Select(definition =>
                new StoreApp(definition, granted.Phone.HasApp(definition.Key)))]);
    }
}
