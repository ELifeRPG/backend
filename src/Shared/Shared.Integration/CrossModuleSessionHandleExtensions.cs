using ELifeRPG.Shared.Integration.Abstractions;
using Npgsql;

namespace ELifeRPG.Shared.Integration;

public static class CrossModuleSessionHandleExtensions
{
    public static NpgsqlTransaction Unwrap(this CrossModuleSessionHandle handle) => (NpgsqlTransaction)handle.RawTransaction;
}
