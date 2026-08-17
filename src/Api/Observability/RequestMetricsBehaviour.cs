using System.Diagnostics;
using Mediator;

namespace ELifeRPG.Api.Observability;

/// <summary>
/// Registered once, centrally, for every Mediator request across every module — not duplicated per
/// module. An open-generic IPipelineBehavior&lt;,&gt; registration applies to every request type
/// regardless of which project registered it, so a per-module copy of this would wrap every other
/// module's requests too, double-counting metrics under the wrong module (found while wiring up the
/// Characters module — see MIGRATION.md).
/// </summary>
public sealed class RequestMetricsBehaviour<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TMessage).Name;
        var module = typeof(TMessage).Namespace?.Split('.') is [_, var moduleName, ..] ? moduleName : "Unknown";

        var stopwatch = Stopwatch.StartNew();
        using var activity = Activities.Source.StartActivity(requestName);
        activity?.SetTag("module", module);

        try
        {
            return await next(message, cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            var tags = new KeyValuePair<string, object?>[]
            {
                new("request", requestName),
                new("module", module),
            };
            Metrics.RequestCounter.Add(1, tags);
            Metrics.RequestDurationHistogram.Record(stopwatch.ElapsedMilliseconds, tags);
        }
    }
}
