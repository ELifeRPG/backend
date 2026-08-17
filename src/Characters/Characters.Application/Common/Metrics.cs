using System.Diagnostics.Metrics;

namespace ELifeRPG.Characters.Application.Common;

public static class Metrics
{
    public const string SourceName = "ELifeRPG.Characters";

    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<int> RequestCounter = Meter.CreateCounter<int>("character_request_counter");

    public static readonly Histogram<float> RequestDurationHistogram = Meter.CreateHistogram<float>("character_request_duration", unit: "ms");
}
