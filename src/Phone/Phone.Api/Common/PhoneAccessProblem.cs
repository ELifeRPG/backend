using Microsoft.AspNetCore.Http;

namespace ELifeRPG.Phone.Api.Common;

/// <summary>
/// One mapping from the shared guard chain's verdict to an HTTP response, so every app's endpoints
/// spend their own switch only on what is actually specific to them.
/// </summary>
internal static class PhoneAccessProblem
{
    public static IResult ToResult(PhoneAccessResult denial) => denial switch
    {
        PhoneAccessResult.SimNotFound => Results.Problem(
            title: "SIM card not found", statusCode: StatusCodes.Status404NotFound),

        // 403 rather than 404: the caller named a SIM that exists, they simply do not own it.
        PhoneAccessResult.NotSimOwner => Results.Problem(
            title: "Character does not own this SIM card", statusCode: StatusCodes.Status403Forbidden),

        PhoneAccessResult.SimSuspended => Results.Problem(
            title: "SIM card is suspended", statusCode: StatusCodes.Status403Forbidden),

        PhoneAccessResult.SimDeactivated => Results.Problem(
            title: "SIM card has been deactivated", statusCode: StatusCodes.Status410Gone),

        PhoneAccessResult.SimNotInstalled => Results.Problem(
            title: "SIM card is not installed in a device", statusCode: StatusCodes.Status409Conflict),

        PhoneAccessResult.DeviceNotFound => Results.Problem(
            title: "Device not found", statusCode: StatusCodes.Status404NotFound),

        // The biolock: holding a handset is not the same as being bound to it.
        PhoneAccessResult.NotDeviceOwner => Results.Problem(
            title: "Character is not bound to this device", statusCode: StatusCodes.Status403Forbidden),

        PhoneAccessResult.DevicePoweredOff => Results.Problem(
            title: "Device is powered off", statusCode: StatusCodes.Status409Conflict),

        PhoneAccessResult.AppNotInstalled => Results.Problem(
            title: "App is not installed on this device", statusCode: StatusCodes.Status409Conflict),

        // A handset pointing at a model that no longer exists is a data fault, not a caller error.
        PhoneAccessResult.ModelNotFound => Results.Problem(
            title: "Device model not found", statusCode: StatusCodes.Status500InternalServerError),

        PhoneAccessResult.Granted => throw new InvalidOperationException("Granted is not a denial."),
    };
}
