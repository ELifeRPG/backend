using System.Diagnostics;

namespace ELifeRPG.Characters.Application.Common.Behaviours;

public sealed class MetricsBehaviour<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = Activities.Source.StartActivity(typeof(TMessage).Name);

        try
        {
            return await next(message, cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            Metrics.RequestCounter.Add(1, new KeyValuePair<string, object?>("request", typeof(TMessage).Name));
            Metrics.RequestDurationHistogram.Record(stopwatch.ElapsedMilliseconds, new KeyValuePair<string, object?>("request", typeof(TMessage).Name));
        }
    }
}
