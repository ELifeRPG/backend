using System.Diagnostics;

namespace ELifeRPG.Characters.Application.Common;

public static class Activities
{
    public const string SourceName = "ELifeRPG.Characters";

    public static readonly ActivitySource Source = new(SourceName);
}
