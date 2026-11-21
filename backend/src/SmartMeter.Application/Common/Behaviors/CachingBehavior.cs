using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using SmartMeter.Application.Abstractions;

namespace SmartMeter.Application.Common.Behaviors;

public sealed class CachingBehavior<TRequest, TResponse>(HybridCache cache, IAnalyticsStateProvider state)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICacheableQuery cacheable) return await next();
        var snapshot = await state.GetAsync(cancellationToken);
        var key = $"sm:v{snapshot.DataVersion}:{typeof(TRequest).Name}:{cacheable.CacheKey}";
        var duration = cacheable.CacheDuration;
        var options = new HybridCacheEntryOptions
        {
            Expiration = duration,
            LocalCacheExpiration = duration < TimeSpan.FromMinutes(2) ? duration : TimeSpan.FromMinutes(2),
        };
        return await cache.GetOrCreateAsync(key, async _ => await next(), options, cancellationToken: cancellationToken);
    }
}
