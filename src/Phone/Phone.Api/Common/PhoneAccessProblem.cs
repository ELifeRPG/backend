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
        PhoneAccessResult.PhoneNotFound => Results.Problem(
            title: "Phone not found", statusCode: StatusCodes.Status404NotFound),

        PhoneAccessResult.PhoneSuspended => Results.Problem(
            title: "Phone is suspended", statusCode: StatusCodes.Status403Forbidden),

        PhoneAccessResult.PhoneDeactivated => Results.Problem(
            title: "Phone has been deactivated", statusCode: StatusCodes.Status410Gone),

        PhoneAccessResult.PhonePoweredOff => Results.Problem(
            title: "Phone is powered off", statusCode: StatusCodes.Status409Conflict),

        PhoneAccessResult.AppNotInstalled => Results.Problem(
            title: "App is not installed on this phone", statusCode: StatusCodes.Status409Conflict),

        PhoneAccessResult.Granted => throw new InvalidOperationException("Granted is not a denial."),
    };
}
