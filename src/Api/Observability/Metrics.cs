using System.Diagnostics.Metrics;

namespace ELifeRPG.Api.Observability;

public static class Metrics
{
    public const string SourceName = "ELifeRPG";

    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<int> RequestCounter = Meter.CreateCounter<int>("request_counter");

    public static readonly Histogram<float> RequestDurationHistogram = Meter.CreateHistogram<float>("request_duration", unit: "ms");
}
