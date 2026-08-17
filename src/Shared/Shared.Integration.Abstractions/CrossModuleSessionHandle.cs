using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ELifeRPG.Shared.Integration")]

namespace ELifeRPG.Shared.Integration.Abstractions;

/// <summary>
/// Opaque handle to a shared cross-module database transaction. Application-layer code only ever
/// passes this through — the underlying transaction is reachable only via Shared.Integration's
/// public CrossModuleSessionHandleExtensions.Unwrap(), used exclusively by each participating
/// module's Infrastructure-layer repository factory. See
/// docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public sealed class CrossModuleSessionHandle
{
    internal object RawTransaction { get; }

    internal CrossModuleSessionHandle(object rawTransaction)
    {
        RawTransaction = rawTransaction;
    }
}
