using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace SmartMeter.Application.Common.Behaviors;

/// <summary>Records request latency (OpenTelemetry histogram) and logs slow requests.</summary>
public sealed class PerformanceBehavior<TRequest, TResponse>(SmartMeterMetrics metrics, ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private const int SlowThresholdMs = 1000;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        using var activity = SmartMeterMetrics.ActivitySource.StartActivity(name);
        var started = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
            var response = await next();
            success = true;
            return response;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            metrics.RecordRequest(name, elapsed, success);
            if (elapsed > SlowThresholdMs)
                logger.LogWarning("Slow request {Request} took {ElapsedMs:F0} ms", name, elapsed);
        }
    }
}
