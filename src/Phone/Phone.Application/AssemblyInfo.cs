using System.Runtime.CompilerServices;

// Opens PhoneAccessPolicy to the test project, the seam World.Api and Shops.Api open for the same
// reason. Its two AuthorizeAsync overloads differ by one question — "is the named app installed" —
// and with every routed app now a system app, nothing public can tell the two chains apart.
[assembly: InternalsVisibleTo("ELifeRPG.Phone.IntegrationTests")]
