using System.Runtime.CompilerServices;

// Opens PhoneAccessPolicy to the test project, the same seam World.Api and Shops.Api open for their
// endpoint parsing, and for the same reason: narrower than a WebApplicationFactory harness this
// phase has consistently declined to build.
//
// What needs it is the two AuthorizeAsync overloads. They differ by exactly one question — "is the
// named app installed" — and the module's whole platform/app split rests on that difference being
// real. It used to be assertable from outside, by uninstalling Messages and reading the notification
// queue anyway. Messages is a system app now, so that no longer uninstalls anything, and every app
// that has routes is likewise permanent: nothing reachable through a public surface can tell the two
// chains apart any more. PhoneAccessResult.AppNotInstalled had no test at all until this opened.
[assembly: InternalsVisibleTo("ELifeRPG.Phone.IntegrationTests")]
